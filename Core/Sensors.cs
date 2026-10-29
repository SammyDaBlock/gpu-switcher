using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GpuSwitcher.Core;

public record GpuSensors(double? Util, int? TempC, int? ClockMhz, double? PowerW, int? FanPct);

/// <summary>
/// Live numbers for the performance overlay. NVIDIA cards through NVML (nvml.dll ships with the driver),
/// CPU and RAM from Windows. Only samples while the overlay is on.
/// </summary>
public static class Sensors
{
    public static double CpuPercent { get; private set; }
    public static ulong RamUsed { get; private set; }
    public static ulong RamTotal { get; private set; }
    /// <summary>GPU key -> sensors. Cards NVML doesn't know (AMD, Intel) aren't in here.</summary>
    public static IReadOnlyDictionary<string, GpuSensors> Gpu { get; private set; } = new Dictionary<string, GpuSensors>();

    static Timer? _timer;
    static PerformanceCounter? _cpu;
    static readonly object Gate = new();

    public static void Start()
    {
        lock (Gate) _timer ??= new Timer(_ => Sample(), null, 0, 1000);
    }

    public static void Stop()
    {
        lock (Gate) { _timer?.Dispose(); _timer = null; }
    }

    public static void Sample()
    {
        lock (Gate)
        {
            try
            {
                // same counter Task Manager uses; can go over 100 with boost clocks
                _cpu ??= new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                CpuPercent = Math.Min(100, _cpu.NextValue());
            }
            catch (Exception ex) { Log.Write("cpu counter: " + ex.Message); _cpu = null; }

            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref m)) { RamTotal = m.TotalPhys; RamUsed = m.TotalPhys - m.AvailPhys; }

            Gpu = Nvml.Read();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
}

/// <summary>NVIDIA's management library. Handles go stale when a card is turned off (force mode), so any error re-inits.</summary>
static class Nvml
{
    const string Dll = "nvml.dll";
    [DllImport(Dll)] static extern int nvmlInit_v2();
    [DllImport(Dll)] static extern int nvmlShutdown();
    [DllImport(Dll)] static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Dll)] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll)] static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, out PciInfo pci);
    [DllImport(Dll)] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization u);
    [DllImport(Dll)] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Dll)] static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll)] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);

    [StructLayout(LayoutKind.Sequential)] struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string BusIdLegacy;
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
    }

    static bool _ready, _missing;
    static List<(IntPtr Handle, string Key)> _devices = new();
    static string _gpuSig = "";

    public static Dictionary<string, GpuSensors> Read()
    {
        var result = new Dictionary<string, GpuSensors>();
        if (_missing) return result;
        try
        {
            // a card was turned off or on: start over
            var sig = string.Join("|", Gpus.All.Select(g => g.Key + g.IsDisabled));
            if (sig != _gpuSig) { Reset(); _gpuSig = sig; }
            if (!_ready && !Init()) return result;

            bool failed = false;
            foreach (var (h, key) in _devices)
            {
                double? util = nvmlDeviceGetUtilizationRates(h, out var u) == 0 ? u.Gpu : null;
                if (util == null) { failed = true; continue; }
                int? temp = nvmlDeviceGetTemperature(h, 0, out var t) == 0 ? (int)t : null;
                int? clock = nvmlDeviceGetClockInfo(h, 0, out var c) == 0 ? (int)c : null;
                double? power = nvmlDeviceGetPowerUsage(h, out var p) == 0 ? p / 1000.0 : null;
                int? fan = nvmlDeviceGetFanSpeed(h, out var f) == 0 ? (int)f : null;
                result[key] = new GpuSensors(util, temp, clock, power, fan);
            }
            if (failed) Reset();
        }
        catch (DllNotFoundException) { _missing = true; } // no NVIDIA driver
        catch (EntryPointNotFoundException) { _missing = true; } // very old driver
        catch (Exception ex) { Log.Write("nvml: " + ex.Message); Reset(); }
        return result;
    }

    static bool Init()
    {
        if (nvmlInit_v2() != 0) return false;
        _ready = true;
        _devices = new();
        if (nvmlDeviceGetCount_v2(out var n) != 0) return true;
        for (uint i = 0; i < n; i++)
        {
            if (nvmlDeviceGetHandleByIndex_v2(i, out var h) != 0 || nvmlDeviceGetPciInfo_v3(h, out var pci) != 0) continue;
            // same VEN&DEV&SUBSYS key the rest of the app uses
            uint ven = pci.PciDeviceId & 0xFFFF, dev = pci.PciDeviceId >> 16;
            _devices.Add((h, $"{ven:X4}&{dev:X4}&{pci.PciSubSystemId:X8}"));
        }
        return true;
    }

    static void Reset()
    {
        if (_ready) try { nvmlShutdown(); } catch { }
        _ready = false;
        _devices = new();
    }
}
