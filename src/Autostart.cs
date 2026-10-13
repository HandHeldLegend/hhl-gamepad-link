// "Start with Windows". HHL Gamepad Link runs as administrator, and Windows skips elevated apps in
// the usual startup list, so it is a Task Scheduler task instead: at this user's sign-in, with
// highest privileges, started hidden in the tray. The task is the only thing the app leaves behind,
// and only while the option is on. It points at this .exe where it is now.

using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace HhlGamepadLink;

static class Autostart
{
    const string TaskName = "HHL Gamepad Link";
    const string OldTaskName = "HHL Gamepad WLAN"; // The app's name up to 0.1
    const int SchtasksWaitMs = 10_000;

    // A Start with Windows task from before the rename becomes this one
    public static void AdoptOldTask()
    {
        if (Schtasks($"/Query /TN \"{OldTaskName}\"") != 0)
            return;
        Schtasks($"/Delete /TN \"{OldTaskName}\" /F");
        Set(true);
    }

    public static bool IsOn() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Set(bool on)
    {
        if (!on)
            return !IsOn() || Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No path to this app");
        string user = WindowsIdentity.GetCurrent().Name;

        // Unlike schtasks' defaults, it also starts on battery and is never stopped for running long
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>HHL Gamepad Link: HOJA gamepads over Bluetooth</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
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

        string file = Path.Combine(Path.GetTempPath(), $"hhl-gamepad-link-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0;
        }
        finally
        {
            File.Delete(file);
        }
    }

    static int Schtasks(string args)
    {
        var start = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(start)!;
        p.WaitForExit(SchtasksWaitMs);
        return p.HasExited ? p.ExitCode : -1;
    }
}
