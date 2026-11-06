using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace DualSenseConnect.Core;

/// <summary>
/// Starts the app at logon through a Task Scheduler task. More reliable than the Run key, which
/// Windows sometimes delays or skips. A logon trigger limited to the current user needs no admin rights.
/// </summary>
static class Autostart
{
    const string TaskName = "DualSenseConnect";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void Enable()
    {
        string user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        string exe = SecurityElement.Escape(Environment.ProcessPath!);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>DualSenseConnect: DualSense tray app</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "autostart-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            int code = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            Log.Write(code == 0 ? "Autostart task created" : $"Creating autostart task failed ({code})");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Disable()
    {
        int code = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        Log.Write(code == 0 ? "Autostart task removed" : $"Removing autostart task failed ({code})");
    }

    /// <summary>Earlier versions used the Run key: replace it with the task (or the task's new exe path).</summary>
    public static void MigrateFromRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(TaskName) == null)
            return;
        Enable();
        if (IsEnabled())
            key.DeleteValue(TaskName);
    }

    static int Schtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
