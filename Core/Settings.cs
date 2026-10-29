using System.Text.Json;

namespace GpuSwitcher.Core;

public class ForceRule
{
    /// <summary>The real game exe (the one that creates the D3D device), e.g. FortniteClient-Win64-Shipping.exe.</summary>
    public string GameExe { get; set; } = "";
    /// <summary>Process names that start the sequence. Includes launchers so the GPU is already off when the game starts.</summary>
    public List<string> Triggers { get; set; } = new();
    /// <summary>GPU to hide while the game starts.</summary>
    public string DisableGpuKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Give up waiting and re-enable after this long, no matter what.</summary>
    public int MaxWaitSeconds { get; set; } = 90;
    public string Label { get; set; } = "";
}

public class AppSettings
{
    // ---- auto-assign ----
    public bool AutoAssignEnabled { get; set; }
    public string? AutoAppsGpuKey { get; set; }
    public string? AutoGamesGpuKey { get; set; }
    /// <summary>Also move running Chromium/Electron apps right away when auto-assign picks a GPU for them.</summary>
    public bool AutoLiveMoveChromium { get; set; }
    /// <summary>exe path -> GPU key auto-assign wrote. Used for undo and to never fight a manual choice.</summary>
    public Dictionary<string, string> AutoAssigned { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Apps the user changed by hand. Auto-assign leaves these alone forever, even if set back to Auto.</summary>
    public HashSet<string> ManualOverride { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- force mode ----
    public List<ForceRule> ForceRules { get; set; } = new();
    /// <summary>When a GPU comes back on, put apps that were bumped off it and belong on it back there.</summary>
    public bool ReturnAppsWhenGpuBack { get; set; } = true;

    // ---- compat results measured on this PC: exe name -> kind ----
    public Dictionary<string, string> CompatOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowSystemProcesses { get; set; }

    // ---- startup apps ----
    public List<ManagedStartup> ManagedStartup { get; set; } = new();
    public bool StartupTakeOverNew { get; set; }
    public int StartupGapMs { get; set; } = 1500;
    /// <summary>Explorer start time of the logon we last launched apps for.</summary>
    public string? LastStartupLogon { get; set; }

    static string FilePath => Path.Combine(Log.Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object Gate = new();

    public static AppSettings Current { get; private set; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                // System.Text.Json drops the comparer, put case-insensitivity back
                s.AutoAssigned = new(s.AutoAssigned, StringComparer.OrdinalIgnoreCase);
                s.ManualOverride = new(s.ManualOverride, StringComparer.OrdinalIgnoreCase);
                s.CompatOverrides = new(s.CompatOverrides, StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        return new AppSettings();
    }

    public void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Log.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
    }
}
