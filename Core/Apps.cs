using System.Diagnostics;
using System.Management;

namespace GpuSwitcher.Core;

/// <summary>One app = every running process with the same exe path.</summary>
public class RunningApp
{
    public required string ExePath { get; init; }
    public string ExeName => Path.GetFileName(ExePath);
    public string DisplayName { get; set; } = "";
    public List<int> Pids { get; } = new();
    public Dictionary<string, double> Util { get; } = new();
    public Dictionary<string, long> Mem { get; } = new();
    public HashSet<string> Touches { get; } = new();

    /// <summary>The GPU it actually renders on: most dedicated VRAM, then most usage, then any GPU it touches.</summary>
    public string? PrimaryGpuKey =>
        Mem.Where(kv => kv.Value > 8 << 20).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault()
        ?? Util.Where(kv => kv.Value > 0.5).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault()
        // a few MB on the display GPU is just its window being shown there; if it touches another GPU, that's where it renders
        ?? Mem.OrderBy(kv => Gpus.ByKey(kv.Key)?.IsDisplayGpu == true).ThenByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault()
        ?? Touches.FirstOrDefault();

    public double TotalUtil => Math.Min(100, Util.Values.Sum());

    public bool IsSystem => Apps.IsSystemPath(ExePath);
}

public static class Apps
{
    static readonly Dictionary<int, (string? Path, DateTime Seen)> PathCache = new();
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static bool IsSystemPath(string path) =>
        path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase) || !path.Contains('\\');

    public static List<RunningApp> Build(GpuSnapshot snap)
    {
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        lock (PathCache)
        {
            foreach (var (pid, use) in snap.Procs)
            {
                var path = PathFor(pid);
                if (path == null) continue;
                if (!apps.TryGetValue(path, out var app))
                    apps[path] = app = new RunningApp { ExePath = path, DisplayName = FriendlyName(path) };
                app.Pids.Add(pid);
                foreach (var (k, v) in use.Util) app.Util[k] = app.Util.GetValueOrDefault(k) + v;
                foreach (var (k, v) in use.Mem) app.Mem[k] = app.Mem.GetValueOrDefault(k) + v;
                app.Touches.UnionWith(use.Touches);
            }
            // forget dead pids
            foreach (var dead in PathCache.Keys.Where(p => !snap.Procs.ContainsKey(p)).ToList()) PathCache.Remove(dead);
        }
        return apps.Values.ToList();
    }

    static string? PathFor(int pid)
    {
        if (pid == 0) return null;
        if (pid == 4) return "System";
        if (PathCache.TryGetValue(pid, out var c)) return c.Path;
        var p = Native.GetProcessPath(pid);
        PathCache[pid] = (p, DateTime.Now);
        return p;
    }

    public static string FriendlyName(string path)
    {
        lock (NameCache)
        {
            if (NameCache.TryGetValue(path, out var n)) return n;
            string name = Path.GetFileNameWithoutExtension(path);
            try
            {
                if (File.Exists(path))
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    var desc = vi.FileDescription?.Trim();
                    if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) name = desc;
                }
            }
            catch { }
            return NameCache[path] = name;
        }
    }

    /// <summary>All processes of an exe with their command lines and parent pids (WMI, so call off the UI thread).</summary>
    public static List<(int Pid, int ParentPid, string? CommandLine)> ProcessesOf(string exePath)
    {
        var list = new List<(int, int, string?)>();
        var name = Path.GetFileName(exePath).Replace("'", "''");
        using var s = new ManagementObjectSearcher($"SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='{name}'");
        foreach (ManagementObject mo in s.Get())
        {
            var p = mo["ExecutablePath"] as string;
            if (p != null && !string.Equals(p, exePath, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((Convert.ToInt32(mo["ProcessId"]), Convert.ToInt32(mo["ParentProcessId"]), mo["CommandLine"] as string));
        }
        return list;
    }
}
