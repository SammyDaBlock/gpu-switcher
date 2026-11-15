using System.Diagnostics;

namespace GpuSwitcher.Core;

/// <summary>
/// Per-app "GPU acceleration off" for Chromium / Electron / CEF apps (Discord, browsers, Medal...).
/// Chromium switches to software rendering for the rest of the session once its GPU process has crashed 3 times, so
/// we end its GPU process until it does that: no restart, the app just blinks. Apps on the list get switched again
/// every time they start. Turning it back on needs a restart of the app (Chromium never goes back by itself).
/// </summary>
public static class NoGpu
{
    static readonly HashSet<string> Working = new(StringComparer.OrdinalIgnoreCase);

    static HashSet<string> List => AppSettings.Current.NoGpuApps;
    public static bool Is(string exePath) => List.Contains(exePath);

    /// <summary>The GPU process runs without a hardware GPU (software / WARP / GL disabled).</summary>
    static bool Software(string? cmd) => cmd != null && (cmd.Contains("--use-gl=disabled") || cmd.Contains("--disable-gpu")
        || cmd.Contains("swiftshader", StringComparison.OrdinalIgnoreCase) || cmd.Contains("warp", StringComparison.OrdinalIgnoreCase));

    public static async Task<string> TurnOff(string exePath)
    {
        List.Add(exePath);
        AppSettings.Current.Save();
        return await Enforce(exePath);
    }

    /// <summary>Takes it off the list and restarts the app so it gets its GPU back.</summary>
    public static async Task<string> TurnOn(string exePath, Func<Task<bool>> confirmForceClose)
    {
        List.Remove(exePath);
        AppSettings.Current.Save();
        if ((await Task.Run(() => Apps.ProcessesOf(exePath))).Count == 0) return "GPU acceleration is on again from its next start.";
        return "GPU acceleration back on. " + await LiveSwitch.RestartOnGpu(exePath, Preferences.GetGpuKey(exePath), confirmForceClose);
    }

    /// <summary>Every ~30 s: listed apps that (re)started with a hardware GPU process get switched again.</summary>
    public static void EnforceAll()
    {
        if (List.Count == 0) return;
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE CommandLine LIKE '%--type=gpu-process%'");
            var hardware = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Management.ManagementObject mo in s.Get())
                if (mo["ExecutablePath"] is string exe && List.Contains(exe) && !Software(mo["CommandLine"] as string)) hardware.Add(exe);
            foreach (var exe in hardware) _ = Task.Run(async () => Log.Write($"GPU acceleration off for {Path.GetFileName(exe)} (it started again): {await Enforce(exe)}"));
        }
        catch (Exception ex) { Log.Write("no-gpu check: " + ex.Message); }
    }

    static async Task<string> Enforce(string exePath)
    {
        lock (Working) if (!Working.Add(exePath)) return "Already switching it.";
        try
        {
            var killed = new HashSet<int>();
            for (int round = 0; round < 5; round++)
            {
                // wait for the GPU process (a fresh one after a kill)
                List<(int Pid, int ParentPid, string? CommandLine)> gpu = new();
                for (int i = 0; i < 24; i++)
                {
                    var procs = await Task.Run(() => Apps.ProcessesOf(exePath));
                    if (procs.Count == 0) return "Off. It isn't running, it gets switched to software rendering when it starts.";
                    gpu = procs.Where(p => p.CommandLine?.Contains("--type=gpu-process") == true && !killed.Contains(p.Pid)).ToList();
                    if (gpu.Count > 0) break;
                    await Task.Delay(250);
                }
                if (gpu.Count == 0) return round == 0 ? "Off. It has no GPU process right now (maybe acceleration is already off in its own settings)."
                                                      : "Off. Its GPU process didn't come back, so it isn't using a GPU.";
                if (gpu.All(p => Software(p.CommandLine)))
                {
                    Log.Write($"{Path.GetFileName(exePath)} GPU process now: {string.Join(" ", gpu[0].CommandLine!.Split(' ').Where(a => a.StartsWith("--use-") || a.StartsWith("--disable-gpu")))}");
                    return "GPU acceleration is off (software rendering). GPU Switcher switches it off again whenever it starts.";
                }
                LiveSwitch.NoteGpuProcessRestart(exePath);
                foreach (var p in gpu)
                {
                    killed.Add(p.Pid);
                    try { using var proc = Process.GetProcessById(p.Pid); proc.Kill(); } catch { }
                }
                await Task.Delay(400);
            }
            return "It kept starting its GPU process on a real GPU. Turn hardware acceleration off in its own settings instead.";
        }
        finally { lock (Working) Working.Remove(exePath); }
    }
}
