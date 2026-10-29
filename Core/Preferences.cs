using Microsoft.Win32;

namespace GpuSwitcher.Core;

/// <summary>
/// Windows per-app GPU preference: HKCU\Software\Microsoft\DirectX\UserGpuPreferences.
/// Value name = full exe path, data = "key=value;" pairs, e.g.
/// "SpecificAdapter=10DE&amp;2504&amp;A630174B;GpuPreference=1073741824;" (24H2+ specific GPU).
/// GpuPreference 0 = let Windows decide, 1 = power saving, 2 = high performance.
/// Other pairs (AutoHDR, SwapEffectUpgrade...) are kept untouched.
/// </summary>
public static class Preferences
{
    const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    const string GlobalName = "DirectXUserGlobalSettings";
    const int SpecificAdapterPref = 0x40000000;

    public static event Action? Changed;

    public static List<KeyValuePair<string, string>> Parse(string? raw)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(raw)) return list;
        foreach (var part in raw.Split(';'))
        {
            var i = part.IndexOf('=');
            if (i <= 0) continue;
            var k = part[..i].Trim();
            var v = part[(i + 1)..].Trim();
            // skip garbage some tools leave behind (non-printable leftovers)
            if (k.Length == 0 || k.Any(c => c < 0x20 || c > 0x7e)) continue;
            list.Add(new(k, v));
        }
        return list;
    }

    static string Join(IEnumerable<KeyValuePair<string, string>> pairs) => string.Concat(pairs.Select(p => $"{p.Key}={p.Value};"));

    public static string? GetRaw(string exePath)
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        return k?.GetValue(exePath) as string;
    }

    /// <summary>GPU key this app is pinned to, or null for "Auto" (no GPU choice saved).</summary>
    public static string? GetGpuKey(string exePath) => Resolve(GetRaw(exePath));

    public static bool HasGpuChoice(string exePath) =>
        Parse(GetRaw(exePath)).Any(p => p.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase) && p.Value != "0");

    static string? Resolve(string? raw)
    {
        var pairs = Parse(raw);
        var pref = pairs.FirstOrDefault(p => p.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)).Value;
        var specific = pairs.FirstOrDefault(p => p.Key.Equals("SpecificAdapter", StringComparison.OrdinalIgnoreCase)).Value;
        if (!int.TryParse(pref, out var n) || n == 0) return null;
        if (n == SpecificAdapterPref) return specific;
        if (n == 2) return GlobalHighPerfKey() ?? Gpus.DisplayGpu?.Key;
        if (n == 1) return GlobalPowerSavingKey();
        return null;
    }

    static string? GlobalSetting(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        var pairs = Parse(k?.GetValue(GlobalName) as string);
        var v = pairs.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static string? GlobalHighPerfKey() => GlobalSetting("HighPerfAdapter");
    public static string? GlobalPowerSavingKey() => GlobalSetting("MinimumPowerAdapter");

    /// <summary>Pin an app to a GPU (key) or clear it with null ("Auto").</summary>
    public static void SetGpu(string exePath, string? gpuKey)
    {
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
        var pairs = Parse(k.GetValue(exePath) as string)
            .Where(p => !p.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)
                     && !p.Key.Equals("SpecificAdapter", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (gpuKey != null)
        {
            pairs.Insert(0, new("GpuPreference", SpecificAdapterPref.ToString()));
            pairs.Insert(0, new("SpecificAdapter", gpuKey));
        }
        if (pairs.Count == 0) k.DeleteValue(exePath, false);
        else k.SetValue(exePath, Join(pairs), RegistryValueKind.String);
        SyncAppIds(k, exePath);
        Log.Write($"pref {Path.GetFileName(exePath)} -> {(gpuKey == null ? "Auto" : Gpus.ByKey(gpuKey)?.ShortName ?? gpuKey)}");
        Changed?.Invoke();
    }

    /// <summary>Store apps (anything under \WindowsApps\) ignore the exe-path value: Windows looks them up by app id
    /// (PackageFamilyName!AppId). We keep the path value as our source of truth and mirror it onto the app id.</summary>
    static void SyncAppIds(RegistryKey k, string exePath)
    {
        var raw = k.GetValue(exePath) as string;
        foreach (var id in AppIdsFor(exePath))
        {
            if (raw == null) k.DeleteValue(id, false);
            else if (k.GetValue(id) as string != raw) { k.SetValue(id, raw, RegistryValueKind.String); Log.Write($"pref app id {id} -> {raw}"); }
        }
    }

    /// <summary>Mirror every saved Store app choice onto its app id (fixes choices saved before app ids were handled).</summary>
    public static void SyncAllAppIds()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            foreach (var name in k.GetValueNames().Where(n => n.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)))
                SyncAppIds(k, name);
        }
        catch (Exception ex) { Log.Write("app id sync: " + ex.Message); }
    }

    static readonly Dictionary<string, List<string>> _appIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App ids of the Store app entries that run this exe, e.g. "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App".</summary>
    static List<string> AppIdsFor(string exePath)
    {
        lock (_appIds)
        {
            if (_appIds.TryGetValue(exePath, out var cached)) return cached;
            var ids = new List<string>();
            try
            {
                // ...\WindowsApps\<Name>_<Version>_<Arch>_<ResourceId>_<PublisherId>\rel\path.exe
                var i = exePath.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    var rest = exePath[(i + 13)..];
                    var slash = rest.IndexOf('\\');
                    var folder = rest[..slash];
                    var rel = rest[(slash + 1)..];
                    var parts = folder.Split('_');
                    if (parts.Length == 5)
                    {
                        var family = $"{parts[0]}_{parts[4]}";
                        var manifest = System.Xml.Linq.XDocument.Load(Path.Combine(exePath[..(i + 13)], folder, "AppxManifest.xml"));
                        foreach (var app in manifest.Descendants().Where(e => e.Name.LocalName == "Application"))
                            if (string.Equals((string?)app.Attribute("Executable"), rel, StringComparison.OrdinalIgnoreCase))
                                ids.Add($"{family}!{(string?)app.Attribute("Id")}");
                    }
                }
            }
            catch (Exception ex) { Log.Write($"app id for {Path.GetFileName(exePath)}: {ex.Message}"); }
            return _appIds[exePath] = ids;
        }
    }

    /// <summary>Every app with a saved GPU choice (paths only, packaged apps use their app id instead of a path).</summary>
    public static List<(string Exe, string? GpuKey, string Raw)> All()
    {
        var list = new List<(string, string?, string)>();
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (k == null) return list;
        var names = k.GetValueNames();
        // app ids we mirror from a Store app's path value aren't separate choices
        var mirrored = names.Where(n => n.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                            .SelectMany(AppIdsFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (name == GlobalName || mirrored.Contains(name)) continue;
            var raw = k.GetValue(name) as string ?? "";
            if (!HasGpuChoiceRaw(raw)) continue;
            list.Add((name, Resolve(raw), raw));
        }
        return list;
    }

    static bool HasGpuChoiceRaw(string raw) =>
        Parse(raw).Any(p => p.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase) && p.Value != "0");
}
