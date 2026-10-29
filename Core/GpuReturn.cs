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
        var results = new List<string>();
        foreach (var a in Apps.Build(LiveSwitch.Monitor!.Sample()))
        {
            if (!bumped.Contains(a.ExePath) || a.IsSystem) continue;
            if (!string.Equals(Preferences.GetGpuKey(a.ExePath), gpu.Key, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.PrimaryGpuKey == gpu.Key) continue;
            var kind = Compat.For(a.ExePath).Kind;
            try
            {
                if (kind == LiveKind.Chromium)
                {
                    Log.Write($"returning {a.ExeName}: " + await LiveSwitch.MoveChromium(a.ExePath, gpu.Key));
                    results.Add(a.DisplayName);
                    continue;
                }
                // games lose progress, launchers may be holding a game that's still starting
                if (kind == LiveKind.Game || a.ExeName.Contains("launcher", StringComparison.OrdinalIgnoreCase)
                    || gameRunning.Contains(a.ExeName, StringComparer.OrdinalIgnoreCase)) continue;
                var why = LiveSwitch.WhyNotRestart(a);
                if (why != null) { Log.Write($"not returning {a.ExeName}: {why}"); continue; }
                Log.Write($"returning {a.ExeName}: " + await LiveSwitch.RestartOnGpu(a.ExePath, gpu.Key, () => Task.FromResult(true)));
                results.Add($"{a.DisplayName} (restarted)");
            }
            catch (Exception ex) { Log.Write($"returning {a.ExeName} failed: {ex.Message}"); }
        }
        if (results.Count > 0) Notify?.Invoke($"{gpu.ShortName} is back on. Moved back to it: {string.Join(", ", results)}.");
    }
}
