using System.Diagnostics;
using System.Management;
using HidSharp;

namespace GpuSwitcher.Core;

/// <summary>
/// Drives the 32x32 round screen on the Cooler Master Atmos II Pixel pump directly over HID (see docs/pixel-protocol.md).
/// Start() takes the pump from Cooler Master's agent (stops MasterCTRLService), Stop() gives it back.
/// Only the live-stream ("ssr") reports are ever sent: no flash, firmware or reset commands.
/// </summary>
public sealed class PixelDisplay : IDisposable
{
    const int Vid = 0x2516, Pid = 0x021C;
    public const int Size = 32;
    const string ServiceName = "MasterCTRLService";
    const int PacketLeds = 20, Packets = 28;

    /// <summary>Left inset of each LED row (rows 2..29, right inset is the same). Rows snake, first row left to right.</summary>
    static readonly int[] RowInset = { 14, 13, 11, 9, 7, 6, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 6, 7, 9, 11, 13, 14 };

    /// <summary>LED index (0-based) -> grid cell y*32+x. Count = 556.</summary>
    public static readonly int[] LedCell = BuildLedCells();

    static int[] BuildLedCells()
    {
        var cells = new List<int>();
        for (int r = 0; r < RowInset.Length; r++)
        {
            int y = r + 2, a = RowInset[r], b = Size - 1 - a;
            if (r % 2 == 0) for (int x = a; x <= b; x++) cells.Add(y * Size + x);
            else for (int x = b; x >= a; x--) cells.Add(y * Size + x);
        }
        Debug.Assert(cells.Count == 556);
        return cells.ToArray();
    }

    /// <summary>True when the grid cell has a real LED (the round mask).</summary>
    public static readonly bool[] HasLed = Enumerable.Range(0, Size * Size).Select(c => LedCell.Contains(c)).ToArray();

    readonly object _gate = new();
    byte[]? _frame;          // latest image, 32*32*3 RGB row-major, before brightness/rotation
    bool _dirty;
    Thread? _thread;
    volatile bool _running;
    HidStream? _ctl, _disp;

    /// <summary>0..100, applied in software like CMAgent does.</summary>
    public int Brightness { get => _brightness; set { _brightness = Math.Clamp(value, 0, 100); Touch(); } }
    int _brightness = 100;
    /// <summary>0, 90, 180, 270. Same meaning as MasterCTRL's rotation setting.</summary>
    public int Rotation { get => _rotation; set { _rotation = ((value / 90) & 3) * 90; Touch(); } }
    int _rotation;
    public int MaxFps { get; set; } = 15;
    public bool Connected { get; private set; }
    public event Action<bool>? ConnectedChanged;

    public bool Running => _running;

