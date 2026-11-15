namespace GpuSwitcher.Core;

/// <summary>
/// When a GPU comes back on (force mode, a script, Device Manager...), apps that were bumped off it
/// and belong on it go back: Chromium apps live, other apps get restarted. Games are never restarted.
/// Watches GPU state from the monitor, so it doesn't matter who turned the GPU off.
/// </summary>
public static class GpuReturn
{
    // gpu key -> exe -> last time we saw it on that GPU
    static readonly Dictionary<string, Dictionary<string, DateTime>> LastOn = new();
    // gpu key -> apps that were on it right before it went off
    static readonly Dictionary<string, HashSet<string>> Bumped = new();
    static readonly HashSet<string> SkipNext = new(StringComparer.OrdinalIgnoreCase);

    public static event Action<string>? Notify;

    /// <summary>"Move everything off" wants apps to stay off: don't bring them back after this one.</summary>
    public static void SkipNextReturn(string gpuKey) { lock (SkipNext) SkipNext.Add(gpuKey); }

    /// <summary>Called with every monitor update.</summary>
    public static void OnApps(IEnumerable<RunningApp> apps)
    {
        var now = DateTime.Now;
        foreach (var g in Gpus.All)
        {
            if (!LastOn.TryGetValue(g.Key, out var seen)) LastOn[g.Key] = seen = new(StringComparer.OrdinalIgnoreCase);
            if (g.IsDisabled)
            {
                if (Bumped.ContainsKey(g.Key)) continue;
                // the app list stops showing the GPU before Gpus notices it's off, so look back a bit
                Bumped[g.Key] = seen.Where(kv => now - kv.Value < TimeSpan.FromSeconds(20)).Select(kv => kv.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // ours already counted this for Chromium apps (DeviceGuard is busy the whole time it's off)
                if (!DeviceGuard.IsBusy)
                    foreach (var exe in Bumped[g.Key].Where(e => Compat.For(e).Kind == LiveKind.Chromium)) LiveSwitch.NoteGpuProcessRestart(exe);
                Log.Write($"{g.ShortName} went off, {Bumped[g.Key].Count} apps were on it");
                continue;
            }
            foreach (var a in apps.Where(a => !a.IsSystem && a.Touches.Contains(g.Key))) seen[a.ExePath] = now;
            if (Bumped.Remove(g.Key, out var bumped))
            {
                bool skip;
                lock (SkipNext) skip = SkipNext.Remove(g.Key);
                if (!skip && AppSettings.Current.ReturnAppsWhenGpuBack && bumped.Count > 0) _ = Task.Run(() => ReturnApps(g, bumped));
            }
        }
    }

    static async Task ReturnApps(GpuAdapter gpu, HashSet<string> bumped)
    {
        await Task.Delay(4000); // let the re-enabled card settle and the counters see its new LUID
        if (DeviceGuard.IsBusy || Gpus.ByKey(gpu.Key)?.IsDisabled != false) return;
        var gameRunning = AppSettings.Current.ForceRules.SelectMany(r => r.Triggers.Append(r.GameExe)).ToList();
        var tasks = new List<Task<(string Name, bool Ok, bool Retry)>>();
        foreach (var a in Apps.Build(LiveSwitch.Monitor!.Sample()))
        {
            if (!bumped.Contains(a.ExePath) || a.IsSystem) continue;
            if (string.Equals(a.ExePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue; // never restart ourselves
            if (!string.Equals(Preferences.GetGpuKey(a.ExePath), gpu.Key, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.PrimaryGpuKey == gpu.Key) continue;
            // anti-cheat games and their in-game overlay stay where the game put them
            if (AntiCheat.Is(a.ExePath) || a.ExeName.StartsWith("EOSOverlayRenderer", StringComparison.OrdinalIgnoreCase)) continue;
            if (NoGpu.Is(a.ExePath)) continue; // GPU acceleration is off for it, nothing to move
            var kind = Compat.For(a.ExePath).Kind;
            if (kind != LiveKind.Chromium)
            {
                // games lose progress, launchers may be holding a game that's still starting
                if (kind == LiveKind.Game || a.ExeName.Contains("launcher", StringComparison.OrdinalIgnoreCase)
                    || gameRunning.Contains(a.ExeName, StringComparer.OrdinalIgnoreCase)) continue;
                var why = LiveSwitch.WhyNotRestart(a);
                if (why != null) { Log.Write($"not returning {a.ExeName}: {why}"); continue; }
            }
            // all at once: one by one took over a minute (restarts are up to ~9 s each)
            tasks.Add(Task.Run(async () =>
            {
                string result;
                try
                {
                    result = kind == LiveKind.Chromium
                        ? await LiveSwitch.MoveChromium(a.ExePath, gpu.Key)
                        : await LiveSwitch.RestartOnGpu(a.ExePath, gpu.Key, () => Task.FromResult(true));
                }
                catch (Exception ex) { result = ex.Message; }
                Log.Write($"returning {a.ExeName}: {result}");
                bool ok = LiveSwitch.Worked(result), retry = !ok && kind == LiveKind.Chromium;
                if (retry) _ = Task.Run(() => RetryChromium(a, gpu));
                return (Name: kind == LiveKind.Chromium ? a.DisplayName : $"{a.DisplayName} (restarted)", Ok: ok, Retry: retry);
            }));
        }
        var results = await Task.WhenAll(tasks);
        if (results.Length == 0) return;
        string Names(IEnumerable<string> names) => string.Join(", ", names);
        var moved = results.Where(r => r.Ok).Select(r => r.Name).ToList();
        var retrying = results.Where(r => r.Retry).Select(r => r.Name).ToList();
        var failed = results.Where(r => !r.Ok && !r.Retry).Select(r => r.Name).ToList();
        Notify?.Invoke($"{gpu.ShortName} is back on." +
            (moved.Count > 0 ? $" Moved back to it: {Names(moved)}." : "") +
            (retrying.Count > 0 ? $" Landed on the wrong GPU, trying again in a few minutes: {Names(retrying)}." : "") +
            (failed.Count > 0 ? $" Couldn't move (see the log): {Names(failed)}." : ""));
    }

    /// <summary>
    /// A Chromium app's new GPU process landed on the wrong card. Every GPU process restart counts towards Chromium's
    /// "3 crashes and acceleration is off" limit, so wait until another one is safe, then try once more.
    /// </summary>
    static async Task RetryChromium(RunningApp a, GpuAdapter gpu)
    {
        await Task.Delay(LiveSwitch.WaitBeforeRestart(a.ExePath) + TimeSpan.FromSeconds(5));
        if (DeviceGuard.IsBusy || Gpus.ByKey(gpu.Key)?.IsDisabled != false) return;
        var now = Apps.Build(LiveSwitch.Monitor!.Sample()).FirstOrDefault(x => string.Equals(x.ExePath, a.ExePath, StringComparison.OrdinalIgnoreCase));
        if (now == null || now.PrimaryGpuKey == gpu.Key) return; // closed, or got there by itself
        if (!string.Equals(Preferences.GetGpuKey(a.ExePath), gpu.Key, StringComparison.OrdinalIgnoreCase)) return; // you changed it meanwhile
        string result;
        try { result = await LiveSwitch.MoveChromium(a.ExePath, gpu.Key); }
        catch (Exception ex) { result = ex.Message; }
        Log.Write($"returning {a.ExeName} (second try): {result}");
        Notify?.Invoke(LiveSwitch.Worked(result) ? $"{a.DisplayName} is back on {gpu.ShortName} now." : $"{a.DisplayName} still didn't move to {gpu.ShortName}: {result}");
    }
}
