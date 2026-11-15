using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GpuSwitcher.Core;
using Drawing = System.Drawing;

namespace GpuSwitcher.UI;

/// <summary>
/// What the pump's 32x32 screen shows (clock, printer progress, screen mirror, GIF/image) and the hand-off with
/// Cooler Master's software. One background thread draws frames; it only wakes as often as the mode needs and only
/// sends a frame to the pump when it actually changed.
/// </summary>
public static class PixelScreen
{
    const int N = PixelDisplay.Size;
    static readonly object Gate = new();       // the drawing thread
    static readonly object EnableGate = new(); // pump on/off (slow: stops/starts a service)
    static volatile PixelDisplay? _pump;
    static volatile int _gen;                  // bumped by Apply(), the drawing thread drops caches when it changes
    static Thread? _thread;
    static volatile bool _run;
    static volatile bool _previewWanted;
    static readonly AutoResetEvent Wake = new(false);

    /// <summary>Every new frame (32*32*3 RGB, before brightness/rotation), for the tab's preview. Background thread.</summary>
    public static event Action<byte[]>? Frame;
    /// <summary>Connection or printer status changed. Background thread.</summary>
    public static event Action? Changed;
    public static event Action<string>? Notify;

    static PixelSettings S => AppSettings.Current.Pixel;
    public static bool PumpConnected => _pump?.Connected == true;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>App start: take the pump if the feature is on, or give it back if a crash left Cooler Master stopped.</summary>
    public static void Start()
    {
        if (S.Enabled) SetEnabled(true);
        else if (S.VendorStopped) Task.Run(GiveBack);
    }

    /// <summary>App exit: hand the pump back to Cooler Master (synchronously, it's quick once the thread is down).</summary>
    public static void Shutdown()
    {
        StopThread();
        lock (EnableGate) { _pump?.Stop(); _pump = null; }
        if (S.VendorStopped) GiveBack();
    }

    /// <summary>The tab's on/off switch. Slow parts (service stop/start) run in the background.</summary>
    public static void SetEnabled(bool on)
    {
        S.Enabled = on;
        AppSettings.Current.Save();
        Task.Run(() =>
        {
            lock (EnableGate)
            {
                if (S.Enabled != on) return; // switched again while we waited
                if (on && _pump == null)
                {
                    if (PixelDisplay.StopVendorAgent()) { S.VendorStopped = true; AppSettings.Current.Save(); }
                    var pump = new PixelDisplay { Brightness = S.Brightness, Rotation = S.Rotation };
                    pump.ConnectedChanged += _ => Changed?.Invoke();
                    var last = _last;
                    if (last != null) pump.SendFrame(last); // current picture right away
                    pump.Start();
                    _pump = pump;
                }
                else if (!on && _pump != null)
                {
                    _pump.Stop();
                    _pump = null;
                    if (S.VendorStopped) GiveBack();
                }
            }
            Changed?.Invoke();
            EnsureThread();
        });
    }

    static void GiveBack()
    {
        PixelDisplay.StartVendorAgent();
        S.VendorStopped = false;
        AppSettings.Current.Save();
    }

    /// <summary>The tab is open: keep drawing for the preview even while the pump is off.</summary>
    public static void SetPreview(bool visible)
    {
        _previewWanted = visible;
        EnsureThread();
    }

    /// <summary>Settings changed: redraw now.</summary>
    public static void Apply()
    {
        var pump = _pump;
        if (pump != null) { pump.Brightness = S.Brightness; pump.Rotation = S.Rotation; }
        _gen++;
        Wake.Set();
    }

