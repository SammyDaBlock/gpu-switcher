using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GpuSwitcher.Core;

public class ProcGpuUse
{
    public int Pid { get; init; }
    /// <summary>Per physical GPU key: utilization % (busiest engine type).</summary>
    public Dictionary<string, double> Util { get; } = new();
    /// <summary>Per physical GPU key: dedicated VRAM bytes.</summary>
    public Dictionary<string, long> Mem { get; } = new();
    /// <summary>GPU keys this process has any engine instance on.</summary>
    public HashSet<string> Touches { get; } = new();
}

public class GpuSnapshot
{
    public DateTime Time { get; init; } = DateTime.Now;
    public Dictionary<int, ProcGpuUse> Procs { get; } = new();
    public Dictionary<string, double> GpuUtil { get; } = new();
    public Dictionary<string, long> GpuMem { get; } = new();
}

/// <summary>
/// Reads the "GPU Engine" and "GPU Process Memory" counters (same data Task Manager uses).
/// Instance names look like pid_1234_luid_0x00000000_0x0001A754_phys_0_eng_3_engtype_3D.
/// </summary>
public class GpuMonitor : IDisposable
{
    static readonly Regex EngineRx = new(@"^pid_(\d+)_luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_\d+_eng_\d+_engtype_(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MemRx = new(@"^pid_(\d+)_luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly PerformanceCounterCategory _engine = new("GPU Engine");
    readonly PerformanceCounterCategory _memory = new("GPU Process Memory");
    Dictionary<string, CounterSample> _prev = new();
    readonly CancellationTokenSource _cts = new();

    public GpuSnapshot Latest { get; private set; } = new();
    public event Action<GpuSnapshot>? Updated;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1.5);

    public void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "GpuMonitor", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    void Loop()
    {
        int tick = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // DXGI LUIDs change when a card is re-enabled, so refresh the GPU list now and then
                if (tick++ % 4 == 0) Gpus.Refresh();
                var snap = Sample();
                Latest = snap;
                Updated?.Invoke(snap);
            }
            catch (Exception ex) { Log.Write("monitor: " + ex.Message); }
            _cts.Token.WaitHandle.WaitOne(Interval);
        }
    }

    readonly object _sampleGate = new();

    public GpuSnapshot Sample()
    {
        lock (_sampleGate) return SampleUnlocked();
    }

    GpuSnapshot SampleUnlocked()
    {
        var snap = new GpuSnapshot();
        var gpus = Gpus.All;
        string? KeyFor(string hi, string lo)
        {
            long luid = (Convert.ToInt64(hi, 16) << 32) | Convert.ToInt64(lo, 16);
            return gpus.FirstOrDefault(g => g.Luids.Contains(luid))?.Key;
        }

        // ---- utilization ----
        var cur = new Dictionary<string, CounterSample>();
        // (pid, gpu, engtype) -> util
        var perEng = new Dictionary<(int, string, string), double>();
        try
        {
            var data = _engine.ReadCategory();
            var util = data["Utilization Percentage"];
            if (util != null)
            {
                foreach (InstanceData inst in util.Values)
                {
                    var m = EngineRx.Match(inst.InstanceName);
                    if (!m.Success) continue;
                    var key = KeyFor(m.Groups[2].Value, m.Groups[3].Value);
                    if (key == null) continue;
                    int pid = int.Parse(m.Groups[1].Value);
                    cur[inst.InstanceName] = inst.Sample;
                    double v = 0;
                    if (_prev.TryGetValue(inst.InstanceName, out var p)) v = CounterSample.Calculate(p, inst.Sample);
                    var k = (pid, key, m.Groups[4].Value);
                    perEng[k] = perEng.GetValueOrDefault(k) + v;
                    Proc(snap, pid).Touches.Add(key);
                }
            }
        }
        catch (Exception ex) { Log.Write("engine counters: " + ex.Message); }
        _prev = cur;

        foreach (var ((pid, key, _), v) in perEng)
        {
            var pu = Proc(snap, pid);
            pu.Util[key] = Math.Max(pu.Util.GetValueOrDefault(key), v);
        }
        // GPU total = busiest engine type summed over processes (like Task Manager)
        foreach (var grp in perEng.GroupBy(kv => (kv.Key.Item2, kv.Key.Item3)))
        {
            var key = grp.Key.Item1;
            snap.GpuUtil[key] = Math.Min(100, Math.Max(snap.GpuUtil.GetValueOrDefault(key), grp.Sum(x => x.Value)));
        }

        // ---- dedicated memory ----
        try
        {
            var data = _memory.ReadCategory();
            var ded = data["Dedicated Usage"];
            if (ded != null)
            {
                foreach (InstanceData inst in ded.Values)
                {
                    var m = MemRx.Match(inst.InstanceName);
                    if (!m.Success) continue;
                    var key = KeyFor(m.Groups[2].Value, m.Groups[3].Value);
                    if (key == null) continue;
                    int pid = int.Parse(m.Groups[1].Value);
                    var pu = Proc(snap, pid);
                    pu.Mem[key] = pu.Mem.GetValueOrDefault(key) + inst.RawValue;
                    snap.GpuMem[key] = snap.GpuMem.GetValueOrDefault(key) + inst.RawValue;
                }
            }
        }
        catch (Exception ex) { Log.Write("memory counters: " + ex.Message); }

        return snap;
    }

    static ProcGpuUse Proc(GpuSnapshot s, int pid)
    {
        if (!s.Procs.TryGetValue(pid, out var p)) s.Procs[pid] = p = new ProcGpuUse { Pid = pid };
        return p;
    }

    public void Dispose() => _cts.Cancel();
}
