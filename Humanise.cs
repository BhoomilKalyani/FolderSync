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
    public static string Ago(DateTime when)
    {
        var age = DateTime.Now - when;
        // A clock change (or a config copied from another machine) can put a timestamp in the future.
        return age <= TimeSpan.Zero ? "just now" : Age(age) + " ago";
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
}
