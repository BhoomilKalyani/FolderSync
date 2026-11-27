using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

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
///
/// <para>Tasks are read back as XML too. The list view (<c>/FO LIST</c>) prints its labels - "Task To
/// Run:", "Scheduled Task State:" - in the language of Windows, so reading them found nothing on a
/// non-English PC; the XML is the same everywhere.</para>
/// </summary>
public static class ScheduleManager
{
    public const string DailyTask = "FolderSync - Daily";
    public const string MonthlyTask = "FolderSync - Monthly";
    public const string LogonTask = "FolderSync - At Logon or Unlock";

    /// <summary>The argument that tells a scheduled run which schedule started it, for the history.</summary>
    public const string TriggerSwitch = "--trigger";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FolderSync.exe");

    private static string CurrentUser => $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>
    /// Shown when a copy running on its own data folder is asked to change a schedule. Windows keeps one
    /// set of tasks per user, so changing them from a test copy would re-point the real ones.
    /// </summary>
    public const string SeparateCopyMessage =
        "Schedules belong to the installed copy of Folder Sync and cannot be changed from a copy that "
        + "uses its own data folder.";

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
            // OEM, not the ANSI default: otherwise "José" in an install path read back as "Jos‚", and
            // every schedule looked as though it pointed at a missing program.
            StandardOutputEncoding = ConsoleEncoding.Oem,
            StandardErrorEncoding = ConsoleEncoding.Oem,
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
        if (AppInstance.IsSeparate) return (false, SeparateCopyMessage);

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
        return Register(DailyTask, "Folder Sync - daily sync", trigger, ArgumentsFor(DailyTask));
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
        return Register(MonthlyTask, "Folder Sync - monthly sync", trigger, ArgumentsFor(MonthlyTask));
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
        var result = Register(LogonTask, "Folder Sync - at logon and on unlock", trigger, ArgumentsFor(LogonTask));
        if (result.ok) RemoveLegacyStartupShortcut();
        return result;
    }

    /// <summary>
    /// The command line a task runs the app with. Each says which schedule it is, so a run's history
    /// entry can say "Daily schedule" rather than only "a schedule".
    /// </summary>
    public static string ArgumentsFor(string taskName) =>
        taskName == LogonTask ? $"--sync --auto {TriggerSwitch} logon"
        : taskName == MonthlyTask ? $"--sync {TriggerSwitch} monthly"
        : $"--sync {TriggerSwitch} daily";

    /// <summary>
    /// Which schedule started a headless run, from its command line. A task registered before the
    /// switch existed has none: "--auto" still identifies the sign-in/unlock task, and anything else is
    /// simply "a schedule".
    /// </summary>
    public static RunTrigger TriggerFromArguments(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (!args[i].Equals(TriggerSwitch, StringComparison.OrdinalIgnoreCase)) continue;
            return args[i + 1].Trim().ToLowerInvariant() switch
            {
                "daily" => RunTrigger.Daily,
                "monthly" => RunTrigger.Monthly,
                "logon" => RunTrigger.Logon,
                _ => RunTrigger.Scheduled,
            };
        }

        return args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase))
            ? RunTrigger.Logon
            : RunTrigger.Scheduled;
    }

    public static (bool ok, string msg) Remove(string taskName)
    {
        if (AppInstance.IsSeparate) return (false, SeparateCopyMessage);

        if (taskName == LogonTask) RemoveLegacyStartupShortcut();
        var (code, output) = Run($"/Delete /F /TN \"{taskName}\"");
        return (code == 0, output);
    }

    /// <summary>Pauses (disable) or resumes (enable) an already-registered task without deleting it.</summary>
    public static (bool ok, string msg) SetEnabled(string taskName, bool enabled)
    {
        if (AppInstance.IsSeparate) return (false, SeparateCopyMessage);

        if (GetState(taskName) == TaskState.NotInstalled)
            return (false, $"Task \"{taskName}\" is not installed yet — enable it first.");
        var (code, output) = Run($"/Change /TN \"{taskName}\" {(enabled ? "/ENABLE" : "/DISABLE")}");
        return (code == 0, output);
    }

    public enum TaskState { NotInstalled, Enabled, Disabled }

    /// <summary>Everything the app needs to know about one registered task, from a single query.</summary>
    /// <param name="Exe">The program it starts, or null when the task is not installed.</param>
    /// <param name="Arguments">What it passes to that program.</param>
    /// <param name="Time">A calendar task's time of day.</param>
    /// <param name="Day">A monthly task's day of the month.</param>
    public sealed record TaskInfo(TaskState State, string? Exe, string Arguments, TimeOnly? Time, int? Day)
    {
        public static readonly TaskInfo Missing = new(TaskState.NotInstalled, null, "", null, null);

        public bool Installed => State != TaskState.NotInstalled;

        /// <summary>
        /// Registered by a version that did not yet tell its runs which schedule started them. The task
        /// works; re-registering it only makes the history more specific.
        /// </summary>
        public bool LacksTrigger => Installed && !Arguments.Contains(TriggerSwitch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a task with one schtasks call. <see cref="TaskInfo.Missing"/> when it is not installed.</summary>
    public static TaskInfo Query(string taskName)
    {
        var (code, xml) = Run($"/Query /TN \"{taskName}\" /XML");
        return code != 0 ? TaskInfo.Missing : ParseTaskXml(xml);
    }

    /// <summary>
    /// Reads a task's XML as schtasks prints it. Separate from <see cref="Query"/> so it can be tested
    /// against real output without a scheduled task.
    ///
    /// <para>Windows leaves out any setting that has its default value, so an enabled task has no
    /// <c>&lt;Enabled&gt;</c> element under <c>&lt;Settings&gt;</c> at all; a disabled one has
    /// <c>&lt;Enabled&gt;false&lt;/Enabled&gt;</c>. The triggers have <c>&lt;Enabled&gt;</c> elements of
    /// their own, which is why only the one directly under Settings is read.</para>
    /// </summary>
    public static TaskInfo ParseTaskXml(string xml)
    {
        try
        {
            int start = xml.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
            if (start < 0) start = xml.IndexOf("<Task", StringComparison.OrdinalIgnoreCase);
            if (start > 0) xml = xml[start..];

            var root = XDocument.Parse(xml).Root;
            if (root is null) return new TaskInfo(TaskState.Enabled, null, "", null, null);

            XElement? Child(XElement? parent, string name) =>
                parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            XElement? Anywhere(string name) =>
                root.Descendants().FirstOrDefault(e => e.Name.LocalName == name);

            bool disabled = string.Equals(Child(Child(root, "Settings"), "Enabled")?.Value.Trim(), "false",
                                          StringComparison.OrdinalIgnoreCase);

            var exec = Anywhere("Exec");
            string? exe = Child(exec, "Command")?.Value.Trim().Trim('"');
            string arguments = Child(exec, "Arguments")?.Value.Trim() ?? "";

            TimeOnly? time = null;
            if (Anywhere("StartBoundary")?.Value is { } boundary && DateTime.TryParse(boundary, out var dt))
                time = TimeOnly.FromDateTime(dt);

            int? day = null;
            if (Anywhere("Day")?.Value is { } dayText && int.TryParse(dayText.Trim(), out int d)) day = d;

            return new TaskInfo(disabled ? TaskState.Disabled : TaskState.Enabled,
                                string.IsNullOrWhiteSpace(exe) ? null : exe, arguments, time, day);
        }
        catch
        {
            // The task exists (the query succeeded) but its description could not be read. Saying
            // "enabled" with no program is the least misleading answer: nothing is reported as missing.
            return new TaskInfo(TaskState.Enabled, null, "", null, null);
        }
    }

    public static TaskState GetState(string taskName) => Query(taskName).State;

    public static bool Exists(string taskName) => GetState(taskName) != TaskState.NotInstalled;

    /// <summary>Just the exe path a task launches, with the arguments stripped off.</summary>
    public static string? GetTaskExe(string taskName) => Query(taskName).Exe;

    /// <summary>All three task names, with friendly labels.</summary>
    public static IEnumerable<(string task, string label)> AllTasks() => new[]
    {
        (DailyTask, "Daily"),
        (MonthlyTask, "Monthly"),
        (LogonTask, "Logon / unlock"),
    };

    /// <summary>
    /// Reads a calendar task's start time and monthly day from its registered XML, so a repair
    /// can keep the user's original schedule instead of resetting it to a default.
    /// </summary>
    public static (TimeOnly? time, int? day) GetCalendarSchedule(string taskName)
    {
        var info = Query(taskName);
        return (info.Time, info.Day);
    }

    /// <summary>
    /// When the calendar schedules will next start a sync, or null when neither is enabled. Worked out
    /// from the task's own time rather than read from schtasks' "Next Run Time", whose date format
    /// follows the Windows language.
    /// </summary>
    public static DateTime? NextRun(TaskInfo daily, TaskInfo monthly, DateTime now)
    {
        DateTime? next = null;

        if (daily.State == TaskState.Enabled && daily.Time is { } dailyAt)
        {
            DateTime candidate = now.Date + dailyAt.ToTimeSpan();
            if (candidate <= now) candidate = candidate.AddDays(1);
            next = candidate;
        }

        if (monthly.State == TaskState.Enabled && monthly.Time is { } monthlyAt)
        {
            int day = Math.Clamp(monthly.Day ?? 1, 1, 28);
            DateTime candidate = new DateTime(now.Year, now.Month, day) + monthlyAt.ToTimeSpan();
            if (candidate <= now) candidate = candidate.AddMonths(1);
            if (next is null || candidate < next) next = candidate;
        }

        return next;
    }

    /// <summary>
    /// Re-registers an installed task so it points at the exe running right now, preserving its
    /// existing time/day. Used after an update or move relocates the program.
    ///
    /// A task that was paused stays paused: registering creates it enabled, which used to switch a
    /// schedule back on that its owner had deliberately turned off.
    /// </summary>
    public static (bool ok, string msg) Repair(string taskName)
    {
        var info = Query(taskName);
        if (!info.Installed) return (false, "not installed");

        var at = info.Time ?? new TimeOnly(18, 0);
        var result = taskName == LogonTask ? RegisterLogon()
                   : taskName == MonthlyTask ? RegisterMonthly(at, info.Day ?? 1)
                   : RegisterDaily(at);

        if (result.ok && info.State == TaskState.Disabled)
            SetEnabled(taskName, false);
        return result;
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
