using System.Diagnostics;
using System.Security;

namespace GpuSwitcher.Core;

/// <summary>Scheduled task "GPU Switcher": runs at logon with highest privileges, so no UAC prompt.</summary>
public static class Autostart
{
    const string TaskName = "GPU Switcher";

    public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>Re-creates the task if it points at another exe or was made by an older version (with a start delay).</summary>
    public static void RefreshIfOutdated()
    {
        // only the installed copy may (re)point the task, never a dev build somewhere else
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!Environment.ProcessPath!.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || !IsEnabled()) return;
        var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var xml = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        if (xml.Contains("<Delay>") || !xml.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase)) Enable();
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath!;
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts GPU Switcher in the tray as admin (no UAC prompt).</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--tray</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "gpuswitcher-task.xml");
        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
        try
        {
            int code = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            if (code != 0) throw new InvalidOperationException($"schtasks failed ({code}).");
        }
        finally { File.Delete(tmp); }
        Log.Write("autostart task created for " + exe);
    }

    public static void Disable()
    {
        Schtasks($"/Delete /TN \"{TaskName}\" /F");
        Log.Write("autostart task removed");
    }

    static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }
}
