using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GpuSwitcher.Core;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public class StartupItem
{
    public required string Id { get; init; }
    public required StartupSource Source { get; init; }
    /// <summary>Run value name, or the shortcut's file name for startup folder items.</summary>
    public required string ValueName { get; init; }
    public required string Command { get; init; }
    public bool EnabledInWindows { get; set; }
    public string? ExePath { get; set; }
    /// <summary>The exe that actually renders (Discord's Update.exe → current app-x.y.z\Discord.exe).</summary>
    public string? TargetExe { get; set; }
    public string Name { get; set; } = "";
    public bool IsWindowsComponent => ExePath != null && Apps.IsSystemPath(ExePath);

    public string SourceText => Source switch
    {
        StartupSource.UserRun => "Registry (you)",
        StartupSource.MachineRun or StartupSource.MachineRun32 => "Registry (all users)",
        _ => "Startup folder",
    };
}

/// <summary>A startup app GPU Switcher took over from Windows.</summary>
public class ManagedStartup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>False = we keep it off (still disabled in Windows).</summary>
    public bool Launch { get; set; } = true;
    /// <summary>Its Windows state before we took it, restored by "give back".</summary>
    public bool WasEnabledInWindows { get; set; } = true;
}

/// <summary>
/// Takes startup apps over from Windows: marks them Disabled exactly like Task Manager does
/// (Explorer\StartupApproved, first byte 02 = on, 03 = off), then launches them itself once
/// GPU Switcher is ready. Everything is reversible with GiveBack.
/// </summary>
public static class StartupManager
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // -------------------------------------------------- reading

    public static List<StartupItem> List()
    {
        var list = new List<StartupItem>();
        ReadRun(list, Registry.CurrentUser, RunKey, StartupSource.UserRun);
        ReadRun(list, Registry.LocalMachine, RunKey, StartupSource.MachineRun);
        ReadRun(list, Registry.LocalMachine, Run32Key, StartupSource.MachineRun32);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder);
        foreach (var i in list)
        {
            i.TargetExe = ResolveTarget(i);
            i.Name = FriendlyName(i);
        }
        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void ReadRun(List<StartupItem> list, RegistryKey hive, string path, StartupSource src)
    {
        using var k = hive.OpenSubKey(path);
        if (k == null) return;
        foreach (var name in k.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || k.GetValue(name) is not string cmd) continue;
            var item = new StartupItem { Id = $"{src}|{name}", Source = src, ValueName = name, Command = cmd };
            item.EnabledInWindows = ReadApproved(item);
            item.ExePath = SplitCommand(cmd).Exe;
            list.Add(item);
        }
    }

    static void ReadFolder(List<StartupItem> list, string dir, StartupSource src)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var item = new StartupItem { Id = $"{src}|{name}", Source = src, ValueName = name, Command = file };
            item.EnabledInWindows = ReadApproved(item);
            item.ExePath = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file).Target : file;
            list.Add(item);
        }
    }

    static (RegistryKey Hive, string Sub) ApprovedLocation(StartupSource s) => s switch
    {
        StartupSource.UserRun => (Registry.CurrentUser, Approved + "Run"),
        StartupSource.MachineRun => (Registry.LocalMachine, Approved + "Run"),
        StartupSource.MachineRun32 => (Registry.LocalMachine, Approved + "Run32"),
        StartupSource.UserFolder => (Registry.CurrentUser, Approved + "StartupFolder"),
        _ => (Registry.LocalMachine, Approved + "StartupFolder"),
    };

    static bool ReadApproved(StartupItem i)
    {
        var (hive, sub) = ApprovedLocation(i.Source);
        using var k = hive.OpenSubKey(sub);
        // no entry = enabled; odd first byte (03, 07) = disabled
        return k?.GetValue(i.ValueName) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    static void WriteApproved(StartupItem i, bool enabled)
    {
        var (hive, sub) = ApprovedLocation(i.Source);
        using var k = hive.CreateSubKey(sub);
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        k.SetValue(i.ValueName, data, RegistryValueKind.Binary);
        i.EnabledInWindows = enabled;
    }

    // -------------------------------------------------- commands

    /// <summary>Splits a Run command into exe + the rest, handling quotes, %vars% and unquoted paths with spaces.</summary>
    public static (string? Exe, string Args) SplitCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd).Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        // unquoted: grow the path token by token until it's an existing .exe
        var parts = cmd.Split(' ');
        for (int n = 1; n <= parts.Length; n++)
        {
            var candidate = string.Join(' ', parts.Take(n));
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                return (candidate, string.Join(' ', parts.Skip(n)));
        }
        return (parts[0], string.Join(' ', parts.Skip(1)));
    }

    static (string? Target, string Args, string? WorkDir) ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return (null, "", null);
            dynamic shell = Activator.CreateInstance(t)!;
            var sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath, args = sc.Arguments, wd = sc.WorkingDirectory;
            return (string.IsNullOrEmpty(target) ? null : target, args ?? "", string.IsNullOrEmpty(wd) ? null : wd);
        }
        catch { return (null, "", null); }
    }

    /// <summary>Squirrel apps (Discord...) start through Update.exe --processStart X.exe: find the real current exe.</summary>
    static string? ResolveTarget(StartupItem i)
    {
        var exe = i.ExePath;
        if (exe == null) return null;
        if (Path.GetFileName(exe).Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(i.Command, @"--processStart\s+""?([^""\s]+)""?", RegexOptions.IgnoreCase);
            var dir = Path.GetDirectoryName(exe);
            if (m.Success && dir != null && Directory.Exists(dir))
            {
                var newest = Directory.EnumerateDirectories(dir, "app-*")
                    .Select(d => Path.Combine(d, m.Groups[1].Value)).Where(File.Exists)
                    .OrderByDescending(AppFolderVersion)
                    .FirstOrDefault();
                if (newest != null) return newest;
            }
        }
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
    }

    static Version AppFolderVersion(string path)
    {
        var m = Regex.Match(path, @"app-([\d.]+)");
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : new Version(0, 0);
    }

    static string FriendlyName(StartupItem i)
    {
        var exe = i.TargetExe ?? i.ExePath;
        // rundll32 & co. would all be called "Windows host process": the entry's own name says more
        if (exe != null && File.Exists(exe) && !Apps.IsSystemPath(exe))
        {
            var n = Apps.FriendlyName(exe);
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }
        return Path.GetFileNameWithoutExtension(i.ValueName);
    }

    // -------------------------------------------------- take over / give back

    static ManagedStartup? Managed(string id) => AppSettings.Current.ManagedStartup.FirstOrDefault(m => m.Id == id);

    public static bool IsManaged(StartupItem i) => Managed(i.Id) != null;

    /// <summary>Windows won't start it any more, GPU Switcher will (launch = true) or nothing will (launch = false).</summary>
    public static void TakeOver(StartupItem i, bool launch = true)
    {
        var s = AppSettings.Current;
        var m = Managed(i.Id);
        if (m == null)
        {
            m = new ManagedStartup { Id = i.Id, Name = i.Name, Command = i.Command, WasEnabledInWindows = i.EnabledInWindows };
            s.ManagedStartup.Add(m);
        }
        m.Launch = launch;
        m.Command = i.Command;
        if (i.EnabledInWindows) WriteApproved(i, false);
        s.Save();
        Log.Write($"startup: {i.Name} -> {(launch ? "started by GPU Switcher" : "off")}");
    }

    /// <summary>Hand it back to Windows in the state it was in before we took it.</summary>
    public static void GiveBack(StartupItem i, bool? enabled = null)
    {
        var s = AppSettings.Current;
        var m = Managed(i.Id);
        bool on = enabled ?? m?.WasEnabledInWindows ?? true;
        if (m != null) s.ManagedStartup.Remove(m);
        WriteApproved(i, on);
        s.Save();
        Log.Write($"startup: {i.Name} -> Windows ({(on ? "on" : "off")})");
    }

    /// <summary>Turn it off without taking it over (same as disabling in Task Manager).</summary>
    public static void TurnOff(StartupItem i)
    {
        if (IsManaged(i)) { TakeOver(i, launch: false); return; }
        WriteApproved(i, false);
        Log.Write($"startup: {i.Name} -> off");
    }

    public static int GiveBackAll()
    {
        var items = List();
        int n = 0;
        foreach (var m in AppSettings.Current.ManagedStartup.ToList())
        {
            var i = items.FirstOrDefault(x => x.Id == m.Id);
            if (i != null) { GiveBack(i); n++; }
            else AppSettings.Current.ManagedStartup.Remove(m);
        }
        AppSettings.Current.Save();
        return n;
    }

    /// <summary>Apps that are safe to take over in bulk: enabled now, not part of Windows/drivers, not us.</summary>
    public static bool GoodCandidate(StartupItem i) =>
        i.EnabledInWindows && !IsManaged(i) && !i.IsWindowsComponent
        && !(i.ExePath?.Contains("GpuSwitcher", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Run on every start and every few minutes: apps that re-enabled themselves get disabled again,
    /// uninstalled ones are forgotten, and (if wanted) new startup apps are taken over.
    /// </summary>
    public static List<string> Reconcile()
    {
        var notes = new List<string>();
        var s = AppSettings.Current;
        var items = List();
        foreach (var m in s.ManagedStartup.ToList())
        {
            var i = items.FirstOrDefault(x => x.Id == m.Id);
            if (i == null) { s.ManagedStartup.Remove(m); Log.Write($"startup: {m.Name} is gone, forgot it"); continue; }
            m.Command = i.Command; // apps rewrite their own command on update
            if (i.EnabledInWindows)
            {
                WriteApproved(i, false);
                Log.Write($"startup: {i.Name} turned itself back on in Windows, took it back");
            }
        }
        if (s.StartupTakeOverNew && s.ManagedStartup.Count > 0)
        {
            foreach (var i in items.Where(GoodCandidate))
            {
                TakeOver(i);
                notes.Add($"New startup app {i.Name} is now started by GPU Switcher.");
            }
        }
        s.Save();
        return notes;
    }

    // -------------------------------------------------- adding new ones

    public record AddCandidate(string Name, string Exe, bool Running);

    /// <summary>Running apps first, then Start Menu / Desktop apps. Skips Windows stuff, Store apps and apps already in startup.</summary>
    public static List<AddCandidate> AddCandidates(IEnumerable<RunningApp> running)
    {
        var inStartup = List().SelectMany(i => new[] { i.ExePath, i.TargetExe }).Where(p => p != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Ok(string exe) => !Apps.IsSystemPath(exe) && !exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                               && !exe.Contains("GpuSwitcher", StringComparison.OrdinalIgnoreCase)
                               && !inStartup.Contains(exe) && seen.Add(exe);

        var list = running.Where(a => Ok(a.ExePath)).Select(a => new AddCandidate(a.DisplayName, a.ExePath, true))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var junk = new[] { "unins", "uninstall", "setup", "install", "update", "crash", "report", "repair", "readme" };
        var installed = new List<AddCandidate>();
        foreach (var dir in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                     .Select(Environment.GetFolderPath).Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var lnk in links)
            {
                var target = ResolveShortcut(lnk).Target;
                if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                if (junk.Any(Path.GetFileNameWithoutExtension(target).ToLowerInvariant().Contains)) continue;
                if (Ok(target)) installed.Add(new(Path.GetFileNameWithoutExtension(lnk), target, false));
            }
        }
        list.AddRange(installed.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>
    /// Puts a program in startup with a shortcut in your Startup folder (so "give back" leaves a normal startup app),
    /// then takes it over. A program that's already in startup just gets taken over.
    /// </summary>
    public static StartupItem Add(string exePath)
    {
        if (exePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            exePath = ResolveShortcut(exePath).Target ?? throw new InvalidOperationException("That shortcut doesn't point to a program.");
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exePath))
            throw new InvalidOperationException("Pick a program (.exe) or a shortcut to one.");

        var existing = List().FirstOrDefault(i => string.Equals(i.TargetExe ?? i.ExePath, exePath, StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(i.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            var name = Apps.FriendlyName(exePath);
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exePath);
            name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var lnk = Path.Combine(dir, name + ".lnk");
            for (int n = 2; File.Exists(lnk); n++) lnk = Path.Combine(dir, $"{name} ({n}).lnk");

            var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is missing.");
            dynamic shell = Activator.CreateInstance(t)!;
            var sc = shell.CreateShortcut(lnk);
            sc.TargetPath = exePath;
            sc.WorkingDirectory = Path.GetDirectoryName(exePath);
            sc.Save();
            Log.Write($"startup: added {name}");
            existing = List().First(i => i.Id == $"{StartupSource.UserFolder}|{Path.GetFileName(lnk)}");
        }
        TakeOver(existing);
        return existing;
    }

    // -------------------------------------------------- launching

    /// <summary>Explorer's start time = when this logon happened. Used to launch startup apps once per logon.</summary>
    static string? LogonStamp()
    {
        var shell = Native.GetShellProcessId();
        if (shell == 0) return null;
        try { using var p = Process.GetProcessById(shell); return p.StartTime.ToString("O"); } catch { return null; }
    }

    /// <summary>Called at logon (from the scheduled task). Launches every taken-over app, once per logon.</summary>
    public static async Task LaunchAtLogon()
    {
        var s = AppSettings.Current;
        if (!s.ManagedStartup.Any(m => m.Launch)) return;

        // explorer must be up: we launch through its (non-admin) token
        string? stamp = null;
        for (int i = 0; i < 180 && (stamp = LogonStamp()) == null; i++) await Task.Delay(500);
        if (stamp == null) { Log.Write("startup: explorer never came up, not launching apps"); return; }
        if (s.LastStartupLogon == stamp) { Log.Write("startup: already launched apps for this logon"); return; }
        s.LastStartupLogon = stamp;
        s.Save();
        await LaunchAll();
    }

    /// <summary>Launches every taken-over app in list order, one by one.</summary>
    public static async Task<List<string>> LaunchAll()
    {
        var s = AppSettings.Current;
        var results = new List<string>();
        var items = List();
        await Task.Delay(1500); // let the GPU list and the first monitor pass settle
        foreach (var m in s.ManagedStartup.Where(m => m.Launch).ToList())
        {
            var i = items.FirstOrDefault(x => x.Id == m.Id);
            if (i == null) { results.Add($"{m.Name}: not in the startup list anymore"); continue; }
            try
            {
                Launch(i);
                results.Add($"{i.Name}: started");
            }
            catch (Exception ex)
            {
                results.Add($"{i.Name}: failed ({ex.Message})");
                Log.Write($"startup: {i.Name} failed: {ex.Message}");
            }
            await Task.Delay(Math.Clamp(s.StartupGapMs, 0, 30000));
        }
        Log.Write($"startup: launched {results.Count(r => r.EndsWith("started"))} apps");
        return results;
    }

    static void Launch(StartupItem i)
    {
        // make sure the GPU choice is in place BEFORE the app creates its device
        if (i.TargetExe != null) AutoAssign.PrepareForLaunch(i.TargetExe);

        if (i.Source is StartupSource.UserFolder or StartupSource.CommonFolder)
        {
            var (target, args, wd) = i.Command.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(i.Command) : (i.Command, "", null);
            if (target != null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                Native.LaunchUnelevated(target, $"\"{target}\" {args}".Trim(), wd);
            else // not an exe (url, document...): let Explorer open it
                Native.LaunchUnelevated(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"explorer.exe \"{i.Command}\"", null);
        }
        else
        {
            var (exe, _) = SplitCommand(i.Command);
            if (exe == null || !File.Exists(exe)) throw new FileNotFoundException("Program not found", exe);
            Native.LaunchUnelevated(exe, Environment.ExpandEnvironmentVariables(i.Command), Path.GetDirectoryName(exe));
        }
        Log.Write($"startup: launched {i.Name}");
    }
}
