using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GpuSwitcher.Core;

namespace GpuSwitcher.UI;

public abstract class Vm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    /// <summary>Re-read every binding (after a light/dark switch).</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Colors come from the current theme (Themes/*.xaml), so they're looked up on every call.</summary>
public static class Look
{
    static Brush R(string key) => (Brush)Application.Current.Resources[key];
    public static Brush Sub => R("Sub");
    public static Brush Warn => R("Warn");
    public static Brush Ok => R("Ok");
    public static Brush Danger => R("Danger");
    /// <summary>Selected fill for non-GPU choices (Auto, All, Windows).</summary>
    public static Brush AutoBrush => R("Primary");

    /// <summary>GPU colors go by role, not by model: the GPU the monitors are on is always color 0, the others follow in order.</summary>
    static int Role(string? key)
    {
        if (key == null) return -1;
        var ordered = Gpus.All.OrderBy(g => g.IsDisplayGpu ? 0 : 1).ToList(); // stable, keeps Windows' order otherwise
        return ordered.FindIndex(g => g.Key == key);
    }

    public static Brush GpuBrush(string? key) => Role(key) is var i and >= 0 ? R($"Gpu{i % 4}") : Sub;
    /// <summary>Light tint of the GPU's color for pills and selected cards.</summary>
    public static Brush GpuSoft(string? key) => Role(key) is var i and >= 0 ? R($"Gpu{i % 4}Soft") : Brushes.Transparent;

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? IconFor(string path)
    {
        if (IconCache.TryGetValue(path, out var img)) return img;
        try
        {
            if (File.Exists(path))
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico != null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    img = src;
                }
            }
        }
        catch { }
        return IconCache[path] = img;
    }

    public static string Bytes(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : $"{b >> 20} MB";
}

public class ChoiceVm : Vm
{
    public required string Label { get; init; }
    public string? Key { get; init; }
    public required Brush SelectedBrush { get; init; }
    public required object Owner { get; init; }
    bool _sel;
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }

    public static ObservableCollection<ChoiceVm> Build(object owner, bool includeAuto, string autoLabel = "Auto")
    {
        var list = new ObservableCollection<ChoiceVm>();
        if (includeAuto) list.Add(new ChoiceVm { Label = autoLabel, Key = null, SelectedBrush = Look.AutoBrush, Owner = owner });
        foreach (var g in Gpus.All)
            list.Add(new ChoiceVm { Label = g.ShortName.Replace("RTX ", ""), Key = g.Key, SelectedBrush = Look.GpuBrush(g.Key), Owner = owner });
        return list;
    }

    public static void Select(IEnumerable<ChoiceVm> choices, string? key)
    {
        foreach (var c in choices) c.IsSelected = string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase);
    }
}

public enum RowAction { None, SwitchLive, MoveOffLive, Restart }

public class AppRowVm : Vm
{
    public required string ExePath { get; init; }
    public string ExeName => Path.GetFileName(ExePath);
    public string Name { get; set; } = "";
    public ImageSource? Icon { get; set; }
    ObservableCollection<ChoiceVm> _choices = new();
    public ObservableCollection<ChoiceVm> Choices { get => _choices; set => Set(ref _choices, value); }

    string? _onKey; public string? OnKey { get => _onKey; set { if (Set(ref _onKey, value)) { Raise(nameof(OnText)); Raise(nameof(OnBrush)); Raise(nameof(OnSoft)); } } }
    public string OnText => Gpus.ByKey(OnKey)?.ShortName.Replace("RTX ", "").Replace("GTX ", "") ?? "-";
    public Brush OnBrush => Look.GpuBrush(OnKey);
    public Brush OnSoft => Look.GpuSoft(OnKey);

    double _util; public double Util { get => _util; set { if (Set(ref _util, value)) Raise(nameof(UtilText)); } }
    public string UtilText => Util < 0.05 ? "0%" : $"{Util:0.#}%";
    long _mem; public long Mem { get => _mem; set { if (Set(ref _mem, value)) Raise(nameof(MemText)); } }
    public string MemText => Look.Bytes(Mem);

    string? _prefKey; public string? PrefKey { get => _prefKey; set { if (Set(ref _prefKey, value)) { Raise(nameof(PrefBrush)); Raise(nameof(PrefSoft)); } } }
    /// <summary>The action button moves the app to its chosen GPU, so it wears that GPU's color.</summary>
    public Brush PrefBrush => Look.GpuBrush(PrefKey);
    public Brush PrefSoft => Look.GpuSoft(PrefKey);

