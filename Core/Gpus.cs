using System.Management;
using System.Text.RegularExpressions;

namespace GpuSwitcher.Core;

public class GpuAdapter
{
    public required string Name { get; init; }
    public required uint VendorId { get; init; }
    public required uint DeviceId { get; init; }
    public required uint SubSysId { get; init; }
    public string? PnpId { get; set; }
    public ulong VramBytes { get; set; }
    /// <summary>All LUIDs that belong to this card (virtual displays rendering on it get their own LUIDs).</summary>
    public HashSet<long> Luids { get; } = new();
    /// <summary>Has monitors plugged in. We never disable a display GPU.</summary>
    public bool IsDisplayGpu { get; set; }
    public bool IsDisabled { get; set; }

    /// <summary>Same format Windows uses in UserGpuPreferences: VEN&amp;DEV&amp;SUBSYS.</summary>
    public string Key => $"{VendorId:X4}&{DeviceId:X4}&{SubSysId:X8}";

    public string ShortName
    {
        get
        {
            var n = Regex.Replace(Name, @"^(NVIDIA\s+)?(GeForce\s+)?", "", RegexOptions.IgnoreCase);
            return n.Replace("AMD ", "").Replace("(TM)", "").Trim();
        }
    }

    public override string ToString() => ShortName;
}

/// <summary>Keeps the list of physical GPUs. Cards stay in the list while we have them temporarily disabled.</summary>
public static class Gpus
{
    static readonly object Gate = new();
    static List<GpuAdapter> _all = new();

    public static IReadOnlyList<GpuAdapter> All { get { lock (Gate) return _all.ToList(); } }

    public static GpuAdapter? ByKey(string? key) =>
        key == null ? null : All.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));

    public static GpuAdapter? ByLuid(long luid) => All.FirstOrDefault(g => g.Luids.Contains(luid));

    public static GpuAdapter? DisplayGpu => All.FirstOrDefault(g => g.IsDisplayGpu);

    public static void Refresh()
    {
        var dxgi = Native.EnumDxgiAdapters().Where(a => !a.Software && a.VendorId != 0x1414).ToList();
        var pnp = QueryPnpVideoDevices();

        lock (Gate)
        {
            var fresh = new List<GpuAdapter>();
            foreach (var grp in dxgi.GroupBy(a => (a.VendorId, a.DeviceId, a.SubSysId)))
            {
                var first = grp.First();
                var key = $"{first.VendorId:X4}&{first.DeviceId:X4}&{first.SubSysId:X8}";
                var g = _all.FirstOrDefault(x => x.Key == key) ?? new GpuAdapter
                {
                    Name = first.Name, VendorId = first.VendorId, DeviceId = first.DeviceId, SubSysId = first.SubSysId
                };
                g.Luids.Clear();
                foreach (var a in grp) g.Luids.Add(a.Luid);
                g.VramBytes = grp.Max(a => a.VramBytes);
                g.PnpId = pnp.FirstOrDefault(p => p.Matches(g.VendorId, g.DeviceId, g.SubSysId))?.PnpId ?? g.PnpId;
                // DXGI reports every monitor on the GPU this process renders on (so on the 3060 when GpuSwitcher.exe is set to it).
                // Ask PnP which card the monitors are actually plugged into; DXGI is only the fallback.
                g.IsDisplayGpu = g.PnpId != null ? Native.HasMonitors(g.PnpId) : grp.Any(a => a.Outputs > 0);
                g.IsDisabled = false;
                fresh.Add(g);
            }

            // Keep cards that vanished from DXGI because they're disabled right now
            foreach (var old in _all.Where(o => fresh.All(f => f.Key != o.Key)))
            {
                if (old.PnpId != null && Native.IsDeviceDisabled(old.PnpId))
                {
                    old.IsDisabled = true;
                    old.Luids.Clear();
                    fresh.Add(old);
                }
            }

            // If no DXGI adapter reports outputs (rare), treat the first one as the display GPU so we never disable it.
            if (fresh.Count > 0 && !fresh.Any(f => f.IsDisplayGpu)) fresh[0].IsDisplayGpu = true;

            _all = fresh.OrderByDescending(f => f.IsDisplayGpu).ThenBy(f => f.Name).ToList();
        }
    }

    record PnpVideo(string PnpId, uint Ven, uint Dev, uint Subsys)
    {
        public bool Matches(uint v, uint d, uint s) => Ven == v && Dev == d && Subsys == s;
    }

    static List<PnpVideo> QueryPnpVideoDevices()
    {
        var list = new List<PnpVideo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass='Display'");
            foreach (ManagementObject mo in searcher.Get())
            {
                var id = mo["PNPDeviceID"] as string;
                if (id == null) continue;
                var m = Regex.Match(id, @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})&SUBSYS_([0-9A-F]{8})", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                list.Add(new PnpVideo(id, Convert.ToUInt32(m.Groups[1].Value, 16), Convert.ToUInt32(m.Groups[2].Value, 16),
                    Convert.ToUInt32(m.Groups[3].Value, 16)));
            }
        }
        catch { /* WMI hiccup: keep previous PnP ids */ }
        return list;
    }
}
