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

/// <summary>One money cover, as fractions of the game window's client area (so it survives resolution changes).</summary>
public class CoverRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

public class OverlaySettings
{
    // ---- performance overlay ----
    public bool PerfEnabled { get; set; }
    /// <summary>TopLeft, TopRight, BottomLeft, BottomRight.</summary>
    public string PerfCorner { get; set; } = "TopLeft";
    /// <summary>Distance from PerfCorner's edges, in DIPs. Set by dragging the overlay.</summary>
    public double PerfOffsetX { get; set; } = 14;
    public double PerfOffsetY { get; set; } = 14;
    /// <summary>Always on this display (device name like \\.\DISPLAY1). Null = the screen the game is on.</summary>
    public string? PerfMonitor { get; set; }
    /// <summary>0 small, 1 medium, 2 large.</summary>
    public int PerfSize { get; set; } = 1;
    /// <summary>0 clear (text only), 1 dim, 2 dark.</summary>
    public int PerfBackground { get; set; } = 1;
    /// <summary>Only this GPU's line. Null = all GPUs.</summary>
    public string? PerfGpuKey { get; set; }
    public HashSet<string> PerfItems { get; set; } = new(PerfOverlayItems.Defaults, StringComparer.OrdinalIgnoreCase);

    // ---- money covers ----
    /// <summary>exe name -> covers for that game.</summary>
    public Dictionary<string, List<CoverRect>> Covers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Revealed: covers are off until switched back on.</summary>
    public bool CoversRevealed { get; set; }
    /// <summary>Black, Gray, Blue, Pink.</summary>
    public string CoverColor { get; set; } = "Black";
    public bool CoverLabel { get; set; } = true;
}

/// <summary>What the performance overlay can show. Keys are saved in settings.</summary>
public static class PerfOverlayItems
{
    public static readonly (string Key, string Label)[] Performance =
    {
        ("fps", "FPS"), ("frametime", "Frame time"), ("low", "1% low FPS"), ("app", "Game + the GPU it's on"),
        ("gpu-usage", "GPU usage"), ("gpu-temp", "GPU temperature"), ("gpu-clock", "GPU clock"), ("gpu-power", "GPU power"),
        ("gpu-fan", "GPU fan"), ("gpu-vram", "VRAM"), ("cpu", "CPU usage"), ("ram", "RAM usage"), ("clock", "Time"),
    };
    public static readonly (string Key, string Label)[] Specs =
    {
        ("spec-cpu", "CPU"), ("spec-gpu", "GPUs"), ("spec-ram", "RAM"), ("spec-board", "Motherboard"), ("spec-os", "Windows"), ("spec-display", "Display"),
    };
    public static readonly string[] Defaults = { "fps", "frametime", "app", "gpu-usage", "gpu-temp", "gpu-vram", "cpu", "ram" };
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

    // ---- overlays ----
    public OverlaySettings Overlay { get; set; } = new();

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
                s.Overlay.PerfItems = new(s.Overlay.PerfItems, StringComparer.OrdinalIgnoreCase);
                s.Overlay.Covers = new(s.Overlay.Covers, StringComparer.OrdinalIgnoreCase);
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