    LiveKind _kind; public LiveKind Kind { get => _kind; set { if (Set(ref _kind, value)) { Raise(nameof(KindText)); Raise(nameof(KindBrush)); } } }
    string _kindNote = ""; public string KindNote { get => _kindNote; set => Set(ref _kindNote, value); }
    public string KindText => Kind switch
    {
        LiveKind.Chromium => "Live switch",
        LiveKind.SurvivesDeviceLoss => $"Live: off {Gpus.All.FirstOrDefault(g => !g.IsDisplayGpu)?.ShortName.Replace("RTX ", "") ?? "2nd GPU"} only",
        LiveKind.CrashesOnDeviceLoss => "Restart only",
        LiveKind.Game => "Game: restart / force",
        _ => "Restart only",
    };
    public Brush KindBrush => Kind switch
    {
        LiveKind.Chromium => Look.Ok,
        LiveKind.SurvivesDeviceLoss => Look.Warn,
        _ => Look.Sub,
    };

    RowAction _action; public RowAction Action { get => _action; set { if (Set(ref _action, value)) { Raise(nameof(ActionText)); Raise(nameof(ActionVisible)); } } }
    string _actionTip = ""; public string ActionTip { get => _actionTip; set => Set(ref _actionTip, value); }
    public string ActionText => Action switch
    {
        RowAction.SwitchLive => "Switch live",
        RowAction.MoveOffLive => "Move live",
        RowAction.Restart => "Restart on " + (Gpus.ByKey(PrefKey)?.ShortName.Replace("RTX ", "") ?? "GPU"),
        _ => "",
    };
    public Visibility ActionVisible => Action == RowAction.None || Busy ? Visibility.Collapsed : Visibility.Visible;

    bool _busy; public bool Busy { get => _busy; set { if (Set(ref _busy, value)) { Raise(nameof(ActionVisible)); Raise(nameof(BusyVisible)); } } }
    public Visibility BusyVisible => Busy ? Visibility.Visible : Visibility.Collapsed;

    bool _force; public bool Force { get => _force; set => Set(ref _force, value); }
    bool _forceAllowed; public bool ForceAllowed { get => _forceAllowed; set => Set(ref _forceAllowed, value); }
    string _forceTip = ""; public string ForceTip { get => _forceTip; set => Set(ref _forceTip, value); }

    public void RefreshActionText() => Raise(nameof(ActionText));
}

public class SavedRowVm : Vm
{
    public required string ExePath { get; init; }
    public string Name { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public bool Missing { get; set; }
    public string SubText => Missing ? ExePath + "  (file not found)" : ExePath;
    public ObservableCollection<ChoiceVm> Choices { get; set; } = new();
    public bool AutoAssigned { get; set; }
    public Visibility BadgeVisible => AutoAssigned ? Visibility.Visible : Visibility.Collapsed;
}

public class ForceRuleVm : Vm
{
    public required ForceRule Rule { get; init; }
    public string Title => string.IsNullOrEmpty(Rule.Label) ? Rule.GameExe : Rule.Label;
    public string Detail => $"Hides {Gpus.ByKey(Rule.DisableGpuKey)?.ShortName ?? "?"} while {Rule.GameExe} starts.  Triggers: {string.Join(", ", Rule.Triggers)}";
    public bool Enabled
    {
        get => Rule.Enabled;
        set { Rule.Enabled = value; AppSettings.Current.Save(); }
    }
}

/// <summary>One segmented picker inside a startup row (the row has two: mode and GPU).</summary>
public class StartupPicker
{
    public required string Kind { get; init; } // "mode" or "gpu"
    public required StartupRowVm Row { get; init; }
    public ObservableCollection<ChoiceVm> Choices { get; set; } = new();
}

public class StartupRowVm : Vm
{
    public required StartupItem Item { get; init; }
    public string Name => Item.Name;
    public string Sub => $"{Item.SourceText}  ·  {Item.Command}";
    public ImageSource? Icon { get; set; }
    public StartupPicker? Mode { get; set; }
    public StartupPicker? Gpu { get; set; }
    public Visibility GpuVisible => Gpu != null ? Visibility.Visible : Visibility.Collapsed;

    public const string Windows = "windows", Switcher = "switcher", Off = "off";
}

public class CompatRowVm
{
    public required string Exe { get; init; }
    public required string Kind { get; init; }
    public required string Note { get; init; }
    public Brush Brush { get; init; } = Look.Sub;
}

public class GpuCardVm : Vm
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    public Brush Brush { get; set; } = Look.Sub;
    public Brush Soft { get; set; } = Brushes.Transparent;
    public string Badge { get; set; } = "";
    public Visibility BadgeVisible => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
    double _util; public double Util { get => _util; set { if (Set(ref _util, value)) Raise(nameof(UtilText)); } }
    public string UtilText => $"{Util:0}%";
    string _memText = ""; public string MemText { get => _memText; set => Set(ref _memText, value); }
    string _state = ""; public string State { get => _state; set => Set(ref _state, value); }
    Brush _stateBrush = Look.Sub; public Brush StateBrush { get => _stateBrush; set => Set(ref _stateBrush, value); }
    /// <summary>True while the Running list is filtered to this GPU (card gets its color).</summary>
    bool _sel; public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
}
