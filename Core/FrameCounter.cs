using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace GpuSwitcher.Core;

public record FrameStats(double Fps, double FrameMs, double Low1Fps);

/// <summary>
/// Counts frames per process from the "Present" events DirectX logs to ETW (the same source PresentMon uses).
/// Nothing is injected into games. Covers DirectX 9-12; OpenGL / Vulkan games don't log these, so they get no FPS.
/// </summary>
public static class FrameCounter
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    const int DxgiPresentStart = 42, D3D9PresentStart = 1;
    const string SessionName = "GpuSwitcher-Frames";
    const double KeepMs = 5000;

    static TraceEventSession? _session;
    /// <summary>pid -> present times (ms since the session started), oldest first.</summary>
    static readonly Dictionary<int, Queue<double>> Frames = new();
    static readonly object Gate = new();
    static double _latest;
    static DateTime _latestAt;

    public static bool Running => _session != null;

    public static void Start()
    {
        lock (Gate)
        {
            if (_session != null) return;
            try
            {
                // same name as a session a crashed run left behind: that one gets replaced
                var s = new TraceEventSession(SessionName) { StopOnDispose = true };
                s.EnableProvider(Dxgi, TraceEventLevel.Verbose, ulong.MaxValue, new TraceEventProviderOptions { EventIDsToEnable = new List<int> { DxgiPresentStart } });
                s.EnableProvider(D3D9, TraceEventLevel.Verbose, ulong.MaxValue, new TraceEventProviderOptions { EventIDsToEnable = new List<int> { D3D9PresentStart } });
                s.Source.AllEvents += OnEvent;
                _session = s;
                new Thread(() =>
                {
                    try { s.Source.Process(); }
                    catch (Exception ex) { Log.Write("frame counter: " + ex.Message); }
                }) { IsBackground = true, Name = "FrameCounter" }.Start();
            }
            catch (Exception ex) { Log.Write("frame counter couldn't start: " + ex.Message); _session = null; }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            Frames.Clear();
        }
    }

    static void OnEvent(TraceEvent e)
    {
        bool present = (int)e.ID == DxgiPresentStart && e.ProviderGuid == Dxgi || (int)e.ID == D3D9PresentStart && e.ProviderGuid == D3D9;
        if (!present) return;
        double t = e.TimeStampRelativeMSec;
        lock (Gate)
        {
            if (!Frames.TryGetValue(e.ProcessID, out var q)) Frames[e.ProcessID] = q = new Queue<double>();
            q.Enqueue(t);
            while (q.Count > 0 && q.Peek() < t - KeepMs) q.Dequeue();
            if (t > _latest) { _latest = t; _latestAt = DateTime.UtcNow; }
        }
    }

    /// <summary>Null when the process hasn't presented anything lately.</summary>
    public static FrameStats? For(int pid)
    {
        lock (Gate)
        {
            // events arrive in ~1 s batches, so "now" is the newest event seen plus the time since it arrived
            double now = _latest + (DateTime.UtcNow - _latestAt).TotalMilliseconds;
            foreach (var dead in Frames.Where(kv => kv.Value.Count == 0 || kv.Value.Last() < now - KeepMs).Select(kv => kv.Key).ToList()) Frames.Remove(dead);
            if (!Frames.TryGetValue(pid, out var q) || q.Count < 2) return null;
            var times = q.ToArray();
            if (times[^1] < now - 2000) return null; // stopped presenting (paused, minimized)

            double end = times[^1];
            int inLastSecond = times.Count(x => x > end - 1000);
            var gaps = new List<double>(times.Length);
            for (int i = 1; i < times.Length; i++) gaps.Add(times[i] - times[i - 1]);
            double avgMs = gaps.Skip(Math.Max(0, gaps.Count - inLastSecond)).DefaultIfEmpty(0).Average();
            gaps.Sort();
            double worst = gaps[(int)Math.Min(gaps.Count - 1, gaps.Count * 0.99)];
            return new FrameStats(inLastSecond, avgMs, worst > 0 ? 1000 / worst : 0);
        }
    }
}
