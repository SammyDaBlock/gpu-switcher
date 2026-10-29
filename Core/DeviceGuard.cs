using System.Diagnostics;
using System.Text.Json;

namespace GpuSwitcher.Core;

/// <summary>
/// The only place that disables a GPU. Recovery layers, in order:
///  1. try/finally re-enable
///  2. a separate watchdog process (this exe with --watchdog) that re-enables on timeout or if we die
///  3. a marker file in ProgramData, checked on every startup (also at logon via the scheduled task)
///  4. the disable is non-persistent (CM_Disable_DevNode without CM_DISABLE_PERSIST), so a reboot restores it anyway
/// The display GPU is never disabled.
/// </summary>
public static class DeviceGuard
{
    record Marker(string OpId, string PnpId, DateTime Since);

    static readonly string MarkerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GpuSwitcher");
    static string MarkerFile => Path.Combine(MarkerDir, "disabled-gpus.json");
    const string MarkerMutex = @"Global\GpuSwitcher.Marker";

    static readonly SemaphoreSlim Busy = new(1, 1);
    static string? _activePnp;

    public static bool IsBusy => Busy.CurrentCount == 0;
    public static event Action<bool>? BusyChanged;

    static DeviceGuard()
    {
        // last-chance re-enable if the app is shutting down or crashing mid-operation
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyEnable();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => EmergencyEnable();
    }

    static void EmergencyEnable()
    {
        var pnp = _activePnp;
        if (pnp != null) Enable(pnp);
    }

    public static string? WhyNot(GpuAdapter gpu)
    {
        if (gpu.IsDisplayGpu) return $"{gpu.ShortName} has your monitors on it. Turning it off would black out every screen, so this app never does that.";
        if (gpu.PnpId == null) return $"Couldn't find the device ID for {gpu.ShortName}.";
        if (Gpus.All.Count(g => !g.IsDisabled) < 2) return "Only one GPU is active.";
        if (IsBusy) return "Another GPU operation is already running.";
        return null;
    }

