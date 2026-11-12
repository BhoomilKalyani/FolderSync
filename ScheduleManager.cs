using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderSync;

/// <summary>
/// Manages the automatic-sync triggers as Windows scheduled tasks.
///
/// Tasks are registered by importing XML (schtasks /XML) rather than using the /SC switches,
/// because that is the only way to get the triggers and settings this app needs:
///   • a Session-Unlock trigger (so a laptop that is only ever locked/unlocked still syncs) —
///     /SC ONLOGON cannot express this and is refused without admin rights anyway;
///   • DisallowStartIfOnBatteries = false — the CLI default silently skips runs on battery;
///   • StartWhenAvailable = true — a missed run (machine asleep) happens once it's awake.
/// Everything runs headless as the current user, so no elevation is required.
/// </summary>
public static class ScheduleManager
{
    public const string DailyTask = "FolderSync - Daily";
    public const string MonthlyTask = "FolderSync - Monthly";
    public const string LogonTask = "FolderSync - At Logon or Unlock";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FolderSync.exe");

    private static string CurrentUser => $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>
    /// True if the app is running out of a build output folder (bin\Debug / bin\Release).
    /// Registering a trigger from there bakes in a throwaway path that breaks on the next clean.
    /// </summary>
    public static bool IsBuildOutputPath(string? exe = null)
    {
        string p = exe ?? ExePath;
        return p.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>
    /// The encoding schtasks writes when its output is redirected: the OEM code page (437 or 850 on
    /// most Western systems). Left unset, .NET decodes it as the ANSI code page in this console-less
    /// app, which turned "José" in an install path into "Jos‚" - so every schedule read as pointing at
    /// a missing program.
    /// </summary>
    private static readonly Lazy<Encoding> OemEncoding = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch { return Encoding.Default; }
    });

    private static (int code, string output) Run(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = OemEncoding.Value,
            StandardErrorEncoding = OemEncoding.Value,
        };
        using var p = Process.Start(psi)!;

        // Both pipes are drained at the same time. Reading one to completion and only then the other
        // deadlocks if the child fills the pipe it is not being read from - the classic hang. schtasks
        // output is small enough that it has never happened, but /Query /XML on a large task is not
        // obviously small, and this runs on the startup path.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Task.WaitAll(stdout, stderr);
        p.WaitForExit();

        return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    private static string Xml(string description, string triggersXml, string arguments)
    {
        string user = SecurityElement(CurrentUser);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{SecurityElement(description)}</Description>
  </RegistrationInfo>
  <Triggers>
{triggersXml}
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{SecurityElement(ExePath)}</Command>
      <Arguments>{SecurityElement(arguments)}</Arguments>
      <WorkingDirectory>{SecurityElement(Path.GetDirectoryName(ExePath) ?? "")}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string SecurityElement(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static (bool ok, string msg) Register(string taskName, string description, string triggersXml, string arguments)
    {
        string? tmp = null;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(), $"foldersync_task_{Guid.NewGuid():N}.xml");
            // schtasks expects UTF-16 for imported task XML.
            File.WriteAllText(tmp, Xml(description, triggersXml, arguments), Encoding.Unicode);
            var (code, output) = Run($"/Create /F /TN \"{taskName}\" /XML \"{tmp}\"");
            return (code == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (tmp is not null) File.Delete(tmp); } catch { }
        }
    }

    public static (bool ok, string msg) RegisterDaily(TimeOnly time)
    {
        string trigger = $"""
    <CalendarTrigger>
      <StartBoundary>{DateTime.Today:yyyy-MM-dd}T{time:HH\:mm}:00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
    </CalendarTrigger>
""";
        return Register(DailyTask, "Folder Sync - daily sync", trigger, "--sync");
    }

    public static (bool ok, string msg) RegisterMonthly(TimeOnly time, int day = 1)
    {
        string months = string.Concat(new[]
        {
            "January","February","March","April","May","June",
            "July","August","September","October","November","December"
        }.Select(m => $"<{m}/>"));

        string trigger = $"""
    <CalendarTrigger>
      <StartBoundary>{DateTime.Today:yyyy-MM-dd}T{time:HH\:mm}:00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByMonth>
        <DaysOfMonth><Day>{day}</Day></DaysOfMonth>
        <Months>{months}</Months>
      </ScheduleByMonth>
    </CalendarTrigger>
""";
        return Register(MonthlyTask, "Folder Sync - monthly sync", trigger, "--sync");
    }

    /// <summary>
    /// Registers one task that fires both at logon and whenever the workstation is unlocked.
    /// Uses "--sync --auto" so the app applies its minimum-gap throttle (an unlock happens often).
    /// </summary>
    public static (bool ok, string msg) RegisterLogon()
    {
        string user = SecurityElement(CurrentUser);
        string trigger = $"""
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
    <SessionStateChangeTrigger>
      <Enabled>true</Enabled>
      <StateChange>SessionUnlock</StateChange>
      <UserId>{user}</UserId>
    </SessionStateChangeTrigger>
""";
        var result = Register(LogonTask, "Folder Sync - at logon and on unlock", trigger, "--sync --auto");
        if (result.ok) RemoveLegacyStartupShortcut();
        return result;
    }

    public static (bool ok, string msg) Remove(string taskName)
    {
        if (taskName == LogonTask) RemoveLegacyStartupShortcut();
        var (code, output) = Run($"/Delete /F /TN \"{taskName}\"");
        return (code == 0, output);
    }

    /// <summary>Pauses (disable) or resumes (enable) an already-registered task without deleting it.</summary>
    public static (bool ok, string msg) SetEnabled(string taskName, bool enabled)
    {
        if (GetState(taskName) == TaskState.NotInstalled)
            return (false, $"Task \"{taskName}\" is not installed yet — enable it first.");
        var (code, output) = Run($"/Change /TN \"{taskName}\" {(enabled ? "/ENABLE" : "/DISABLE")}");
        return (code == 0, output);
    }

    public enum TaskState { NotInstalled, Enabled, Disabled }

    public static TaskState GetState(string taskName)
    {
        var (code, output) = Run($"/Query /TN \"{taskName}\" /FO LIST /V");
        if (code != 0) return TaskState.NotInstalled;
        // Only the "Scheduled Task State:" line tells enabled vs disabled — other fields
        // (e.g. "Idle Time: Disabled") also contain the word, so don't scan the whole output.
        foreach (var line in output.Split('\n'))
        {
            if (line.TrimStart().StartsWith("Scheduled Task State:", StringComparison.OrdinalIgnoreCase))
                return line.Contains("Disabled", StringComparison.OrdinalIgnoreCase) ? TaskState.Disabled : TaskState.Enabled;
        }
        return TaskState.Enabled; // installed but state line not found
    }

    public static bool Exists(string taskName) => GetState(taskName) != TaskState.NotInstalled;

    /// <summary>Just the exe path a task launches, with the arguments stripped off.</summary>
    public static string? GetTaskExe(string taskName)
    {
        var target = GetTaskTarget(taskName);
        if (string.IsNullOrWhiteSpace(target)) return null;
        return target.Replace("\"", "")
                     .Replace("--sync", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("--auto", "", StringComparison.OrdinalIgnoreCase)
                     .Trim();
    }

    /// <summary>All three task names, with friendly labels.</summary>
    public static IEnumerable<(string task, string label)> AllTasks() => new[]
    {
        (DailyTask, "Daily"),
        (MonthlyTask, "Monthly"),
        (LogonTask, "Logon / unlock"),
    };

    /// <summary>The exe a registered task will launch, or null if the task isn't installed.</summary>
    public static string? GetTaskTarget(string taskName)
    {
        var (code, output) = Run($"/Query /TN \"{taskName}\" /FO LIST /V");
        if (code != 0) return null;
        foreach (var line in output.Split('\n'))
        {
            if (line.TrimStart().StartsWith("Task To Run:", StringComparison.OrdinalIgnoreCase))
                return line.Split(':', 2)[1].Trim();
        }
        return null;
    }

    /// <summary>
    /// Reads a calendar task's start time and monthly day from its registered XML, so a repair
    /// can keep the user's original schedule instead of resetting it to a default.
    /// </summary>
    public static (TimeOnly? time, int? day) GetCalendarSchedule(string taskName)
    {
        var (code, xml) = Run($"/Query /TN \"{taskName}\" /XML");
        if (code != 0) return (null, null);

        TimeOnly? time = null;
        int? day = null;

        var start = Between(xml, "<StartBoundary>", "</StartBoundary>");
        if (start is not null && DateTime.TryParse(start, out var dt)) time = TimeOnly.FromDateTime(dt);

        var dayText = Between(xml, "<Day>", "</Day>");
        if (dayText is not null && int.TryParse(dayText.Trim(), out int d)) day = d;

        return (time, day);
    }

    private static string? Between(string text, string open, string close)
    {
        int a = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (a < 0) return null;
        a += open.Length;
        int b = text.IndexOf(close, a, StringComparison.OrdinalIgnoreCase);
        return b < 0 ? null : text[a..b];
    }

    /// <summary>
    /// Re-registers an installed task so it points at the exe running right now, preserving its
    /// existing time/day. Used after an update or move relocates the program.
    /// </summary>
    public static (bool ok, string msg) Repair(string taskName)
    {
        if (!Exists(taskName)) return (false, "not installed");

        if (taskName == LogonTask) return RegisterLogon();

        var (time, day) = GetCalendarSchedule(taskName);
        var at = time ?? new TimeOnly(18, 0);
        return taskName == MonthlyTask
            ? RegisterMonthly(at, day ?? 1)
            : RegisterDaily(at);
    }

    /// <summary>Deletes the old Startup-folder shortcut used by earlier versions.</summary>
    public static void RemoveLegacyStartupShortcut()
    {
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            foreach (var name in new[] { "FolderSync.lnk", "FolderSync.lnk.disabled" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { /* best effort */ }
    }
}
