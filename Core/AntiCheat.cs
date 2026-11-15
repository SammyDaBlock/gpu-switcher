namespace GpuSwitcher.Core;

/// <summary>
/// Games protected by Easy Anti-Cheat or BattlEye. For these GPU Switcher only writes the Windows per-app GPU
/// preference (registry, read by the game when it starts). No force mode, no GPU off/on while one runs, no closing
/// or restarting them, no overlays over them, and no handles opened to their processes.
/// Turning the 3060 off while Fortnite's EAC bootstrapper loaded gave "EAC unload timeout" (30005), and turning it
/// back on while Fortnite was loading froze its render thread (2026-11-01).
/// </summary>
public static class AntiCheat
{
    // Always protected, plus whatever is in settings.json "AntiCheatGames"
    static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "FortniteClient-Win64-Shipping.exe", "FortniteClient-Win64-Shipping_EAC_EOS.exe",
        "FortniteClient-Win64-Shipping_BE.exe", "FortniteClient-Win64-Shipping_EAC.exe", "FortniteLauncher.exe",
    };

    // Folders/files EAC and BattlEye install next to the games they protect
    static readonly string[] Markers = { "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "BEService.exe", "BEService_x64.exe" };

    static readonly Dictionary<string, bool> DirCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>By exe name only (works without a path, e.g. from the Toolhelp process list).</summary>
    public static bool IsName(string exe)
    {
        if (!AppSettings.Current.AntiCheatSafety) return false;
        var name = Path.GetFileName(exe);
        if (BuiltIn.Contains(name) || AppSettings.Current.AntiCheatGames.Contains(name)) return true;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.EndsWith("_EAC", StringComparison.OrdinalIgnoreCase) || stem.EndsWith("_EAC_EOS", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_BE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || name.StartsWith("BEService", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>By name, or by EAC/BattlEye files in the game's folder.</summary>
    public static bool Is(string exePath)
    {
        if (!AppSettings.Current.AntiCheatSafety) return false;
        if (IsName(exePath)) return true;
        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir) || Apps.IsSystemPath(exePath) || !Compat.IsGame(exePath)) return false;
        lock (DirCache)
        {
            if (DirCache.TryGetValue(dir, out var hit)) return hit;
            return DirCache[dir] = HasMarker(dir);
        }
    }

    // Shared folders that hold EAC/BattlEye's own install or data (Program Files (x86)\EasyAntiCheat, AppData\Local\BattlEye):
    // finding a marker there says nothing about the game, so the climb stops before them
    static readonly HashSet<string> SharedRoots = new(new[]
    {
        Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
    }.Select(f => Environment.GetFolderPath(f).TrimEnd('\\')).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// <summary>The game exe's folder and up to 3 parents (Unreal: &lt;game&gt;\Binaries\Win64\x.exe), never a drive root or shared folder.</summary>
    static bool HasMarker(string dir)
    {
        var d = new DirectoryInfo(dir);
        for (int i = 0; i < 4 && d?.Parent?.Parent != null && !SharedRoots.Contains(d.FullName.TrimEnd('\\')); i++, d = d.Parent)
        {
            try { if (Markers.Any(m => Path.Exists(Path.Combine(d!.FullName, m)))) return true; }
            catch { }
        }
        return false;
    }

    /// <summary>Name of a running anti-cheat game, or null. Uses the process list only, never opens a process.</summary>
    public static string? Running()
    {
        foreach (var (pid, name) in Native.ProcessSnapshot())
            if (IsName(name) || (Native.GetProcessPath(pid) is { } p && Is(p))) return name;
        return null;
    }

    public static bool IsRule(ForceRule r) => IsName(r.GameExe) || r.Triggers.Any(IsName);

    public static string Explain(string name) =>
        $"{Path.GetFileNameWithoutExtension(name)} uses anti-cheat (Easy Anti-Cheat / BattlEye). GPU Switcher only sets its Windows GPU " +
        "preference before it starts and never touches it while it runs: turning a GPU off or on during launch breaks the anti-cheat " +
        "(EAC error 30005) or freezes the game.";

    /// <summary>Self-check, run with --selftest-anticheat.</summary>
    public static void SelfTest()
    {
        void Check(bool ok, string what) { if (!ok) throw new Exception("AntiCheat self-test failed: " + what); }
        Check(IsName("FortniteClient-Win64-Shipping.exe"), "Fortnite by name");
        Check(IsName("Game-Win64-Shipping_EAC_EOS.exe"), "_EAC_EOS suffix");
        Check(IsName("DayZ_BE.exe"), "_BE suffix");
        Check(!IsName("brave.exe"), "brave is not anti-cheat");
        Check(!IsName("Voicemod.exe"), "Voicemod is not anti-cheat");
        var fn = @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";
        Check(Is(fn), "Fortnite by path");
        // the shared EAC service folder in Program Files (x86) must not flag every 32-bit app
        Check(!Is(@"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe"), "Afterburner is not anti-cheat");
        Check(!Is(@"C:\Program Files (x86)\Steam\steam.exe"), "Steam is not anti-cheat");
        // %LOCALAPPDATA%\BattlEye is BattlEye's shared data folder
        Check(!Is(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Discord\app-1.0.9266\Discord.exe")), "Discord is not anti-cheat");
    }
}
