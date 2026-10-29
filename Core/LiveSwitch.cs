using System.Diagnostics;

namespace GpuSwitcher.Core;

public static class LiveSwitch
{
    // Chromium turns GPU acceleration off (--use-gl=disabled) after ~3 GPU process crashes in a couple of minutes.
    // Every kill by us AND every time its GPU gets disabled counts, so we track both and stop at 2.
    static readonly Dictionary<string, List<DateTime>> GpuProcessRestarts = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(3);
    const int MaxRestartsInWindow = 2;

    public static GpuMonitor? Monitor { get; set; }

    public static void NoteGpuProcessRestart(string exePath)
    {
        lock (GpuProcessRestarts)
        {
            if (!GpuProcessRestarts.TryGetValue(exePath, out var l)) GpuProcessRestarts[exePath] = l = new();
            l.Add(DateTime.Now);
            l.RemoveAll(t => DateTime.Now - t > RestartWindow);
        }
    }

    /// <summary>How long until it's safe to restart this app's GPU process again (zero = now).</summary>
    public static TimeSpan WaitBeforeRestart(string exePath)
    {
        lock (GpuProcessRestarts)
        {
            if (!GpuProcessRestarts.TryGetValue(exePath, out var l)) return TimeSpan.Zero;
            l.RemoveAll(t => DateTime.Now - t > RestartWindow);
            return l.Count < MaxRestartsInWindow ? TimeSpan.Zero : l[0] + RestartWindow - DateTime.Now;
        }
    }

    /// <summary>
    /// Chromium apps on this GPU will lose their GPU process when it's disabled: count that.
    /// Uses the monitor's last snapshot so it costs nothing right before a time-critical disable.
    /// </summary>
    public static void NoteGpuDisable(GpuAdapter gpu)
    {
        if (Monitor == null) return;
        foreach (var a in Apps.Build(Monitor.Latest))
            if (a.Touches.Contains(gpu.Key) && Compat.For(a.ExePath).Kind == LiveKind.Chromium) NoteGpuProcessRestart(a.ExePath);
    }

