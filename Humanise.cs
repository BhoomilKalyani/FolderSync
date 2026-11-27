namespace FolderSync;

/// <summary>
/// The two bits of "make this readable for a person" formatting the app needs.
///
/// Gathered here because both had been written three times over: the relative-time helpers in the
/// health check, the window and the pair history had drifted into disagreeing with each other, so the
/// same sync could be described as "1.5 hour(s) ago" on one screen and "2 hour(s) ago" on another.
/// </summary>
public static class Humanise
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>A byte count as "1.5 KB", "237.5 GB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) return "";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {Units[unit]}";
    }

    /// <summary>How long ago something happened: "just now", "45 minutes ago", "3 days ago".</summary>
    public static string Ago(DateTime when) => Ago(when, DateTime.Now);

    /// <summary>The same, measured from a given moment rather than the clock - so it can be tested.</summary>
    public static string Ago(DateTime when, DateTime now)
    {
        var age = now - when;
        // Under a minute, Age() already says "just now", which takes no suffix ("just now ago").
        // A clock change (or a config copied from another machine) can put a timestamp in the future.
        return age < TimeSpan.FromMinutes(1) ? "just now" : Age(age) + " ago";
    }

    /// <summary>
    /// A duration as "just now", "45 minutes", "2 hours", "3 days". Whole units only: a backup that
    /// ran "1.5 days" ago reads oddly, and the precision was never meaningful.
    /// </summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return Plural((int)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Plural((int)age.TotalHours, "hour");
        return Plural((int)age.TotalDays, "day");
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// A count with its noun: "1 pair", "3 pairs", "1,204 files". Replaces the "3 pair(s)" the window
    /// used to show. <paramref name="plural"/> is only needed when adding an "s" is not enough.
    /// </summary>
    public static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count:N0} {plural ?? singular + "s"}";

    /// <summary>How long something took: "under 1 s", "42 s", "3 min 05 s", "1 h 12 min". Short enough for a table column.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(1)) return "under 1 s";
        if (span < TimeSpan.FromMinutes(1)) return $"{(int)span.TotalSeconds} s";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
        return $"{(int)span.TotalHours} h {span.Minutes:00} min";
    }
}
