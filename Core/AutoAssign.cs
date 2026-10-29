namespace GpuSwitcher.Core;

/// <summary>
/// "Everything on GPU A, games on GPU B": gives every app that has no GPU choice yet a default.
/// Never touches apps that already have a choice or that the user changed by hand.
/// </summary>
public static class AutoAssign
{
    static readonly HashSet<string> SeenThisSession = new(StringComparer.OrdinalIgnoreCase);

    // Leave these on whatever Windows picks
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "GpuSwitcher.exe", "nvcontainer.exe", "NVIDIA Overlay.exe", "NVIDIA app.exe", "NVIDIA Share.exe",
        "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe", "BEService.exe", "vgc.exe",
    };

    static bool Eligible(string exe)
    {
        if (Apps.IsSystemPath(exe)) return false;
        if (exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false; // packaged apps use app ids, not paths
        if (Skip.Contains(Path.GetFileName(exe))) return false;
        var s = AppSettings.Current;
        if (s.ManualOverride.Contains(exe) || s.AutoAssigned.ContainsKey(exe)) return false;
        return !Preferences.HasGpuChoice(exe);
    }

    public static string? TargetFor(string exe)
    {
        var s = AppSettings.Current;
        return Compat.IsGame(exe) ? s.AutoGamesGpuKey : s.AutoAppsGpuKey;
    }

    static readonly HashSet<string> CarryChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Discord, Roblox Studio etc. install each update into a new folder (app-1.0.9266\Discord.exe), which silently
    /// drops the Windows GPU choice. If the same exe in a sibling version folder had a choice, copy it over.
    /// </summary>
    public static void CarryOverVersioned(IEnumerable<RunningApp> apps)
    {
        foreach (var a in apps)
        {
            lock (CarryChecked) if (!CarryChecked.Add(a.ExePath)) continue;
            if (!a.IsSystem) CarryOver(a.ExePath);
        }
    }

    /// <summary>Copies the GPU choice from the same exe in a sibling version folder. True if it did.</summary>
    public static bool CarryOver(string exePath)
    {
        if (Preferences.HasGpuChoice(exePath)) return false;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(exePath));
        if (root == null) return false;
        var name = Path.GetFileName(exePath);
        var prev = Preferences.All()
            .Where(x => x.GpuKey != null && x.Exe.Contains('\\')
                        && Path.GetFileName(x.Exe).Equals(name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(x.Exe)), root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Exe, StringComparer.OrdinalIgnoreCase) // newest version folder first
            .FirstOrDefault();
        if (prev.Exe == null) return false;
        Preferences.SetGpu(exePath, prev.GpuKey);
        Log.Write($"{name} updated: carried its GPU choice over from {Path.GetFileName(Path.GetDirectoryName(prev.Exe))}");
        return true;
    }

    /// <summary>Before we launch an app ourselves: carry its choice over from an old version, or auto-assign it.</summary>
    public static void PrepareForLaunch(string exePath)
    {
        lock (CarryChecked) CarryChecked.Add(exePath);
        if (CarryOver(exePath)) return;
        var s = AppSettings.Current;
        if (!s.AutoAssignEnabled || !Eligible(exePath)) return;
        var target = TargetFor(exePath);
        if (target == null || Gpus.ByKey(target) == null) return;
        Preferences.SetGpu(exePath, target);
        s.AutoAssigned[exePath] = target;
        s.Save();
        Log.Write($"auto-assign (before launch): {Path.GetFileName(exePath)} -> {Gpus.ByKey(target)?.ShortName}");
    }

    /// <summary>Called with every monitor update.</summary>
    public static void OnApps(IEnumerable<RunningApp> apps)
    {
        var s = AppSettings.Current;
        if (!s.AutoAssignEnabled) return;
        bool changed = false;
        foreach (var a in apps)
        {
            lock (SeenThisSession) if (!SeenThisSession.Add(a.ExePath)) continue;
            if (!Eligible(a.ExePath)) continue;
            var target = TargetFor(a.ExePath);
            if (target == null || Gpus.ByKey(target) == null) continue;

            Preferences.SetGpu(a.ExePath, target);
            s.AutoAssigned[a.ExePath] = target;
            changed = true;
            Log.Write($"auto-assign: {a.ExeName} -> {Gpus.ByKey(target)?.ShortName}");

            if (s.AutoLiveMoveChromium && a.PrimaryGpuKey != target && Compat.For(a.ExePath).Kind == LiveKind.Chromium)
            {
                var exe = a.ExePath;
                _ = Task.Run(async () =>
                {
                    try { Log.Write($"auto-assign live: {await LiveSwitch.MoveChromium(exe, target)}"); }
                    catch (Exception ex) { Log.Write($"auto-assign live move failed: {ex.Message}"); }
                });
            }
        }
        if (changed) s.Save();
    }

    public record Candidate(string Exe, string Name, bool IsGame, string GpuKey);

    /// <summary>Apps from the Start Menu that have no GPU choice yet.</summary>
    public static List<Candidate> ScanInstalled()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var junk = new[] { "unins", "uninstall", "setup", "install", "update", "crash", "report", "repair", "readme" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<Candidate>();
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return list;
        dynamic shell = Activator.CreateInstance(shellType)!;

        foreach (var dir in dirs.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var lnk in links)
            {
                string? target;
                try { target = (string)shell.CreateShortcut(lnk).TargetPath; } catch { continue; }
                if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                var file = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
                if (junk.Any(file.Contains)) continue;
                if (!seen.Add(target) || !Eligible(target)) continue;
                var key = TargetFor(target);
                if (key == null) continue;
                list.Add(new(target, Path.GetFileNameWithoutExtension(lnk), Compat.IsGame(target), key));
            }
        }
        return list.OrderBy(c => c.IsGame).ThenBy(c => c.Name).ToList();
    }

    public static void Apply(IEnumerable<Candidate> candidates)
    {
        var s = AppSettings.Current;
        foreach (var c in candidates)
        {
            Preferences.SetGpu(c.Exe, c.GpuKey);
            s.AutoAssigned[c.Exe] = c.GpuKey;
        }
        s.Save();
    }

    /// <summary>Removes every choice auto-assign made that's still unchanged, and turns auto-assign off.</summary>
    public static int Undo()
    {
        var s = AppSettings.Current;
        int n = 0;
        foreach (var (exe, key) in s.AutoAssigned.ToList())
        {
            if (string.Equals(Preferences.GetGpuKey(exe), key, StringComparison.OrdinalIgnoreCase))
            {
                Preferences.SetGpu(exe, null);
                n++;
            }
        }
        s.AutoAssigned.Clear();
        s.AutoAssignEnabled = false;
        s.Save();
        lock (SeenThisSession) SeenThisSession.Clear();
        return n;
    }

    /// <summary>The user picked a GPU by hand: auto-assign must never change this app again.</summary>
    public static void MarkManual(string exe)
    {
        var s = AppSettings.Current;
        s.ManualOverride.Add(exe);
        s.AutoAssigned.Remove(exe);
        s.Save();
    }
}