    static void EnsureThread()
    {
        lock (Gate)
        {
            bool want = _pump != null || _previewWanted;
            if (want && _thread == null)
            {
                _run = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "PixelScreen", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
            else if (!want && _thread != null) StopThreadLocked();
        }
    }

    static void StopThread() { lock (Gate) StopThreadLocked(); }

    static void StopThreadLocked()
    {
        _run = false;
        Wake.Set();
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(2000);
        _thread = null;
        _printer?.Cancel(); _printer = null;
        _capture?.Dispose(); _capture = null;
    }

    // ------------------------------------------------------------------ frame loop

    static byte[]? _last;

    static void Loop()
    {
        var img = new byte[N * N * 3];
        int gen = -1;
        while (_run)
        {
            int waitMs;
            try
            {
                if (gen != _gen)
                {
                    // settings changed: reload the image, reopen the capture / printer connection, resend
                    gen = _gen;
                    _imageFrames = null;
                    _capture?.Dispose(); _capture = null;
                    _printer?.Cancel(); _printer = null;
                    _last = null;
                }
                Array.Clear(img);
                waitMs = S.Mode switch
                {
                    "Printer" => DrawPrinter(img),
                    "Mirror" => DrawMirror(img),
                    "Image" => DrawImage(img),
                    _ => DrawClock(img),
                };
                if (S.Mode != "Printer" && _printer != null) { _printer.Cancel(); _printer = null; }
                if (S.Mode != "Mirror" && _capture != null) { _capture.Dispose(); _capture = null; }

                if (waitMs >= 0 && (_last == null || !img.AsSpan().SequenceEqual(_last)))
                {
                    _last = (byte[])img.Clone();
                    _pump?.SendFrame(_last);
                    Frame?.Invoke(_last);
                }
            }
            catch (Exception ex) { Log.Write("Pixel screen: " + ex.Message); waitMs = 1000; }
            Wake.WaitOne(Math.Abs(waitMs));
        }
    }

    // ------------------------------------------------------------------ drawing helpers

    static (byte R, byte G, byte B) ParseColor(string hex)
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return (c.R, c.G, c.B);
        }
        catch { return (255, 255, 255); }
    }

    static (byte, byte, byte) Dim((byte R, byte G, byte B) c, int pct) => ((byte)(c.R * pct / 100), (byte)(c.G * pct / 100), (byte)(c.B * pct / 100));

    static void Set(byte[] img, int x, int y, (byte R, byte G, byte B) c)
    {
        if ((uint)x >= N || (uint)y >= N) return;
        int i = (y * N + x) * 3;
        img[i] = c.R; img[i + 1] = c.G; img[i + 2] = c.B;
    }

    // 3x5 digits ('0'-'9', ':'), drawn at scale 1 or 2. Rows top to bottom, bit 2 = left column.
    static readonly Dictionary<char, int[]> Font = new()
    {
        ['0'] = new[] { 7, 5, 5, 5, 7 }, ['1'] = new[] { 2, 6, 2, 2, 7 }, ['2'] = new[] { 7, 1, 7, 4, 7 },
        ['3'] = new[] { 7, 1, 7, 1, 7 }, ['4'] = new[] { 5, 5, 7, 1, 1 }, ['5'] = new[] { 7, 4, 7, 1, 7 },
        ['6'] = new[] { 7, 4, 7, 5, 7 }, ['7'] = new[] { 7, 1, 1, 2, 2 }, ['8'] = new[] { 7, 5, 7, 5, 7 },
        ['9'] = new[] { 7, 5, 7, 1, 7 }, [':'] = new[] { 0, 2, 0, 2, 0 }, ['!'] = new[] { 2, 2, 2, 0, 2 },
    };

    static int TextWidth(string s, int scale) => s.Sum(ch => (ch == ':' ? 1 : 3) * scale) + (s.Length - 1) * scale;

    /// <summary>Draws centered text with its top at y.</summary>
    static void Text(byte[] img, string s, int y, int scale, (byte, byte, byte) c)
    {
        int x = (N - TextWidth(s, scale) + 1) / 2;
        foreach (var ch in s)
        {
            if (Font.TryGetValue(ch, out var rows))
            {
                bool narrow = ch == ':';
                for (int r = 0; r < 5; r++)
                    for (int col = 0; col < 3; col++)
                    {
                        if ((rows[r] >> (2 - col) & 1) == 0) continue;
                        int cx = narrow ? 0 : col; // ':' only uses its middle column
                        if (narrow && col != 1) continue;
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                                Set(img, x + cx * scale + dx, y + r * scale + dy, c);
                    }
            }
            x += ((ch == ':' ? 1 : 3) + 1) * scale;
        }
    }

    /// <summary>Outer bands of LEDs with their clockwise angle from 12 o'clock (0..1), sorted by angle.
    /// The seconds dot uses the thin outer edge, the progress ring is ~2 LEDs thick.</summary>
    static readonly (int X, int Y, double A)[] SecRing = BuildRing(12.5), ProgRing = BuildRing(11.5);

    static (int, int, double)[] BuildRing(double minDist)
    {
        var list = new List<(int, int, double)>();
        foreach (var cell in PixelDisplay.LedCell)
        {
            int x = cell % N, y = cell / N;
            double dx = x - 15.5, dy = y - 15.5;
            if (dx * dx + dy * dy < minDist * minDist) continue;
            double a = Math.Atan2(dx, -dy) / (2 * Math.PI);
            list.Add((x, y, a < 0 ? a + 1 : a));
        }
        return list.OrderBy(r => r.Item3).ToArray();
    }

    // ------------------------------------------------------------------ clock

    // Thin rounded 4x7 digits like Cooler Master's own clock face (slashed zero included).
    static readonly Dictionary<char, string[]> Thin = new()
    {
        ['0'] = new[] { ".##.", "#..#", "#..#", "#.##", "##.#", "#..#", ".##." },
        ['1'] = new[] { "..#.", ".##.", "..#.", "..#.", "..#.", "..#.", "..#." },
        ['2'] = new[] { ".##.", "#..#", "...#", "..#.", ".#..", "#...", "####" },
        ['3'] = new[] { ".##.", "#..#", "...#", "..#.", "...#", "#..#", ".##." },
        ['4'] = new[] { "...#", "..##", ".#.#", "#..#", "####", "...#", "...#" },
        ['5'] = new[] { "####", "#...", "###.", "...#", "...#", "#..#", ".##." },
        ['6'] = new[] { ".##.", "#...", "#...", "###.", "#..#", "#..#", ".##." },
        ['7'] = new[] { "####", "...#", "...#", "..#.", ".#..", ".#..", ".#.." },
        ['8'] = new[] { ".##.", "#..#", "#..#", ".##.", "#..#", "#..#", ".##." },
        ['9'] = new[] { ".##.", "#..#", "#..#", ".###", "...#", "...#", ".##." },
        [':'] = new[] { ".", ".", "#", ".", "#", ".", "." },
    };

    // Pink heart with a light highlight, same as the hearts on Cooler Master's clock. P = pink, L = light pink.
    static readonly string[] Heart = { ".P.P.", "PPPLP", "PPPLP", ".PLP.", "..P.." };
    static readonly (byte, byte, byte) Pink = (217, 0, 134), LightPink = (216, 175, 200), BackPink = (120, 0, 75), Sparkle = (255, 225, 240);

    const int TimeTop = 13, HeartTopY = 4, HeartBottomY = 23;
    const double SpinSeconds = 2.4, SparkleMs = 1400;
    static readonly Random Rng = new();
    static readonly List<(int X, int Y, long Born)> Sparkles = new();

    /// <summary>LEDs inside the seconds ring that don't touch the time or the hearts: where sparkles may appear.</summary>
    static readonly (int X, int Y)[] SparkleSpots = PixelDisplay.LedCell
        .Select(c => (X: c % N, Y: c / N))
        .Where(p => Math.Pow(p.X - 15.5, 2) + Math.Pow(p.Y - 15.5, 2) < 11 * 11)
        .Where(p => p.Y < TimeTop - 2 || p.Y > TimeTop + 8)                                  // clear of the time
        .Where(p => p.X < 11 || p.X > 19 || (p.Y > HeartTopY + 6 && p.Y < HeartBottomY - 2))  // clear of the hearts
        .ToArray();

    /// <summary>Cute clock: time on one line, a spinning heart above and below, twinkling sparkles, seconds dot on the edge.</summary>
    static int DrawClock(byte[] img)
    {
        var now = DateTime.Now;
        long ms = Environment.TickCount64;
        var c = ParseColor(S.Color);

        // sparkles first, so nothing else ever gets drawn over by one
        Sparkles.RemoveAll(s => ms - s.Born > SparkleMs);
        if (Sparkles.Count < 3 && Rng.Next(4) == 0 && SparkleSpots.Length > 0)
        {
            var p = SparkleSpots[Rng.Next(SparkleSpots.Length)];
            if (Sparkles.All(s => Math.Abs(s.X - p.X) + Math.Abs(s.Y - p.Y) > 6)) Sparkles.Add((p.X, p.Y, ms));
        }
        foreach (var s in Sparkles)
        {
            // fade in, flash into a little + star, fade out
            double t = (ms - s.Born) / SparkleMs, glow = Math.Sin(t * Math.PI);
            Set(img, s.X, s.Y, Dim(Sparkle, (int)(100 * glow)));
            if (glow > 0.55)
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    Set(img, s.X + dx, s.Y + dy, Dim(Sparkle, (int)(90 * (glow - 0.45))));
        }

        int h = S.Clock24h ? now.Hour : (now.Hour + 11) % 12 + 1;
        int x = (N - (4 * 4 + 1 + 4)) / 2;
        foreach (var ch in $"{h:00}:{now.Minute:00}")
        {
            var rows = Thin[ch];
            for (int r = 0; r < 7; r++)
                for (int col = 0; col < rows[r].Length; col++)
                    if (rows[r][col] == '#') Set(img, x + col, TimeTop + r, c);
            x += rows[0].Length + 1;
        }

        double spin = ms / 1000.0 / SpinSeconds * 2 * Math.PI;
        SpinningHeart(img, HeartTopY, spin);
        SpinningHeart(img, HeartBottomY, spin + Math.PI);

        if (S.ClockSeconds)
        {
            // a dot running around the edge once a minute, with a short fading tail
            double a = (now.Second + now.Millisecond / 1000.0) / 60;
            int head = Array.FindLastIndex(SecRing, r => r.A <= a);
            if (head < 0) head = SecRing.Length - 1;
            for (int t = 0; t < 4; t++)
            {
                var r = SecRing[(head - t + SecRing.Length) % SecRing.Length];
                Set(img, r.X, r.Y, Dim(c, 100 - t * 30));
            }
        }
        return 125; // ~8 fps, same as Cooler Master's own clock
    }

    /// <summary>The heart turning like a coin: squashed sideways by cos(angle), back side darker and mirrored.</summary>
    static void SpinningHeart(byte[] img, int top, double angle)
    {
        double s = Math.Cos(angle), w = Math.Abs(s);
        bool back = s < 0;
        for (int dx = -2; dx <= 2; dx++)
        {
            int src = w < 0.2 ? (dx == 0 ? 0 : 99) : (int)Math.Round(dx / w); // edge-on: a 1 pixel sliver
            if (Math.Abs(src) > 2) continue;
            int col = 2 + (back ? -src : src);
            for (int r = 0; r < 5; r++)
            {
                char p = Heart[r][col];
                if (p == '.') continue;
                Set(img, 15 + dx, top + r, back ? BackPink : p == 'L' ? LightPink : Pink);
            }
        }
    }

    // ------------------------------------------------------------------ 3D printer

    sealed record PrinterState(bool Connected, string State, int Percent, int Remaining, string Error);
    static PrinterState? _ps;
    static string? _psPrevState;
    static DateTime _flashUntil, _doneAt;
    static CancellationTokenSource? _printer;
    public static string PrinterStatus { get; private set; } = "";

    static int DrawPrinter(byte[] img)
    {
        if (_printer == null) { _printer = new CancellationTokenSource(); var tok = _printer.Token; Task.Run(() => PrinterLoop(tok)); }
        var ps = _ps;
        bool failed = ps != null && (ps.State == "FAILED" || ps.Error != "");
        bool done = ps?.State == "FINISH";
        if (ps != null && _psPrevState != null && _psPrevState != ps.State && (done || failed))
        {
            _flashUntil = DateTime.Now.AddMinutes(2); // flash for 2 minutes when a print finishes or fails
            if (done) _doneAt = DateTime.Now;
        }
        if (ps != null) _psPrevState = ps.State;

        // nothing printing (or finished a while ago, the A1 stays "finished" until the next print): be a clock
        if (ps == null || !ps.Connected || ps.State is "IDLE" or "OFFLINE" or "" || (done && DateTime.Now - _doneAt > TimeSpan.FromMinutes(10)))
            return DrawClock(img);
        bool flashing = DateTime.Now < _flashUntil;
        if (flashing && DateTime.Now.Millisecond >= 500) return 500 - DateTime.Now.Millisecond % 500; // blank half

        var main = failed ? ((byte)255, (byte)40, (byte)40) : done ? ((byte)40, (byte)220, (byte)90)
            : ps.State == "PAUSE" ? ((byte)255, (byte)170, (byte)0) : ParseColor(S.Color);
        double p = done ? 1 : Math.Clamp(ps.Percent / 100.0, 0, 1);
        foreach (var r in ProgRing) Set(img, r.X, r.Y, r.A < p ? main : Dim(main, 12));

        if (failed) Text(img, "!", 8, 3, main);
        else if (done) Check(img, main);
        else
        {
            Text(img, Math.Min(ps.Percent, 99).ToString(), 7, 2, main);
            Text(img, $"{ps.Remaining / 60}:{ps.Remaining % 60:00}", 20, 1, Dim(main, 70)); // h:mm left
        }
        return flashing ? 500 - DateTime.Now.Millisecond % 500 : 1000;
    }

    static void Check(byte[] img, (byte, byte, byte) c)
    {
        // a thick check mark in the middle
        int[] xs = { 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22 };
        int[] ys = { 15, 16, 17, 18, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10 };
        for (int i = 0; i < xs.Length; i++) { Set(img, xs[i], ys[i], c); Set(img, xs[i], ys[i] + 1, c); Set(img, xs[i], ys[i] + 2, c); }
    }

    /// <summary>Listens to the print app's TV websocket; it pushes the printer state whenever it changes.</summary>
    static async Task PrinterLoop(CancellationToken tok)
    {
        var buf = new byte[1 << 16];
        while (!tok.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(S.PrinterKey) || string.IsNullOrWhiteSpace(S.PrinterUrl)) { SetPrinterStatus("Paste the print app's TV link below."); await Task.Delay(3000, tok); continue; }
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(new Uri($"{S.PrinterUrl}?key={Uri.EscapeDataString(S.PrinterKey)}"), tok);
                SetPrinterStatus("Connected to the print app.");
                var msg = new MemoryStream();
                while (ws.State == WebSocketState.Open && !tok.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, tok);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    msg.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    OnPrinterMessage(msg.ToArray());
                    msg.SetLength(0);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { SetPrinterStatus($"Can't reach the print app ({ex.Message}). Retrying..."); }
            _ps = null;
            try { await Task.Delay(10000, tok); } catch { return; }
        }
    }

    static void OnPrinterMessage(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var t) || t.GetString() != "state") return;
        var d = root.GetProperty("data");
        string Str(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int Int(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var ps = new PrinterState(d.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
            Str("state"), Int("percent"), Int("remaining"), Str("print_error"));
        bool changed = _ps == null || _ps.State != ps.State;
        _ps = ps;
        if (changed) SetPrinterStatus(!ps.Connected ? "Print app is up, printer is offline." : $"Printer: {ps.State.ToLowerInvariant()}");
        Wake.Set();
    }

    static void SetPrinterStatus(string s)
    {
        if (PrinterStatus == s) return;
        PrinterStatus = s;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ screen mirror

    static ScreenCapture? _capture;

    static int DrawMirror(byte[] img)
    {
        if (_capture == null)
        {
            var mons = Overlays.Monitors();
            var m = mons.FirstOrDefault(x => x.Device == S.MirrorMonitor);
            if (m.Device == null) m = mons.FirstOrDefault();
            // capture on the GPU the monitors are plugged into (the 5070), whatever GPU this app is set to
            _capture = new ScreenCapture((m.Bounds.X, m.Bounds.Y, m.Bounds.W, m.Bounds.H), Gpus.DisplayGpu?.Luids.ToArray() ?? Array.Empty<long>());
        }
        // -66: nothing new on screen, keep the last frame and check again in a frame's time
        return _capture.Grab(S.MirrorRegion, img) ? 66 : -66;
    }

    // ------------------------------------------------------------------ GIF / image

    static List<(byte[] Rgb, int Ms)>? _imageFrames;
    static int _imageIndex;

    static int DrawImage(byte[] img)
    {
        if (_imageFrames == null) { _imageFrames = LoadImage(S.ImagePath); _imageIndex = 0; }
        var frames = _imageFrames;
        if (frames.Count == 0) { Text(img, "!", 8, 3, ParseColor(S.Color)); return 1000; }
        var (rgb, ms) = frames[_imageIndex++ % frames.Count];
        rgb.CopyTo(img, 0);
        return frames.Count == 1 ? 5000 : ms;
    }

    /// <summary>Every frame of the file, cropped to the middle square and scaled to 32x32 once, so playback costs nothing.</summary>
    static List<(byte[], int)> LoadImage(string? path)
    {
        var list = new List<(byte[], int)>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;
        try
        {
            using var src = Drawing.Image.FromFile(path);
            var dim = Drawing.Imaging.FrameDimension.Time;
            int count = src.FrameDimensionsList.Contains(dim.Guid) ? src.GetFrameCount(dim) : 1;
            var delays = src.PropertyIdList.Contains(0x5100) ? src.GetPropertyItem(0x5100)!.Value : null;
            int side = Math.Min(src.Width, src.Height);
            var crop = new Drawing.Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
            for (int f = 0; f < Math.Min(count, 2000); f++)
            {
                if (count > 1) src.SelectActiveFrame(dim, f);
                using var bmp = new Drawing.Bitmap(N, N, Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(Drawing.Color.Black);
                    g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Drawing.Rectangle(0, 0, N, N), crop, Drawing.GraphicsUnit.Pixel);
                }
                var rgb = new byte[N * N * 3];
                var data = bmp.LockBits(new Drawing.Rectangle(0, 0, N, N), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[N * 3];
                    for (int y = 0; y < N; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        for (int x = 0; x < N; x++) { int i = (y * N + x) * 3; rgb[i] = row[x * 3 + 2]; rgb[i + 1] = row[x * 3 + 1]; rgb[i + 2] = row[x * 3]; }
                    }
                }
                finally { bmp.UnlockBits(data); }
                // GIF delays are in 1/100 s; 0 means "as fast as possible", browsers use 100 ms. Pump max is 15 fps.
                int ms = delays != null && delays.Length >= (f + 1) * 4 ? BitConverter.ToInt32(delays, f * 4) * 10 : 100;
                list.Add((rgb, Math.Max(ms == 0 ? 100 : ms, 67)));
            }
            Log.Write($"Pixel screen: loaded {Path.GetFileName(path)} ({list.Count} frame{(list.Count == 1 ? "" : "s")})");
        }
        catch (Exception ex) { Notify?.Invoke($"Couldn't open that image: {ex.Message}"); }
        return list;
    }
}
