using System.Diagnostics;
using System.Security;

namespace SensorPanel.Services;

/// <summary>
/// "Start with Windows" as a scheduled task that runs at sign-in with administrator rights (needed for the sensors), so
/// there's no UAC prompt at every start. Creating or removing it needs administrator rights itself.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "SensorPanel";

    public static bool IsEnabled => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void Enable()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown program path.");
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        // Written as XML so the task never times out (tasks made by "schtasks /Create" stop after 72 hours) and runs on battery.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts SensorPanel for the USB sensor display.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--minimized</Arguments></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "SensorPanel-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            if (Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") != 0)
                throw new InvalidOperationException("Windows refused to create the startup task.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Disable() => Schtasks($"/Delete /TN \"{TaskName}\" /F");

    private static int Schtasks(string arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return process.ExitCode;
    }
}
