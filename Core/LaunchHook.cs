using Microsoft.Win32;

namespace GpuSwitcher.Core;

/// <summary>
/// Force mode for anti-cheat games. Their anti-cheat starts a few ms after the launcher, faster than any process
/// watcher, and a GPU going off while it loads kills it (EAC 30005). So the launcher exe (rule.Hook) gets an Image File
/// Execution Options "Debugger" pointing at GpuSwitcherHook.exe: Windows starts that instead, ForceMode sees it, turns
/// the GPU off, removes the hook (the helper's go signal) and puts it back once the real launcher is running.
/// </summary>
public static class LaunchHook
{
    const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    public const string HelperExe = "GpuSwitcherHook.exe";
    static string HelperPath => Path.Combine(AppContext.BaseDirectory, HelperExe);
    static readonly object Lock = new();

    static bool Ours(object? v) => v is string s && s.Contains(HelperExe, StringComparison.OrdinalIgnoreCase);

    // settings.json is user-writable but we write HKLM as admin: only ever hook launchers we know, never a name from the file
    static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "FortniteLauncher.exe" };

    /// <summary>Hooks every enabled rule's launcher and unhooks everything else that is ours.</summary>
    public static void Sync()
    {
        lock (Lock)
        {
            var want = File.Exists(HelperPath)
                ? AppSettings.Current.ForceRules.Where(r => r.Enabled && !string.IsNullOrEmpty(r.Hook)).Select(r => r.Hook!).Where(Allowed.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(Ifeo)!;
                foreach (var name in root.GetSubKeyNames())
                    if (!want.Contains(name)) Remove(name);
                foreach (var name in want)
                {
                    using var k = Registry.LocalMachine.CreateSubKey($@"{Ifeo}\{name}");
                    var cur = k.GetValue("Debugger");
                    if (cur != null && !Ours(cur)) { Log.Write($"launch hook: {name} already has a Debugger set by something else, left it alone"); continue; }
                    if (cur == null) { k.SetValue("Debugger", $"\"{HelperPath}\""); Log.Write($"launch hook: hooked {name}"); }
                }
            }
            catch (Exception ex) { Log.Write("launch hook sync: " + ex.Message); }
        }
    }

    /// <summary>Lets the waiting helper start the real launcher.</summary>
    public static void Remove(string name)
    {
        lock (Lock)
        {
            try
            {
                using (var r = Registry.LocalMachine.OpenSubKey($@"{Ifeo}\{name}"))
                    if (r == null || !Ours(r.GetValue("Debugger"))) return;
                using var k = Registry.LocalMachine.OpenSubKey($@"{Ifeo}\{name}", writable: true)!;
                k.DeleteValue("Debugger");
                Log.Write($"launch hook: unhooked {name}");
            }
            catch (Exception ex) { Log.Write($"launch hook remove {name}: " + ex.Message); }
        }
    }

    /// <summary>Unhooked: put it back once the real launcher has started (or the helper gave up), never before,
    /// or the helper would start another copy of itself.</summary>
    public static async Task Rearm(string name)
    {
        Remove(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (int i = 0; i < 300; i++)
        {
            if (System.Diagnostics.Process.GetProcessesByName(stem).Length > 0) break;
            if (System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HelperExe)).Length == 0) break;
            await Task.Delay(100);
        }
        Sync();
    }

    /// <summary>On exit/uninstall: Fortnite must start normally without GPU Switcher.</summary>
    public static void RemoveAll()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(Ifeo)!;
            foreach (var name in root.GetSubKeyNames()) Remove(name);
        }
        catch (Exception ex) { Log.Write("launch hook remove all: " + ex.Message); }
    }
}
