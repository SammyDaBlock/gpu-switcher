using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GpuSwitcher.Core;

public record Specs(string Cpu, string Ram, string Board, string Os);

/// <summary>The PC's spec sheet for the overlay. Read once (WMI is slow), GPUs and the display come live from elsewhere.</summary>
public static class SpecSheet
{
    static Specs? _specs;
    static Task<Specs>? _loading;

    /// <summary>Null until it's loaded in the background.</summary>
    public static Specs? Current
    {
        get
        {
            if (_specs != null) return _specs;
            _loading ??= Task.Run(Load);
            if (_loading.IsCompletedSuccessfully) _specs = _loading.Result;
            return _specs;
        }
    }

    static Specs Load() => new(Cpu(), Ram(), Board(), Os());

    static string Cpu()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            var cpus = s.Get().Cast<ManagementObject>().ToList();
            var name = Regex.Replace((cpus[0]["Name"] as string ?? "CPU").Replace("(R)", "").Replace("(TM)", ""), @"\s+(\d+-Core|\w+-Core) Processor|\s+Processor|\s+CPU @.*", "");
            int cores = cpus.Sum(c => Convert.ToInt32(c["NumberOfCores"])), threads = cpus.Sum(c => Convert.ToInt32(c["NumberOfLogicalProcessors"]));
            return $"{Regex.Replace(name, @"\s{2,}", " ").Trim()}  {cores}C/{threads}T";
        }
        catch { return Environment.ProcessorCount + " threads"; }
    }

    static string Ram()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            var sticks = s.Get().Cast<ManagementObject>().ToList();
            ulong total = sticks.Aggregate(0UL, (a, m) => a + Convert.ToUInt64(m["Capacity"]));
            uint speed = sticks.Select(m => Convert.ToUInt32(m["ConfiguredClockSpeed"] ?? m["Speed"] ?? 0)).DefaultIfEmpty(0u).Max();
            var type = sticks.Select(m => Convert.ToUInt32(m["SMBIOSMemoryType"] ?? 0)).FirstOrDefault() switch
            {
                34 => "DDR5", 26 => "DDR4", 24 => "DDR3", _ => "",
            };
            return $"{Math.Round(total / (double)(1UL << 30))} GB {type} {(speed > 0 ? speed + " MT/s" : "")}".Replace("  ", " ").Trim()
                   + $"  ({sticks.Count} stick{(sticks.Count == 1 ? "" : "s")})";
        }
        catch { return "?"; }
    }

    static string Board()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            var b = s.Get().Cast<ManagementObject>().First();
            var maker = (b["Manufacturer"] as string ?? "").Replace("ASUSTeK COMPUTER INC.", "ASUS").Replace("Micro-Star International Co., Ltd.", "MSI")
                .Replace("Gigabyte Technology Co., Ltd.", "Gigabyte");
            return $"{maker} {b["Product"]}".Trim();
        }
        catch { return "?"; }
    }

    static string Os()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            // Windows 11 still says "Windows 10" here
            if (Environment.OSVersion.Version.Build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {k?.GetValue("DisplayVersion")}".Trim();
        }
        catch { return "Windows"; }
    }
}