    static readonly HashSet<string> WarnedSoftware = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chromium apps whose GPU process fell back to software rendering. Each one is reported once.</summary>
    public static List<string> NewSoftwareFallbacks()
    {
        var found = new List<string>();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE CommandLine LIKE '%--type=gpu-process%'");
            foreach (System.Management.ManagementObject mo in s.Get())
            {
                var cmd = mo["CommandLine"] as string ?? "";
                var exe = mo["ExecutablePath"] as string;
                if (exe == null) continue;
                bool software = cmd.Contains("--use-gl=disabled") || cmd.Contains("swiftshader", StringComparison.OrdinalIgnoreCase);
                lock (WarnedSoftware)
                {
                    if (software && WarnedSoftware.Add(exe)) found.Add(exe);
                    if (!software) WarnedSoftware.Remove(exe);
                }
            }
        }
        catch { }
        return found;
    }

    /// <summary>
    /// Chromium/Electron/CEF: save the preference, then kill only the GPU process. The browser starts a new one,
    /// which reads the new preference. Only this app is affected.
    /// </summary>
    public static async Task<string> MoveChromium(string exePath, string targetKey)
    {
        var target = Gpus.ByKey(targetKey) ?? throw new InvalidOperationException("Unknown GPU.");
        var wait = WaitBeforeRestart(exePath);
        if (wait > TimeSpan.Zero)
            throw new InvalidOperationException($"{Path.GetFileName(exePath)} already restarted its GPU process twice in the last few minutes. " +
                $"One more and it turns GPU acceleration off, so wait {Math.Ceiling(wait.TotalMinutes)} min. (The choice is saved for next launch.)");

        Preferences.SetGpu(exePath, targetKey);
        var procs = await Task.Run(() => Apps.ProcessesOf(exePath));
        var gpuProcs = procs.Where(p => p.CommandLine?.Contains("--type=gpu-process") == true).Select(p => p.Pid).ToHashSet();
        if (gpuProcs.Count == 0)
            throw new InvalidOperationException("No GPU process found. Hardware acceleration might be off in this app.");
        Compat.MarkChromium(exePath, true);

        NoteGpuProcessRestart(exePath);
        foreach (var pid in gpuProcs)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); } catch { }
        }
        Log.Write($"restarted GPU process of {Path.GetFileName(exePath)}");

        // wait for the replacement GPU process
        int? fresh = null;
        for (int i = 0; i < 40 && fresh == null; i++)
        {
            await Task.Delay(250);
            fresh = (await Task.Run(() => Apps.ProcessesOf(exePath)))
                .Where(p => p.CommandLine?.Contains("--type=gpu-process") == true && !gpuProcs.Contains(p.Pid))
                .Select(p => (int?)p.Pid).FirstOrDefault();
        }
        if (fresh == null) return $"{Path.GetFileName(exePath)} didn't start a new GPU process yet. It'll use {target.ShortName} once it does.";

        // let the counters catch up, then verify where it landed
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            var gpu = WhereIs(fresh.Value);
            if (gpu == targetKey) return $"Moved to {target.ShortName} live.";
        }
        var landed = Gpus.ByKey(WhereIs(fresh.Value))?.ShortName;
        return landed == null
            ? $"New GPU process started, should be on {target.ShortName} (no usage yet to confirm)."
            : $"New GPU process landed on {landed}, not {target.ShortName}. This app might ignore the Windows setting.";
    }

    static string? WhereIs(int pid)
    {
        var snap = Monitor?.Sample() ?? new GpuSnapshot();
        if (!snap.Procs.TryGetValue(pid, out var u)) return null;
        return u.Mem.Where(kv => kv.Value > 4 << 20).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
    }

    public record EvacuationResult(string App, string Exe, string Outcome);

    /// <summary>
    /// Briefly disables a (non-display) GPU so everything on it gets a device-lost and moves to the other GPU.
    /// Results are saved as compatibility data for this PC.
    /// </summary>
    public static async Task<List<EvacuationResult>> MoveEverythingOff(GpuAdapter gpu)
    {
        var mon = Monitor ?? throw new InvalidOperationException("Monitor not running.");
        var before = Apps.Build(mon.Sample()).Where(a => a.Touches.Contains(gpu.Key) && !a.IsSystem).ToList();

        NoteGpuDisable(gpu);
        GpuReturn.SkipNextReturn(gpu.Key);
        await DeviceGuard.RunWithGpuDisabled(gpu, 20, "move everything off", ct => Task.Delay(4000, ct));
        await Task.Delay(3000);

        var nowApps = Apps.Build(mon.Sample());
        var results = new List<EvacuationResult>();
        foreach (var a in before)
        {
            bool alive = a.Pids.Any(ProcessAlive);
            var now = nowApps.FirstOrDefault(n => string.Equals(n.ExePath, a.ExePath, StringComparison.OrdinalIgnoreCase));
            string outcome;
            var kind = Compat.For(a.ExePath).Kind;
            if (kind == LiveKind.Chromium) outcome = "Restarted its GPU process (normal for Chromium apps)";
            else if (alive)
            {
                outcome = $"Survived, now on {Gpus.ByKey(now?.PrimaryGpuKey)?.ShortName ?? "?"}";
                AppSettings.Current.CompatOverrides[a.ExeName] = nameof(LiveKind.SurvivesDeviceLoss);
            }
            else
            {
                outcome = now != null ? "Crashed, but got restarted" : "Crashed / closed";
                AppSettings.Current.CompatOverrides[a.ExeName] = nameof(LiveKind.CrashesOnDeviceLoss);
            }
            results.Add(new(a.DisplayName, a.ExeName, outcome));
        }
        AppSettings.Current.Save();
        return results;
    }

    static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    /// <summary>
    /// Saves the preference, closes the app (gracefully first), and relaunches it as the normal user with the same command line.
    /// <paramref name="confirmForceClose"/> is asked if it won't close by itself.
    /// </summary>
    public static async Task<string> RestartOnGpu(string exePath, string? targetKey, Func<Task<bool>> confirmForceClose)
    {
        Preferences.SetGpu(exePath, targetKey);
        var procs = await Task.Run(() => Apps.ProcessesOf(exePath));
        if (procs.Count == 0) return "It isn't running. The new setting applies next time it starts.";
        var pids = procs.Select(p => p.Pid).ToHashSet();
        var root = procs.FirstOrDefault(p => !pids.Contains(p.ParentPid));
        var cmd = root.CommandLine;

        foreach (var pid in pids)
        {
            try { using var p = Process.GetProcessById(pid); p.CloseMainWindow(); } catch { }
        }
        if (!await WaitAllExit(pids, 8000))
        {
            if (!await confirmForceClose()) return "Cancelled. The setting is saved for next launch.";
            foreach (var pid in pids)
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(true); } catch { }
            }
            if (!await WaitAllExit(pids, 8000)) return "Couldn't close it. The setting is saved for next launch.";
        }

        await Task.Delay(800);
        try
        {
            Native.LaunchUnelevated(exePath, cmd, Path.GetDirectoryName(exePath));
            Log.Write($"relaunched {Path.GetFileName(exePath)}");
            return $"Restarted on {Gpus.ByKey(targetKey)?.ShortName ?? "Auto"}.";
        }
        catch (Exception ex)
        {
            Log.Write($"relaunch {Path.GetFileName(exePath)} failed: {ex.Message}");
            return $"Closed it, but relaunching failed ({ex.Message}). Start it yourself, it'll use the new GPU.";
        }
    }

    public static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
        { "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wezterm-gui.exe", "alacritty.exe" };

    /// <summary>Why this app must not be restarted automatically, or null if it's fine to.</summary>
    public static string? WhyNotRestart(RunningApp a)
    {
        if (Terminals.Contains(a.ExeName)) return "terminal, restarting it would close everything running in it";
        if (a.ExePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return "Store app, can't be relaunched from here";
        if (IsService(a)) return "Windows service";
        return null;
    }

    static bool IsService(RunningApp a) => a.Pids.All(pid =>
    {
        try { using var p = Process.GetProcessById(pid); return p.SessionId == 0; } catch { return false; }
    });

    static async Task<bool> WaitAllExit(HashSet<int> pids, int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            if (!pids.Any(ProcessAlive)) return true;
            await Task.Delay(250);
        }
        return !pids.Any(ProcessAlive);
    }
}