    /// <summary>Starts talking to the pump. Stop Cooler Master's agent first (StopVendorAgent) or the two fight.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "PixelDisplay", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Log.Write("Pixel display: started");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(2000);
        _thread = null;
        Close();
        Log.Write("Pixel display: stopped");
    }

    public void Dispose() => Stop();

    /// <summary>Queue a 32x32 RGB frame (3072 bytes, row-major). Cells without an LED are ignored.</summary>
    public void SendFrame(byte[] rgb)
    {
        if (rgb.Length != Size * Size * 3) throw new ArgumentException("need 32*32*3 bytes", nameof(rgb));
        lock (_gate) { _frame = rgb; _dirty = true; }
    }

    void Touch() { lock (_gate) _dirty = true; }

    void Loop()
    {
        var sinceKeepAlive = Stopwatch.StartNew();
        var buf = new byte[Packets * PacketLeds * 3];
        while (_running)
        {
            try
            {
                if (!Connected)
                {
                    // unplugged / asleep: try again every 2 s, then resend the current frame
                    if (!Open()) { for (int i = 0; i < 20 && _running; i++) Thread.Sleep(100); continue; }
                    lock (_gate) _dirty = true;
                }

                if (sinceKeepAlive.ElapsedMilliseconds >= 3000) { WriteCtl(0x02, 0x03); sinceKeepAlive.Restart(); }

                byte[]? frame = null;
                lock (_gate)
                {
                    // the pump holds the last frame as long as the keepalive keeps coming, so only send changes
                    if (_frame != null && _dirty) { frame = _frame; _dirty = false; }
                }
                if (frame != null)
                {
                    Pack(frame, buf);
                    WriteFrame(buf);
                }
                Thread.Sleep(1000 / Math.Clamp(MaxFps, 1, 15));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or UnauthorizedAccessException)
            {
                Log.Write($"Pixel display: lost device ({ex.Message}), retrying");
                Close();
            }
        }
    }

    /// <summary>Applies rotation + brightness and maps the 32x32 image to the 556-LED snake order.</summary>
    void Pack(byte[] rgb, byte[] outBuf)
    {
        int rot = Rotation, bri = Brightness;
        for (int i = 0; i < LedCell.Length; i++)
        {
            int c = LedCell[i], x = c % Size, y = c / Size;
            // grid cell (x,y) shows image pixel (sx,sy): content turned counter-clockwise by `rot`, like CMAgent
            (int sx, int sy) = rot switch
            {
                90 => (Size - 1 - y, x),
                180 => (Size - 1 - x, Size - 1 - y),
                270 => (y, Size - 1 - x),
                _ => (x, y),
            };
            int s = (sy * Size + sx) * 3, d = i * 3;
            outBuf[d] = (byte)(rgb[s] * bri / 100);
            outBuf[d + 1] = (byte)(rgb[s + 1] * bri / 100);
            outBuf[d + 2] = (byte)(rgb[s + 2] * bri / 100);
        }
    }

    void WriteFrame(byte[] leds)
    {
        var report = new byte[_disp!.Device.GetMaxOutputReportLength()];
        for (int p = 0; p < Packets; p++)
        {
            // MI_01 has no report IDs: byte 0 is Windows' "report 0", the 64 wire bytes start at byte 1
            Array.Clear(report);
            report[1] = 0x80; report[2] = 0xDD; report[3] = (byte)(p >> 8); report[4] = (byte)p;
            Buffer.BlockCopy(leds, p * PacketLeds * 3, report, 5, PacketLeds * 3);
            _disp.Write(report);
        }
    }

    void WriteCtl(byte id, byte value)
    {
        var report = new byte[_ctl!.Device.GetMaxOutputReportLength()];
        report[0] = id; report[1] = value;
        _ctl.Write(report);
    }

    bool Open()
    {
        var devs = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
        var ctl = devs.FirstOrDefault(d => d.DevicePath.Contains("mi_00", StringComparison.OrdinalIgnoreCase));
        var disp = devs.FirstOrDefault(d => d.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase));
        if (ctl == null || disp == null) return false;
        if (!ctl.TryOpen(out var c)) return false;
        if (!disp.TryOpen(out var d)) { c.Dispose(); return false; }
        c.WriteTimeout = d.WriteTimeout = 1000;
        _ctl = c; _disp = d;
        // same connect sequence CMAgent uses: hello, keepalive, live-stream mode
        WriteCtl(0x02, 0x04);
        WriteCtl(0x02, 0x03);
        WriteCtl(0x16, 0x01);
        SetConnected(true);
        Log.Write("Pixel display: connected");
        return true;
    }

    void Close()
    {
        try { _ctl?.Dispose(); } catch { }
        try { _disp?.Dispose(); } catch { }
        _ctl = _disp = null;
        SetConnected(false);
    }

    void SetConnected(bool v)
    {
        if (Connected == v) return;
        Connected = v;
        ConnectedChanged?.Invoke(v);
    }

    // ---- Cooler Master agent hand-off ----

    /// <summary>Stops MasterCTRLService (which owns CMAgent). Returns true if it was running.</summary>
    public static bool StopVendorAgent()
    {
        try
        {
            using var svc = FindService();
            if (svc == null || (string)svc["State"] != "Running") return false;
            svc.InvokeMethod("StopService", null);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000 && Process.GetProcessesByName("CMAgent").Length > 0) Thread.Sleep(200);
            Log.Write("Pixel display: stopped MasterCTRLService");
            return true;
        }
        catch (Exception ex) { Log.Write($"Pixel display: couldn't stop MasterCTRLService: {ex.Message}"); return false; }
    }

    public static void StartVendorAgent()
    {
        try
        {
            using var svc = FindService();
            svc?.InvokeMethod("StartService", null);
            Log.Write("Pixel display: restarted MasterCTRLService");
        }
        catch (Exception ex) { Log.Write($"Pixel display: couldn't restart MasterCTRLService: {ex.Message}"); }
    }

    static ManagementObject? FindService()
    {
        using var q = new ManagementObjectSearcher($"SELECT * FROM Win32_Service WHERE Name='{ServiceName}'");
        return q.Get().Cast<ManagementObject>().FirstOrDefault();
    }
}