    /// <summary>Disables the GPU, runs <paramref name="whileDisabled"/>, then ALWAYS re-enables it.</summary>
    public static async Task RunWithGpuDisabled(GpuAdapter gpu, int maxSeconds, string reason, Func<CancellationToken, Task> whileDisabled)
    {
        var why = WhyNot(gpu);
        if (why != null) throw new InvalidOperationException(why);
        if (!await Busy.WaitAsync(0)) throw new InvalidOperationException("Another GPU operation is already running.");
        var pnp = gpu.PnpId!;
        var opId = Guid.NewGuid().ToString("N");
        BusyChanged?.Invoke(true);
        try
        {
            AddMarker(new Marker(opId, pnp, DateTime.Now));
            StartWatchdog(opId, pnp, maxSeconds + 15);
            _activePnp = pnp;

            Log.Write($"disabling {gpu.ShortName} ({reason})");
            var sw = Stopwatch.StartNew();
            await Task.Run(() => Disable(pnp));
            Log.Write($"{gpu.ShortName} off after {sw.ElapsedMilliseconds} ms");
            Gpus.Refresh();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(maxSeconds));
            try { await whileDisabled(cts.Token); }
            catch (OperationCanceledException) { Log.Write($"{reason}: hit the {maxSeconds}s limit"); }
        }
        finally
        {
            bool ok = await Task.Run(() => Enable(pnp));
            _activePnp = null;
            if (ok) RemoveMarker(opId);
            Log.Write(ok ? $"re-enabled {gpu.ShortName}" : $"FAILED to re-enable {gpu.ShortName}, watchdog will retry");
            Gpus.Refresh();
            Busy.Release();
            BusyChanged?.Invoke(false);
        }
    }

    static void Disable(string pnp)
    {
        int cr = Native.DisableDevice(pnp);
        if (cr != Native.CR_SUCCESS)
        {
            // Polite disable got vetoed: fall back to what Device Manager does
            Log.Write($"CM_Disable_DevNode returned {cr}, using pnputil");
            RunPnputil("/disable-device", pnp);
        }
        var until = DateTime.Now.AddSeconds(10);
        while (!Native.IsDeviceDisabled(pnp) && DateTime.Now < until) Thread.Sleep(100);
        if (!Native.IsDeviceDisabled(pnp)) throw new InvalidOperationException("Windows didn't disable the GPU.");
    }

    /// <summary>Re-enable with retries. Safe to call on an enabled device.</summary>
    public static bool Enable(string pnp)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!Native.IsDeviceDisabled(pnp)) return true;
            if (attempt < 3) Native.EnableDevice(pnp);
            else RunPnputil("/enable-device", pnp); // also clears a persistent disable
            for (int i = 0; i < 20 && Native.IsDeviceDisabled(pnp); i++) Thread.Sleep(100);
        }
        return !Native.IsDeviceDisabled(pnp);
    }

    static void RunPnputil(string verb, string pnp)
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", $"{verb} \"{pnp}\"") { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch (Exception ex) { Log.Write("pnputil failed: " + ex.Message); }
    }

    // ---------- watchdog ----------
    static void StartWatchdog(string opId, string pnp, int seconds)
    {
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--watchdog", opId, pnp, seconds.ToString(), Environment.ProcessId.ToString() }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary>Entry point for the watchdog process.</summary>
    public static void RunWatchdog(string opId, string pnp, int seconds, int parentPid)
    {
        var deadline = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < deadline)
        {
            if (!ReadMarkers().Any(m => m.OpId == opId)) return; // main app finished cleanly
            if (!ProcessAlive(parentPid)) break;                // main app died: recover now
            Thread.Sleep(500);
        }
        if (ReadMarkers().Any(m => m.OpId == opId))
        {
            Log.Write($"watchdog: re-enabling {pnp}");
            if (Enable(pnp)) RemoveMarker(opId);
        }
    }

    static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // ---------- startup recovery ----------
    /// <summary>Re-enables anything a previous run left disabled. Called on every start.</summary>
    public static int RecoverLeftovers()
    {
        int n = 0;
        foreach (var m in ReadMarkers())
        {
            Log.Write($"recovery: re-enabling {m.PnpId} (disabled since {m.Since})");
            if (Enable(m.PnpId)) { RemoveMarker(m.OpId); n++; }
        }
        return n;
    }

    /// <summary>Panic button: enable every disabled display device, ours or not.</summary>
    public static int EnableAllGpus()
    {
        int n = 0;
        foreach (var g in Gpus.All.Where(g => g.PnpId != null))
            if (Native.IsDeviceDisabled(g.PnpId!) && Enable(g.PnpId!)) n++;
        n += RecoverLeftovers();
        Gpus.Refresh();
        return n;
    }

    // ---------- marker file ----------
    static T WithMarkerLock<T>(Func<T> f)
    {
        using var mx = new Mutex(false, MarkerMutex);
        bool got = false;
        try
        {
            try { got = mx.WaitOne(5000); } catch (AbandonedMutexException) { got = true; }
            return f();
        }
        finally { if (got) mx.ReleaseMutex(); }
    }

    static List<Marker> ReadMarkersUnlocked()
    {
        try { return File.Exists(MarkerFile) ? JsonSerializer.Deserialize<List<Marker>>(File.ReadAllText(MarkerFile)) ?? new() : new(); }
        catch { return new(); }
    }

    static void WriteMarkersUnlocked(List<Marker> list)
    {
        Directory.CreateDirectory(MarkerDir);
        if (list.Count == 0) { File.Delete(MarkerFile); return; }
        File.WriteAllText(MarkerFile, JsonSerializer.Serialize(list));
    }

    static List<Marker> ReadMarkers() => WithMarkerLock(ReadMarkersUnlocked);

    static void AddMarker(Marker m) => WithMarkerLock(() => { var l = ReadMarkersUnlocked(); l.Add(m); WriteMarkersUnlocked(l); return 0; });

    static void RemoveMarker(string opId) => WithMarkerLock(() => { var l = ReadMarkersUnlocked(); l.RemoveAll(x => x.OpId == opId); WriteMarkersUnlocked(l); return 0; });
}
