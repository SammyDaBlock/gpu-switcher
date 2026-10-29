using System.Diagnostics;

namespace GpuSwitcher.Core;

/// <summary>
/// For games that ignore every GPU setting (Fortnite / Unreal picks the card with the most VRAM):
/// when a trigger process starts, hide the other GPU, wait until the game has created its device on the
/// right one, then bring the hidden GPU back. Only outside methods: no injection, nothing touches the game.
/// </summary>
public class ForceMode : IDisposable
{
    readonly HashSet<ForceRule> _running = new();
    public event Action<string>? Notify;

    public static ForceRule FortnitePreset(string disableGpuKey) => new()
    {
        Label = "Fortnite",
        GameExe = "FortniteClient-Win64-Shipping.exe",
        Triggers = new() { "FortniteLauncher.exe", "FortniteClient-Win64-Shipping_EAC_EOS.exe", "FortniteClient-Win64-Shipping_BE.exe", "FortniteClient-Win64-Shipping.exe" },
        DisableGpuKey = disableGpuKey,
    };

    /// <summary>
    /// Polls the process list every 100 ms (Toolhelp snapshot, under 1 ms) while any rule is on.
    /// WMI's Win32_ProcessStartTrace was tested first but delivered events ~1.5 s late, too slow for games
    /// that pick their GPU early.
    /// </summary>
    public void Start()
    {
        var t = new Thread(() =>
        {
            HashSet<int> known = Native.ProcessSnapshot().Select(p => p.Pid).ToHashSet();
            while (!_stop.IsCancellationRequested)
            {
                _stop.Token.WaitHandle.WaitOne(100);
                try
                {
                    var rules = AppSettings.Current.ForceRules.ToList();
                    if (!rules.Any(r => r.Enabled)) { known.Clear(); continue; }
                    var now = Native.ProcessSnapshot();
                    bool first = known.Count == 0;
                    foreach (var (pid, name) in now)
                    {
                        if (first || known.Contains(pid)) continue;
                        if (rules.Any(r => r.Enabled && (r.Triggers.Contains(name, StringComparer.OrdinalIgnoreCase) || r.GameExe.Equals(name, StringComparison.OrdinalIgnoreCase))))
                        {
                            try
                            {
                                using var p = Process.GetProcessById(pid);
                                Log.Write($"saw {name} start ({(DateTime.Now - p.StartTime).TotalMilliseconds:0} ms after launch)");
                            }
                            catch { }
                            OnProcessStarted(name);
                        }
                    }
                    known = now.Select(p => p.Pid).ToHashSet();
                }
                catch (Exception ex) { Log.Write("force mode watcher: " + ex.Message); }
            }
        }) { IsBackground = true, Name = "ForceModeWatcher", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    readonly CancellationTokenSource _stop = new();

    void OnProcessStarted(string name)
    {
        foreach (var rule in AppSettings.Current.ForceRules.ToList())
        {
            if (!rule.Enabled) continue;
            if (!rule.Triggers.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase))
                && !rule.GameExe.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            lock (_running)
            {
                if (_running.Contains(rule)) return;
                // already running game (e.g. rule triggered by a helper exe later) - nothing to do
                if (IsRunning(rule.GameExe) && !rule.GameExe.Equals(name, StringComparison.OrdinalIgnoreCase)) return;
                _running.Add(rule);
            }
            _ = Task.Run(() => Run(rule, name));
            return;
        }
    }

    static bool IsRunning(string exe) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length > 0;

    public async Task Run(ForceRule rule, string trigger)
    {
        var label = string.IsNullOrEmpty(rule.Label) ? rule.GameExe : rule.Label;
        try
        {
            var gpu = Gpus.ByKey(rule.DisableGpuKey);
            if (gpu == null) { Notify?.Invoke($"Force mode ({label}): GPU not found."); return; }
            var why = DeviceGuard.WhyNot(gpu);
            if (why != null) { Notify?.Invoke($"Force mode ({label}): {why}"); return; }

            // Too late? Pulling the GPU out from under a running game crashes it, so don't.
            if (GameIsOn(rule, gpu))
            {
                Notify?.Invoke($"Force mode ({label}): too late, the game already picked {gpu.ShortName}. Left it alone so it doesn't crash. Restart the game to try again.");
                return;
            }

            Log.Write($"force mode: {trigger} started, hiding {gpu.ShortName} for {label}");
            LiveSwitch.NoteGpuDisable(gpu);
            string result = "gave up waiting";
            await DeviceGuard.RunWithGpuDisabled(gpu, rule.MaxWaitSeconds, $"force mode {label}", async ct =>
            {
                result = await WaitForGpuPick(rule, gpu, ct);
            });
            lock (_running) _running.Remove(rule);
            Notify?.Invoke($"Force mode ({label}): {result}. {gpu.ShortName} is back on.");
        }
        catch (Exception ex)
        {
            Log.Write($"force mode {label}: {ex.Message}");
            Notify?.Invoke($"Force mode ({label}) failed: {ex.Message}");
        }
        finally { lock (_running) _running.Remove(rule); }
    }

    static bool GameIsOn(ForceRule rule, GpuAdapter gpu)
    {
        var pids = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(rule.GameExe))
            .Select(p => { var id = p.Id; p.Dispose(); return id; }).ToHashSet();
        if (pids.Count == 0) return false;
        var snap = LiveSwitch.Monitor!.Sample();
        return snap.Procs.Where(kv => pids.Contains(kv.Key)).Any(kv => kv.Value.Touches.Contains(gpu.Key));
    }

    static async Task<string> WaitForGpuPick(ForceRule rule, GpuAdapter hidden, CancellationToken ct)
    {
        var mon = LiveSwitch.Monitor!;
        var start = DateTime.Now;
        var lastAlive = DateTime.Now;
        DateTime? seen = null, picked = null;
        var name = Path.GetFileNameWithoutExtension(rule.GameExe);
        while (true)
        {
            await Task.Delay(500, ct);
            var pids = Process.GetProcessesByName(name).Select(p => { var id = p.Id; p.Dispose(); return id; }).ToHashSet();
            if (pids.Count > 0)
            {
                seen ??= DateTime.Now;
                var snap = mon.Sample();
                // "picked" = the game has created a D3D device on a visible GPU (it shows up in the GPU Engine
                // counters as soon as the device exists, even before it uses any VRAM)
                bool onVisible = snap.Procs.Where(kv => pids.Contains(kv.Key))
                    .Any(kv => kv.Value.Touches.Any(k => k != hidden.Key));
                if (onVisible) picked ??= DateTime.Now;
                // keep the GPU hidden a bit longer: engines may make a test device first and the real one after
                if (picked != null && DateTime.Now - picked > TimeSpan.FromSeconds(10))
                    return $"game is on {Gpus.All.FirstOrDefault(g => g.Key != hidden.Key)?.ShortName}";
            }
            else
            {
                if (seen != null) return "game closed";
                // launcher and game both gone for a while: the launch died, don't keep the GPU off
                if (rule.Triggers.Any(IsRunning)) lastAlive = DateTime.Now;
                if (DateTime.Now - lastAlive > TimeSpan.FromSeconds(5)) return "launch stopped before the game started";
                if (DateTime.Now - start > TimeSpan.FromSeconds(60)) return "game never started";
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
