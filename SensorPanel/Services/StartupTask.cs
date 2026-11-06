using System.Diagnostics;
using System.Security;

namespace SensorPanel.Services;

/// <summary>
/// "Start with Windows" as a scheduled task that runs at sign-in with administrator rights (needed for the sensors), so
/// there's no UAC prompt at every start. Creating or removing it needs administrator rights itself.
/// The task also starts SensorPanel again after a crash (Windows logs an "Application Error" event for it). A crash can
/// happen inside the graphics driver while it's being updated or reset, which .NET can't catch.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "SensorPanel";

    public static bool IsEnabled => Schtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>
    /// Recreates a task made by an older version (without the restart after a crash), if it starts this copy. A task that
    /// starts another copy is left alone.
    /// </summary>
    public static void Update()
    {
        if (Schtasks($"/Query /TN \"{TaskName}\" /XML", out string xml) != 0 || xml.Contains("<EventTrigger>")) return;
        string? command = System.Xml.Linq.XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
        if (string.Equals(command?.Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) Enable();
    }

    public static void Enable()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown program path.");
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string crashed = $"""
            <QueryList><Query Id="0" Path="Application"><Select Path="Application">*[System[Provider[@Name='Application Error'] and EventID=1000]] and *[EventData[Data[@Name='AppName']='{Path.GetFileName(exe)}']]</Select></Query></QueryList>
            """;
        // Written as XML so the task never times out (tasks made by "schtasks /Create" stop after 72 hours) and runs on battery.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts SensorPanel for the USB sensor display, and again if it crashes.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
                <EventTrigger><Enabled>true</Enabled><Subscription>{SecurityElement.Escape(crashed)}</Subscription><Delay>PT15S</Delay></EventTrigger>
              </Triggers>
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
            if (Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out _) != 0)
                throw new InvalidOperationException("Windows refused to create the startup task.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Disable() => Schtasks($"/Delete /TN \"{TaskName}\" /F", out _);

    private static int Schtasks(string arguments, out string output)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        output = process.StandardOutput.ReadToEnd();
        error.Wait();
        process.WaitForExit(15000);
        return process.ExitCode;
    }
}
