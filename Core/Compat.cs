namespace GpuSwitcher.Core;

public enum LiveKind
{
    Unknown,
    /// <summary>Chromium/Electron/CEF: we restart only its GPU process, it comes back on the chosen GPU. Any direction.</summary>
    Chromium,
    /// <summary>Recreates its device when its GPU disappears. Can only be moved OFF a GPU, and that moves everything on that GPU.</summary>
    SurvivesDeviceLoss,
    /// <summary>Crashes or quits when its GPU disappears. Restart only.</summary>
    CrashesOnDeviceLoss,
    /// <summary>Games: never survive losing their GPU. Restart or force mode.</summary>
    Game,
}

public record CompatEntry(LiveKind Kind, string Note);

public static class Compat
{
    // Measured on an RTX 5070 + RTX 3060 PC, 2026-10-28 (3060 disabled for 5 s, then re-enabled)
    static readonly Dictionary<string, CompatEntry> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brave.exe"] = new(LiveKind.Chromium, "Tested: GPU process restart moved it, tabs stayed"),
        ["wallpaper64.exe"] = new(LiveKind.SurvivesDeviceLoss, "Tested: moved to the other GPU live"),
        ["Voicemod.exe"] = new(LiveKind.SurvivesDeviceLoss, "Tested: moved to the other GPU live"),
        ["SteelSeriesGGClient.exe"] = new(LiveKind.SurvivesDeviceLoss, "Tested: moved to the other GPU live"),
        ["MedalEncoder.exe"] = new(LiveKind.SurvivesDeviceLoss, "Tested: survived, kept running"),
        ["KinectService.exe"] = new(LiveKind.SurvivesDeviceLoss, "Tested: survived"),
        ["CMAgent.exe"] = new(LiveKind.CrashesOnDeviceLoss, "Tested: crashed (Cooler Master restarts it)"),
        ["NVIDIA Overlay.exe"] = new(LiveKind.CrashesOnDeviceLoss, "Tested: died, NVIDIA app respawns it"),
    };

    static readonly string[] GameFolders =
    {
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\EA Games\", @"\Electronic Arts\",
        @"\Ubisoft Game Launcher\games\", @"\GOG Galaxy\Games\", @"\XboxGames\", @"\Battle.net\", @"\Rockstar Games\",
    };

    static readonly string[] GameFiles = { "UnityPlayer.dll", "steam_api64.dll", "steam_api.dll", "EOSSDK-Win64-Shipping.dll" };

    // Tools that live in game folders but aren't games
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LosslessScaling.exe", "wallpaper64.exe", "wallpaper32.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe",
        "EOSOverlayRenderer-Win64-Shipping.exe", "UnrealCEFSubProcess.exe", "CrashReportClient.exe", "steamwebhelper.exe",
    };

    static readonly Dictionary<string, bool> ChromiumCache = new(StringComparer.OrdinalIgnoreCase);

    public static void MarkChromium(string exePath, bool isChromium)
    {
        lock (ChromiumCache) ChromiumCache[exePath] = isChromium;
    }

    public static bool? KnownChromium(string exePath)
    {
        lock (ChromiumCache) return ChromiumCache.TryGetValue(exePath, out var b) ? b : null;
    }

    /// <summary>
    /// For running apps we haven't classified yet: Chromium-family if one of its processes is a "--type=gpu-process".
    /// One WMI query covers all of them; only runs when new apps show up.
    /// </summary>
    public static void DetectRunning(IEnumerable<RunningApp> apps)
    {
        var unknown = apps.Where(a => !a.IsSystem && KnownChromium(a.ExePath) == null).Select(a => a.ExePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unknown.Count == 0) return;
        var withGpuProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT ExecutablePath, CommandLine FROM Win32_Process");
            foreach (System.Management.ManagementObject mo in s.Get())
            {
                var path = mo["ExecutablePath"] as string;
                if (path != null && unknown.Contains(path) && (mo["CommandLine"] as string)?.Contains("--type=gpu-process") == true)
                    withGpuProcess.Add(path);
            }
        }
        catch { return; }
        foreach (var exe in unknown) MarkChromium(exe, withGpuProcess.Contains(exe));
    }

    public static bool IsGame(string exePath)
    {
        var name = Path.GetFileName(exePath);
        if (NotGames.Contains(name)) return false;
        if (name.Contains("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) return true;
        // launchers live in game folders but are just UI
        if (name.Contains("launcher", StringComparison.OrdinalIgnoreCase) || name.Contains("patcher", StringComparison.OrdinalIgnoreCase)) return false;
        if (GameFolders.Any(f => exePath.Contains(f, StringComparison.OrdinalIgnoreCase))) return true;
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (dir != null && GameFiles.Any(f => File.Exists(Path.Combine(dir, f)))) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Chromium-family apps ship these next to the exe (Chrome keeps them in a version subfolder).</summary>
    static bool LooksChromium(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath)!;
            if (File.Exists(Path.Combine(dir, "libcef.dll"))) return true;
            if (File.Exists(Path.Combine(dir, "v8_context_snapshot.bin")) && File.Exists(Path.Combine(dir, "resources.pak"))) return true;
            if (File.Exists(Path.Combine(dir, "chrome_elf.dll"))) return true;
            return Directory.EnumerateDirectories(dir).Any(d => File.Exists(Path.Combine(d, "chrome_elf.dll")));
        }
        catch { return false; }
    }

    public static CompatEntry For(string exePath)
    {
        var name = Path.GetFileName(exePath);
        if (AppSettings.Current.CompatOverrides.TryGetValue(name, out var o) && Enum.TryParse<LiveKind>(o, out var k))
            return new(k, "Tested on this PC");
        if (BuiltIn.TryGetValue(name, out var e)) return e;
        if (IsGame(exePath)) return new(LiveKind.Game, "Games don't survive losing their GPU");
        var chromium = KnownChromium(exePath) ?? LooksChromium(exePath);
        if (chromium) return new(LiveKind.Chromium, "Chromium/Electron app: restarts its GPU process");
        return new(LiveKind.Unknown, "Not tested yet");
    }

    public static IEnumerable<(string Exe, CompatEntry Entry)> BuiltInList() => BuiltIn.Select(kv => (kv.Key, kv.Value));
}
