using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GpuSwitcher.Core;
using GpuSwitcher.UI;

namespace GpuSwitcher;

public partial class MainWindow : Window
{
    readonly ObservableCollection<AppRowVm> _rows = new();
    readonly Dictionary<string, AppRowVm> _rowMap = new(StringComparer.OrdinalIgnoreCase);
    readonly ObservableCollection<GpuCardVm> _cards = new();
    readonly ObservableCollection<SavedRowVm> _saved = new();
    readonly ObservableCollection<ForceRuleVm> _rules = new();
    readonly ObservableCollection<string> _activity = new();
    List<RunningApp> _lastApps = new();
    string _gpuSignature = "";

    // stand-in owners for the settings pickers
    sealed class PickerOwner { public required string Which { get; init; } public ObservableCollection<ChoiceVm> Choices { get; set; } = new(); }
    readonly PickerOwner _autoApps = new() { Which = "apps" };
    readonly PickerOwner _autoGames = new() { Which = "games" };
    readonly PickerOwner _filter = new() { Which = "filter" };
    string? _filterKey;
    bool _fixing;

    public MainWindow()
    {
        InitializeComponent();
        RunningList.ItemsSource = _rows;
        GpuCards.ItemsSource = _cards;
        SavedList.ItemsSource = _saved;
        ForceList.ItemsSource = _rules;
        ActivityList.ItemsSource = _activity;
        ShowSystem.IsChecked = AppSettings.Current.ShowSystemProcesses;

        Log.Message += msg => Dispatcher.BeginInvoke(() =>
        {
            _activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {msg}");
            while (_activity.Count > 300) _activity.RemoveAt(_activity.Count - 1);
        });
        DeviceGuard.BusyChanged += busy => Dispatcher.BeginInvoke(() => BusyBanner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed);
        Preferences.Changed += () => Dispatcher.BeginInvoke(() => { if (Tabs.SelectedIndex == 1) RefreshSaved(); });
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != Tabs) return;
            if (Tabs.SelectedIndex == 1) RefreshSaved();
            if (Tabs.SelectedIndex == 2) RefreshRules();
            if (Tabs.SelectedIndex == 3) RefreshStartup();
            if (Tabs.SelectedIndex == 4) RefreshSettings();
        };
        SourceInitialized += (_, _) =>
        {
            Theme.ApplyMica(this);
            // Windows broadcasts this when light/dark is switched
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr lParam, ref bool _) =>
            {
                if (Theme.IsThemeMessage(msg, lParam)) Dispatcher.BeginInvoke(() => Theme.Refresh());
                return IntPtr.Zero;
            });
        };
        Theme.Changed += OnThemeChanged;
        Closing += (_, e) =>
        {
            if (App.Exiting) return;
            e.Cancel = true;
            Hide();
            ((App)Application.Current).HiddenToTray();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Theme.Refresh(); // may have switched while we were in the tray
            if (_themeDirty) RecolorAll();
            OnData(_lastApps, LiveSwitch.Monitor?.Latest ?? new GpuSnapshot());
        };
    }

    // ------------------------------------------------------------------ light / dark

    bool _themeDirty;

    void OnThemeChanged()
    {
        Theme.ApplyTitleBar(this);
        if (IsVisible) { RecolorAll(); OnData(_lastApps, LiveSwitch.Monitor?.Latest ?? new GpuSnapshot()); }
        else _themeDirty = true;
    }

    /// <summary>Brushes from code are looked up once, so rebuild everything that holds one.</summary>
    void RecolorAll()
    {
        _themeDirty = false;
        _gpuSignature = ""; // cards, filter and row pickers get rebuilt on the next OnData
        foreach (var r in _rows) r.RaiseAll();
        if (Tabs.SelectedIndex == 1) RefreshSaved();
        if (Tabs.SelectedIndex == 3) RefreshStartup();
        if (Tabs.SelectedIndex == 4) RefreshSettings();
    }

    // ------------------------------------------------------------------ data

    /// <summary>Called from the monitor thread.</summary>
    public void PostData(List<RunningApp> apps, GpuSnapshot snap)
    {
        _lastApps = apps;
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(() => OnData(apps, snap));
    }

    void OnData(List<RunningApp> apps, GpuSnapshot snap)
    {
        UpdateCards(snap);

        var search = Search.Text.Trim();
        bool showSys = ShowSystem.IsChecked == true;
        var visible = apps.Where(a => (showSys || !a.IsSystem) && a.ExePath != "System" || showSys && a.ExePath == "System")
            .Where(a => search.Length == 0 || a.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                          || a.ExeName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(a => _filterKey == null || a.PrimaryGpuKey == _filterKey)
            .ToList();

        int wrong = apps.Count(a => !a.IsSystem && TargetIfWrong(a) != null);
        FixAllButton.Content = _fixing ? "Working…" : wrong == 0 ? "Everything is on its GPU ✓" : $"Put everything on its GPU ({wrong})";
        FixAllButton.IsEnabled = !_fixing && wrong > 0;

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in visible)
        {
            keep.Add(a.ExePath);
            if (!_rowMap.TryGetValue(a.ExePath, out var row))
            {
                row = new AppRowVm { ExePath = a.ExePath, Name = a.DisplayName, Icon = Look.IconFor(a.ExePath) };
                row.Choices = ChoiceVm.Build(row, true);
                _rowMap[a.ExePath] = row;
                _rows.Add(row);
            }
            row.OnKey = a.PrimaryGpuKey;
            row.Util = a.TotalUtil;
            row.Mem = a.Mem.Values.Sum();
            UpdateRowState(row);
        }
        foreach (var gone in _rowMap.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            if (_rowMap[gone].Busy) continue;
            _rows.Remove(_rowMap[gone]);
            _rowMap.Remove(gone);
        }

        // stable order: by GPU it's on, then busiest first in coarse steps, then name
        var gpuOrder = Gpus.All.Select(g => g.Key).ToList();
        var sorted = _rows.OrderBy(r => r.OnKey == null ? 99 : gpuOrder.IndexOf(r.OnKey))
            .ThenByDescending(r => r.Util >= 1 ? 1 : 0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = _rows.IndexOf(sorted[i]);
            if (cur != i) _rows.Move(cur, i);
        }

        StatusIdle();
    }

    void UpdateRowState(AppRowVm row)
    {
        row.PrefKey = Preferences.GetGpuKey(row.ExePath);
        ChoiceVm.Select(row.Choices, row.PrefKey);
        var compat = Compat.For(row.ExePath);
        row.Kind = compat.Kind;
        row.KindNote = compat.Note;

        var pref = Gpus.ByKey(row.PrefKey);
        var on = Gpus.ByKey(row.OnKey);
        if (pref == null || on == null || pref.Key == on.Key || pref.IsDisabled) row.Action = RowAction.None;
        else if (row.Kind == LiveKind.Chromium)
        {
            row.Action = RowAction.SwitchLive;
            row.ActionTip = $"Restarts only its GPU process, it comes back on {pref.ShortName}. Nothing else is affected.";
        }
        else if (row.Kind == LiveKind.SurvivesDeviceLoss && !on.IsDisplayGpu)
        {
            row.Action = RowAction.MoveOffLive;
            row.ActionTip = $"Turns {on.ShortName} off for about 5 seconds, so everything on it moves to {pref.ShortName}.";
        }
        else if (LiveSwitch.Terminals.Contains(row.ExeName) || row.ExePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            row.Action = RowAction.None;
            row.KindNote = $"Restart it yourself to move it to {pref.ShortName} ("
                + (LiveSwitch.Terminals.Contains(row.ExeName) ? "restarting a terminal from here would close everything in it" : "Store apps can't be relaunched from here") + ").";
        }
        else
        {
            row.Action = RowAction.Restart;
            row.ActionTip = $"Closes it and opens it again on {pref.ShortName}.";
        }
        row.RefreshActionText();

        var target = pref ?? Gpus.DisplayGpu;
        var hide = Gpus.All.FirstOrDefault(g => g.Key != target?.Key && !g.IsDisplayGpu);
        row.Force = AppSettings.Current.ForceRules.Any(r => r.GameExe.Equals(row.ExeName, StringComparison.OrdinalIgnoreCase));
        row.ForceAllowed = row.Force || hide != null;
        row.ForceTip = hide != null
            ? $"Force mode: every time {row.ExeName} starts, {hide.ShortName} is turned off until the game has picked {target?.ShortName}."
            : $"Force mode can only hide a GPU without monitors. Forcing onto {target?.ShortName} would mean turning off your monitors' GPU.";
    }

    void UpdateCards(GpuSnapshot snap)
    {
        var gpus = Gpus.All;
        var sig = string.Join("|", gpus.Select(g => g.Key));
        if (sig != _gpuSignature)
        {
            _gpuSignature = sig;
            _cards.Clear();
            double width = gpus.Count <= 2 ? 250 : gpus.Count == 3 ? 210 : 175;
            foreach (var g in gpus)
                _cards.Add(new GpuCardVm { Key = g.Key, Name = g.ShortName, Brush = Look.GpuBrush(g.Key), Soft = Look.GpuSoft(g.Key), Width = width, Badge = g.IsDisplayGpu ? "monitors" : "" });
            OneGpuBanner.Visibility = gpus.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
            var others = gpus.Where(g => !g.IsDisplayGpu).ToList();
            // with 3+ GPUs there's one button per extra GPU, so keep them short
            EvacButtons.ItemsSource = others.Select(g => new { Label = others.Count == 1 ? $"Move everything off {g.ShortName}" : $"Move all off {Look.Label(g)}", g.Key }).ToList();
            foreach (var r in _rows) r.Choices = ChoiceVm.Build(r, true);
            if (_filterKey != null && gpus.All(g => g.Key != _filterKey)) _filterKey = null;
            _filter.Choices = ChoiceVm.Build(_filter, true, "All");
            GpuFilterPicker.Content = null;
            GpuFilterPicker.Content = _filter;
            ShowFilter();
        }
        foreach (var c in _cards)
        {
            var g = gpus.First(x => x.Key == c.Key);
            c.Util = snap.GpuUtil.GetValueOrDefault(c.Key);
            c.MemText = $"{Look.Bytes(snap.GpuMem.GetValueOrDefault(c.Key))} / {Look.Bytes((long)g.VramBytes)}";
            c.State = g.IsDisabled ? "temporarily off" : "on";
            c.StateBrush = g.IsDisabled ? Look.Warn : Look.Ok;
        }
    }

    DateTime _statusUntil;
    void Status(string msg, int seconds = 8)
    {
        StatusText.Text = msg;
        _statusUntil = DateTime.Now.AddSeconds(seconds);
    }
    void StatusIdle()
    {
        if (DateTime.Now < _statusUntil) return;
        var s = AppSettings.Current;
        StatusText.Text = $"{_lastApps.Count(a => !a.IsSystem)} apps using a GPU"
                          + (s.AutoAssignEnabled ? $"  ·  auto-assign on ({s.AutoAssigned.Count} set)" : "")
                          + (s.ForceRules.Any(r => r.Enabled) ? $"  ·  force mode watching {s.ForceRules.Count(r => r.Enabled)} game(s)" : "");
    }

    // ------------------------------------------------------------------ choice clicks

    async void Choice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChoiceVm c) return;
        switch (c.Owner)
        {
            case AppRowVm row:
                if (string.Equals(row.PrefKey, c.Key, StringComparison.OrdinalIgnoreCase)) return;
                Preferences.SetGpu(row.ExePath, c.Key);
                AutoAssign.MarkManual(row.ExePath);
                FixForceRuleAfterPrefChange(row.ExeName, c.Key);
                UpdateRowState(row);
                if (row.Action == RowAction.SwitchLive) await DoAction(row);
                else Status(row.Action == RowAction.None
                    ? $"Saved: {row.Name} uses {Gpus.ByKey(c.Key)?.ShortName ?? "Auto"}."
                    : $"Saved: {row.Name} will use {Gpus.ByKey(c.Key)?.ShortName} next launch. Use '{row.ActionText}' to switch now.");
                break;

            case SavedRowVm saved:
                Preferences.SetGpu(saved.ExePath, c.Key);
                AutoAssign.MarkManual(saved.ExePath);
                FixForceRuleAfterPrefChange(Path.GetFileName(saved.ExePath), c.Key);
                RefreshSaved();
                break;

            case StartupPicker sp:
                StartupPick(sp, c.Key);
                break;

            case PickerOwner { Which: "filter" }:
                SetFilter(c.Key);
                break;

            case PickerOwner p:
                if (p.Which == "apps") AppSettings.Current.AutoAppsGpuKey = c.Key;
                else AppSettings.Current.AutoGamesGpuKey = c.Key;
                AppSettings.Current.Save();
                ChoiceVm.Select(p.Choices, c.Key);
                break;
        }
    }

    /// <summary>A force rule hides one GPU; if the app is now set to that GPU, the rule makes no sense anymore.</summary>
    void FixForceRuleAfterPrefChange(string exeName, string? newKey)
    {
        var s = AppSettings.Current;
        var bad = s.ForceRules.Where(r => r.GameExe.Equals(exeName, StringComparison.OrdinalIgnoreCase) && r.DisableGpuKey == newKey).ToList();
        if (bad.Count == 0) return;
        foreach (var r in bad) s.ForceRules.Remove(r);
        s.Save();
        Status($"Force mode turned off for {exeName}: it would hide the GPU you just picked.");
    }

    // ------------------------------------------------------------------ GPU filter

    void SetFilter(string? key)
    {
        _filterKey = key;
        ShowFilter();
        Tabs.SelectedIndex = 0;
        OnData(_lastApps, LiveSwitch.Monitor?.Latest ?? new GpuSnapshot());
    }

    void ShowFilter()
    {
        ChoiceVm.Select(_filter.Choices, _filterKey);
        foreach (var c in _cards) c.IsSelected = c.Key == _filterKey;
    }

    void GpuCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag as string;
        SetFilter(_filterKey == key ? null : key);
    }

    // ------------------------------------------------------------------ put everything on its GPU

    /// <summary>The GPU this app is set to, if it's currently running somewhere else. Null = fine / no choice.</summary>
    static string? TargetIfWrong(RunningApp a)
    {
        var pref = Gpus.ByKey(Preferences.GetGpuKey(a.ExePath));
        if (pref == null || pref.IsDisabled || a.PrimaryGpuKey == null || a.PrimaryGpuKey == pref.Key) return null;
        return pref.Key;
    }

    async void FixAll_Click(object sender, RoutedEventArgs e) => await FixAll();

    public async Task FixAll()
    {
        if (_fixing) return;
        var wrong = _lastApps.Where(a => !a.IsSystem)
            .Select(a => (App: a, Target: TargetIfWrong(a), Kind: Compat.For(a.ExePath).Kind))
            .Where(x => x.Target != null).ToList();
        if (wrong.Count == 0) { Status("Everything is already on its GPU."); return; }

        var live = wrong.Where(x => x.Kind == LiveKind.Chromium).ToList();
        var games = wrong.Where(x => x.Kind == LiveKind.Game).ToList();
        var skipped = wrong.Where(x => x.Kind != LiveKind.Chromium && x.Kind != LiveKind.Game && LiveSwitch.WhyNotRestart(x.App) != null)
            .Select(x => (x.App, x.Target, x.Kind, Why: LiveSwitch.WhyNotRestart(x.App)!)).ToList();
        var restart = wrong.Except(live).Except(games).Where(x => skipped.All(s => s.App != x.App)).ToList();

        string Names(IEnumerable<(RunningApp App, string? Target, LiveKind Kind)> l) =>
            string.Join(", ", l.Select(x => $"{x.App.DisplayName} → {Look.Label(x.Target)}"));

        if (restart.Count > 0)
        {
            var msg = (live.Count > 0 ? $"Switch live: {Names(live)}\n\n" : "")
                      + $"Restart: {Names(restart)}\nApps that don't close by themselves (tray apps) get force-closed.\n\n"
                      + (games.Count > 0 ? $"Skipped games, restart these yourself: {Names(games)}\n\n" : "")
                      + (skipped.Count > 0 ? "Skipped: " + string.Join(", ", skipped.Select(s => $"{s.App.DisplayName} ({s.Why})")) + "\n\n" : "")
                      + "Anything unsaved in the restarted apps could be lost.";
            if (!Dialog.Confirm("Put everything on its GPU?", msg, "Do it", "Cancel")) return;
        }

        _fixing = true;
        var results = new List<string>();
        try
        {
            foreach (var x in live.Concat(restart))
            {
                _rowMap.TryGetValue(x.App.ExePath, out var row);
                if (row != null) row.Busy = true;
                Status($"Moving {x.App.DisplayName}…", 60);
                try
                {
                    var r = x.Kind == LiveKind.Chromium
                        ? await LiveSwitch.MoveChromium(x.App.ExePath, x.Target!)
                        : await LiveSwitch.RestartOnGpu(x.App.ExePath, x.Target, () => Task.FromResult(true));
                    results.Add($"{x.App.DisplayName}: {r}");
                }
                catch (Exception ex) { results.Add($"{x.App.DisplayName}: {ex.Message}"); }
                finally { if (row != null) row.Busy = false; }
            }
        }
        finally { _fixing = false; }

        foreach (var g in games) results.Add($"{g.App.DisplayName}: game, restart it yourself to move it");
        foreach (var s in skipped) results.Add($"{s.App.DisplayName}: skipped ({s.Why})");
        Status("Done.");
        Dialog.Info("Done", string.Join("\n", results));
    }

    // ------------------------------------------------------------------ row actions

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppRowVm row) await DoAction(row);
    }

    async Task DoAction(AppRowVm row)
    {
        var pref = Gpus.ByKey(row.PrefKey);
        if (pref == null) return;
        try
        {
            switch (row.Action)
            {
                case RowAction.SwitchLive:
                    row.Busy = true;
                    Status($"Moving {row.Name} to {pref.ShortName}…");
                    Status(await LiveSwitch.MoveChromium(row.ExePath, pref.Key));
                    break;

                case RowAction.MoveOffLive:
                    var on = Gpus.ByKey(row.OnKey);
                    if (on != null) await Evacuate(on);
                    break;

                case RowAction.Restart:
                    var gameNote = row.Kind == LiveKind.Game ? "\n\nGames lose unsaved progress. Launcher games might need to be started from their launcher again." : "";
                    if (!Dialog.Confirm($"Restart {row.Name}?", $"It'll be closed and opened again on {pref.ShortName}.{gameNote}")) return;
                    row.Busy = true;
                    Status($"Restarting {row.Name}…");
                    Status(await LiveSwitch.RestartOnGpu(row.ExePath, pref.Key, () => Task.FromResult(
                        Dialog.Confirm($"{row.Name} won't close", "It didn't close by itself (it might be hiding in the tray). Force close it?"))));
                    break;
            }
        }
        catch (Exception ex) { Status(ex.Message, 12); }
        finally { row.Busy = false; }
    }

    async void Evacuate_Click(object sender, RoutedEventArgs e)
    {
        var gpu = Gpus.ByKey((sender as FrameworkElement)?.Tag as string);
        if (gpu != null) await Evacuate(gpu);
    }

    async Task Evacuate(GpuAdapter gpu)
    {
        var why = DeviceGuard.WhyNot(gpu);
        if (why != null) { Dialog.Info("Can't do that", why); return; }
        var other = Gpus.All.FirstOrDefault(g => g.Key != gpu.Key)?.ShortName ?? "the other GPU";
        var on = _lastApps.Where(a => !a.IsSystem && a.Touches.Contains(gpu.Key)).Select(a => a.DisplayName).Distinct().ToList();
        var list = on.Count == 0 ? "Nothing is using it right now." : "Using it right now: " + string.Join(", ", on) + ".";
        if (!Dialog.Confirm($"Turn {gpu.ShortName} off for 5 seconds?",
                $"{list}\n\nApps that can handle it move to {other}. Others may crash or restart. Your monitors stay on.\n\n" +
                $"Apps stay on {other} afterwards, they don't move back when {gpu.ShortName} comes back.",
                "Turn it off", "Cancel")) return;
        try
        {
            Status($"{gpu.ShortName} is off for a few seconds…", 30);
            var results = await LiveSwitch.MoveEverythingOff(gpu);
            Status($"{gpu.ShortName} is back on.");
            Dialog.Info($"{gpu.ShortName} is back on",
                results.Count == 0 ? "Nothing was using it." : string.Join("\n", results.Select(r => $"{r.App}: {r.Outcome}")));
        }
        catch (Exception ex) { Dialog.Info("That didn't work", ex.Message); }
    }

    void Force_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRowVm row) return;
        var s = AppSettings.Current;
        var existing = s.ForceRules.Where(r => r.GameExe.Equals(row.ExeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (existing.Count > 0)
        {
            foreach (var r in existing) s.ForceRules.Remove(r);
            s.Save();
            Status($"Force mode off for {row.Name}.");
        }
        else
        {
            var target = Gpus.ByKey(row.PrefKey) ?? Gpus.DisplayGpu;
            var hide = Gpus.All.FirstOrDefault(g => g.Key != target?.Key && !g.IsDisplayGpu);
            if (hide == null || target == null) { UpdateRowState(row); return; }
            var rule = row.ExeName.StartsWith("FortniteClient", StringComparison.OrdinalIgnoreCase)
                ? ForceMode.FortnitePreset(hide.Key)
                : new ForceRule { GameExe = row.ExeName, Triggers = new() { row.ExeName }, DisableGpuKey = hide.Key, Label = row.Name };
            s.ForceRules.Add(rule);
            if (row.PrefKey == null) { Preferences.SetGpu(row.ExePath, target.Key); AutoAssign.MarkManual(row.ExePath); }
            s.Save();
            Status($"Force mode on: next time {row.Name} starts, {hide.ShortName} goes off until it has picked {target.ShortName}.", 12);
        }
        UpdateRowState(row);
    }

    // ------------------------------------------------------------------ tabs

    void RefreshSaved()
    {
        _saved.Clear();
        var auto = AppSettings.Current.AutoAssigned;
        foreach (var (exe, key, _) in Preferences.All().OrderBy(x => Path.GetFileName(x.Exe), StringComparer.OrdinalIgnoreCase))
        {
            bool isPath = exe.Contains('\\');
            var row = new SavedRowVm
            {
                ExePath = exe,
                Name = isPath ? Apps.FriendlyName(exe) : exe,
                Icon = isPath ? Look.IconFor(exe) : null,
                Missing = isPath && !File.Exists(exe),
                AutoAssigned = auto.ContainsKey(exe),
            };
            row.Choices = ChoiceVm.Build(row, true);
            ChoiceVm.Select(row.Choices, key);
            _saved.Add(row);
        }
        int missing = _saved.Count(r => r.Missing);
        RemoveMissing.Content = $"Remove missing ({missing})";
        RemoveMissing.IsEnabled = missing > 0;
    }

    void RemoveMissing_Click(object sender, RoutedEventArgs e)
    {
        var missing = _saved.Where(r => r.Missing).ToList();
        if (missing.Count == 0) { Status("Nothing to remove, every saved app still exists."); return; }
        if (!Dialog.Confirm("Remove missing apps?", $"Removes the GPU choice for {missing.Count} apps that aren't installed anymore:\n\n" +
                string.Join("\n", missing.Take(15).Select(m => m.ExePath)) + (missing.Count > 15 ? $"\n…and {missing.Count - 15} more" : ""))) return;
        foreach (var m in missing) Preferences.SetGpu(m.ExePath, null);
        RefreshSaved();
        Status($"Removed {missing.Count} entries.");
    }

    // ------------------------------------------------------------------ startup apps

    void RefreshStartup()
    {
        var s = AppSettings.Current;
        var items = StartupManager.List();
        var rows = new List<StartupRowVm>();
        foreach (var i in items)
        {
            var row = new StartupRowVm { Item = i, Icon = Look.IconFor(i.TargetExe ?? i.ExePath ?? "") };
            row.Mode = new StartupPicker { Kind = "mode", Row = row };
            row.Mode.Choices = new()
            {
                new ChoiceVm { Label = "Windows", Key = StartupRowVm.Windows, SelectedBrush = Look.Sub, Owner = row.Mode },
                new ChoiceVm { Label = "GPU Switcher", Key = StartupRowVm.Switcher, SelectedBrush = Look.AutoBrush, Owner = row.Mode },
                new ChoiceVm { Label = "Off", Key = StartupRowVm.Off, SelectedBrush = Look.Sub, Owner = row.Mode },
            };
            var managed = s.ManagedStartup.FirstOrDefault(m => m.Id == i.Id);
            ChoiceVm.Select(row.Mode.Choices,
                managed != null ? (managed.Launch ? StartupRowVm.Switcher : StartupRowVm.Off)
                : i.EnabledInWindows ? StartupRowVm.Windows : StartupRowVm.Off);
            if (i.TargetExe != null && !i.IsWindowsComponent)
            {
                row.Gpu = new StartupPicker { Kind = "gpu", Row = row };
                row.Gpu.Choices = ChoiceVm.Build(row.Gpu, true);
                ChoiceVm.Select(row.Gpu.Choices, Preferences.GetGpuKey(i.TargetExe));
            }
            rows.Add(row);
        }
        // taken-over apps first (that's the launch order), then the rest
        StartupList.ItemsSource = rows.OrderBy(r => s.ManagedStartup.FindIndex(m => m.Id == r.Item.Id) is var x && x >= 0 ? x : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        StartupTakeNew.IsChecked = s.StartupTakeOverNew;
        StartupGap.Text = (s.StartupGapMs / 1000.0).ToString("0.#");
        bool any = s.ManagedStartup.Any(m => m.Launch);
        StartupGiveAll.IsEnabled = s.ManagedStartup.Count > 0;
        StartupTest.IsEnabled = any;
        StartupWarning.Visibility = any && !Core.Autostart.IsEnabled() ? Visibility.Visible : Visibility.Collapsed;
    }

    void StartupPick(StartupPicker sp, string? key)
    {
        var i = sp.Row.Item;
        try
        {
            if (sp.Kind == "gpu")
            {
                Preferences.SetGpu(i.TargetExe!, key);
                AutoAssign.MarkManual(i.TargetExe!);
                ChoiceVm.Select(sp.Choices, key);
                Status($"{i.Name} will start on {Gpus.ByKey(key)?.ShortName ?? "Auto"}.");
                return;
            }
            switch (key)
            {
                case StartupRowVm.Windows: StartupManager.GiveBack(i, enabled: true); break;
                case StartupRowVm.Switcher: StartupManager.TakeOver(i); break;
                case StartupRowVm.Off: StartupManager.TurnOff(i); break;
            }
        }
        catch (Exception ex) { Dialog.Info("Couldn't change that", ex.Message); }
        RefreshStartup();
    }

    void StartupTakeAll_Click(object sender, RoutedEventArgs e)
    {
        var candidates = StartupManager.List().Where(StartupManager.GoodCandidate).ToList();
        if (candidates.Count == 0) { Status("Nothing to take over: every app that's on is already handled (Windows/driver stuff stays with Windows)."); return; }
        var autostartNote = Core.Autostart.IsEnabled() ? "" : "\n\n'Start with Windows' gets turned on too, otherwise they wouldn't start.";
        if (!Dialog.Confirm("Take over startup apps?",
                $"From now on GPU Switcher starts these, after fixing their GPU choice:\n\n{string.Join(", ", candidates.Select(c => c.Name))}\n\n" +
                "Apps you turned off stay off. Windows and driver stuff stays with Windows. 'Give all back' undoes this." + autostartNote)) return;
        try
        {
            if (!Core.Autostart.IsEnabled()) Core.Autostart.Enable();
            foreach (var c in candidates) StartupManager.TakeOver(c);
            Status($"GPU Switcher now starts {candidates.Count} apps.");
        }
        catch (Exception ex) { Dialog.Info("Couldn't take them over", ex.Message); }
        RefreshStartup();
    }

    async void StartupAdd_Click(object sender, RoutedEventArgs e)
    {
        Status("Looking for apps…");
        var running = _lastApps;
        var candidates = await Task.Run(() => StartupManager.AddCandidates(running));
        StatusIdle();
        var pick = Dialog.PickApp("Add a startup app", candidates);
        if (pick == "browse")
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick a program to start with Windows",
                Filter = "Programs and shortcuts|*.exe;*.lnk",
                DereferenceLinks = false,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            pick = dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }
        if (pick == null) return;
        try
        {
            if (!Core.Autostart.IsEnabled()) Core.Autostart.Enable();
            var item = StartupManager.Add(pick);
            Status($"{item.Name} now starts with Windows, through GPU Switcher. Pick its GPU in the list.", 12);
        }
        catch (Exception ex) { Dialog.Info("Couldn't add it", ex.Message); }
        RefreshStartup();
    }

    void StartupGiveAll_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialog.Confirm("Give all back to Windows?", "Every app GPU Switcher took over goes back to how it was before (on or off).")) return;
        Status($"Gave {StartupManager.GiveBackAll()} apps back to Windows.");
        RefreshStartup();
    }

    async void StartupTest_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialog.Confirm("Launch them now?", "Starts every app GPU Switcher manages, the same way it does at logon. Apps that are already running usually just stay as they are, some may open their window.")) return;
        StartupTest.IsEnabled = false;
        Status("Launching startup apps…", 60);
        var results = await Task.Run(StartupManager.LaunchAll);
        StartupTest.IsEnabled = true;
        Status("Done.");
        Dialog.Info("Startup apps", string.Join("\n", results));
    }

    void StartupTakeNew_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.StartupTakeOverNew = StartupTakeNew.IsChecked == true;
        AppSettings.Current.Save();
    }

    void StartupGap_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(StartupGap.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
        {
            AppSettings.Current.StartupGapMs = (int)(Math.Clamp(sec, 0, 30) * 1000);
            AppSettings.Current.Save();
        }
        StartupGap.Text = (AppSettings.Current.StartupGapMs / 1000.0).ToString("0.#");
    }

    void StartupAutostartOn_Click(object sender, RoutedEventArgs e)
    {
        try { Core.Autostart.Enable(); } catch (Exception ex) { Dialog.Info("Couldn't change startup", ex.Message); }
        RefreshStartup();
    }

    void RefreshRules()
    {
        _rules.Clear();
        foreach (var r in AppSettings.Current.ForceRules) _rules.Add(new ForceRuleVm { Rule = r });
    }

    void RefreshSettings()
    {
        var s = AppSettings.Current;
        AutoEnabled.IsChecked = s.AutoAssignEnabled;
        AutoLive.IsChecked = s.AutoLiveMoveChromium;
        ReturnApps.IsChecked = s.ReturnAppsWhenGpuBack;
        Autostart.IsChecked = Core.Autostart.IsEnabled();
        _autoApps.Choices = ChoiceVm.Build(_autoApps, false);
        _autoGames.Choices = ChoiceVm.Build(_autoGames, false);
        ChoiceVm.Select(_autoApps.Choices, s.AutoAppsGpuKey);
        ChoiceVm.Select(_autoGames.Choices, s.AutoGamesGpuKey);
        AutoAppsPicker.Content = null; AutoAppsPicker.Content = _autoApps;
        AutoGamesPicker.Content = null; AutoGamesPicker.Content = _autoGames;
        UndoAuto.Content = $"Undo auto-assign ({s.AutoAssigned.Count})";
        UndoAuto.IsEnabled = s.AutoAssigned.Count > 0;

        static string KindLabel(LiveKind k) => k switch
        {
            LiveKind.Chromium => "Live switch (any direction)",
            LiveKind.SurvivesDeviceLoss => "Survives GPU off (moves)",
            LiveKind.CrashesOnDeviceLoss => "Crashes: restart only",
            LiveKind.Game => "Game: restart / force",
            _ => "Not tested",
        };
        var rows = Compat.BuiltInList().Select(x => (x.Exe, x.Entry)).ToList();
        foreach (var (exe, kind) in s.CompatOverrides)
            if (Enum.TryParse<LiveKind>(kind, out var k)) { rows.RemoveAll(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)); rows.Add((exe, new CompatEntry(k, "Measured on this PC"))); }
        foreach (var a in _lastApps.Where(a => !a.IsSystem && Compat.KnownChromium(a.ExePath) == true && rows.All(r => !r.Exe.Equals(a.ExeName, StringComparison.OrdinalIgnoreCase))))
            rows.Add((a.ExeName, new CompatEntry(LiveKind.Chromium, "Detected: Chromium GPU process")));
        CompatList.ItemsSource = rows.OrderBy(r => r.Entry.Kind).ThenBy(r => r.Exe).Select(r => new CompatRowVm
        {
            Exe = r.Exe, Kind = KindLabel(r.Entry.Kind), Note = r.Entry.Note,
            Brush = r.Entry.Kind switch { LiveKind.Chromium => Look.Ok, LiveKind.SurvivesDeviceLoss => Look.Warn, LiveKind.CrashesOnDeviceLoss => Look.Danger, _ => Look.Sub },
        }).ToList();
    }

    // ------------------------------------------------------------------ settings handlers

    void AutoEnabled_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        s.AutoAssignEnabled = AutoEnabled.IsChecked == true;
        s.Save();
        Status(s.AutoAssignEnabled
            ? $"Auto-assign on: new apps go to {Gpus.ByKey(s.AutoAppsGpuKey)?.ShortName}, games to {Gpus.ByKey(s.AutoGamesGpuKey)?.ShortName}."
            : "Auto-assign off. Choices it already made stay (use Undo to remove them).");
        RefreshSettings();
    }

    void AutoLive_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AutoLiveMoveChromium = AutoLive.IsChecked == true;
        AppSettings.Current.Save();
    }

    void ReturnApps_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.ReturnAppsWhenGpuBack = ReturnApps.IsChecked == true;
        AppSettings.Current.Save();
    }

    async void AssignInstalled_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        if (Gpus.ByKey(s.AutoAppsGpuKey) == null || Gpus.ByKey(s.AutoGamesGpuKey) == null) { Dialog.Info("Pick the GPUs first", "Choose where apps and games go above."); return; }
        Status("Scanning Start Menu and Desktop shortcuts…");
        var found = await Task.Run(AutoAssign.ScanInstalled);
        if (found.Count == 0) { Dialog.Info("Nothing to do", "Every installed app already has a GPU choice."); return; }
        var apps = found.Where(f => !f.IsGame).ToList();
        var games = found.Where(f => f.IsGame).ToList();
        string Names(List<AutoAssign.Candidate> l) => l.Count == 0 ? "none" : string.Join(", ", l.Take(12).Select(c => c.Name)) + (l.Count > 12 ? $" and {l.Count - 12} more" : "");
        if (!Dialog.Confirm("Assign all installed apps?",
                $"{apps.Count} apps → {Gpus.ByKey(s.AutoAppsGpuKey)?.ShortName}: {Names(apps)}\n\n" +
                $"{games.Count} games → {Gpus.ByKey(s.AutoGamesGpuKey)?.ShortName}: {Names(games)}\n\n" +
                "Apps that already have a choice are skipped. Running apps switch on their next launch. You can undo this.")) return;
        AutoAssign.Apply(found);
        Status($"Done: {apps.Count} apps and {games.Count} games assigned.");
        RefreshSettings();
    }

    void UndoAuto_Click(object sender, RoutedEventArgs e)
    {
        var n = AppSettings.Current.AutoAssigned.Count;
        if (!Dialog.Confirm("Undo auto-assign?", $"Removes the {n} GPU choices auto-assign made (ones you didn't change since) and turns auto-assign off.")) return;
        Status($"Removed {AutoAssign.Undo()} auto-assigned choices.");
        RefreshSettings();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        // without autostart, taken-over startup apps would never start: hand them back first
        if (Autostart.IsChecked != true && AppSettings.Current.ManagedStartup.Any(m => m.Launch))
        {
            if (!Dialog.Confirm("Stop starting with Windows?",
                    "GPU Switcher starts some of your startup apps. They'll be given back to Windows so they still start.", "Turn off", "Cancel"))
            {
                Autostart.IsChecked = true;
                return;
            }
            StartupManager.GiveBackAll();
        }
        try
        {
            if (Autostart.IsChecked == true) Core.Autostart.Enable(); else Core.Autostart.Disable();
            Status(Autostart.IsChecked == true ? "GPU Switcher now starts with Windows." : "Removed from startup.");
        }
        catch (Exception ex) { Dialog.Info("Couldn't change startup", ex.Message); }
        Autostart.IsChecked = Core.Autostart.IsEnabled();
    }

    async void EnableAll_Click(object sender, RoutedEventArgs e)
    {
        var n = await Task.Run(DeviceGuard.EnableAllGpus);
        Status(n == 0 ? "All GPUs are already on." : $"Re-enabled {n} GPU(s).");
    }

    void AddFortnite_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        if (s.ForceRules.Any(r => r.GameExe.StartsWith("FortniteClient", StringComparison.OrdinalIgnoreCase))) { Status("Fortnite is already in the list."); return; }
        var hide = Gpus.All.FirstOrDefault(g => !g.IsDisplayGpu);
        if (hide == null) { Dialog.Info("No GPU to hide", "Force mode needs a second GPU without monitors."); return; }
        s.ForceRules.Add(ForceMode.FortnitePreset(hide.Key));
        s.Save();
        RefreshRules();
        Status($"Fortnite added: {hide.ShortName} goes off for a few seconds whenever Fortnite starts.");
    }

    /// <summary>Force mode for a game that isn't running (the Running tab's Force switch needs it running).</summary>
    void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick the game's exe (Unreal Engine games: the ...-Win64-Shipping.exe)",
            Filter = "Programs|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) != true) return;
        var exe = dlg.FileName;
        var name = Path.GetFileName(exe);
        var s = AppSettings.Current;
        if (s.ForceRules.Any(r => r.GameExe.Equals(name, StringComparison.OrdinalIgnoreCase))) { Status($"{name} is already in the list."); return; }
        // same choice as the Force switch: the game's GPU (or the monitors' GPU), hide one without monitors
        var target = Gpus.ByKey(Preferences.GetGpuKey(exe)) ?? Gpus.DisplayGpu;
        var hide = Gpus.All.FirstOrDefault(g => g.Key != target?.Key && !g.IsDisplayGpu);
        if (hide == null || target == null) { Dialog.Info("No GPU to hide", "Force mode needs a second GPU without monitors."); return; }
        var rule = name.StartsWith("FortniteClient", StringComparison.OrdinalIgnoreCase)
            ? ForceMode.FortnitePreset(hide.Key)
            : new ForceRule { GameExe = name, Triggers = new() { name }, DisableGpuKey = hide.Key, Label = Apps.FriendlyName(exe) };
        s.ForceRules.Add(rule);
        if (Preferences.GetGpuKey(exe) == null) { Preferences.SetGpu(exe, target.Key); AutoAssign.MarkManual(exe); }
        s.Save();
        RefreshRules();
        Status($"Force mode on: next time {rule.Label} starts, {hide.ShortName} goes off until it has picked {target.ShortName}.", 12);
    }

    void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ForceRuleVm vm) return;
        if (!Dialog.Confirm("Remove force mode?", $"Stop forcing {vm.Title}?")) return;
        AppSettings.Current.ForceRules.Remove(vm.Rule);
        AppSettings.Current.Save();
        RefreshRules();
    }

    void ShowSystem_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.ShowSystemProcesses = ShowSystem.IsChecked == true;
        AppSettings.Current.Save();
        OnData(_lastApps, LiveSwitch.Monitor?.Latest ?? new GpuSnapshot());
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        OnData(_lastApps, LiveSwitch.Monitor?.Latest ?? new GpuSnapshot());
    }

    void OpenLogs_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", Log.Dir);

    public void SelectTab(int i) => Tabs.SelectedIndex = i;
}
