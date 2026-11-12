using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// The parsers against real robocopy output, with every word robocopy prints replaced - as it would be on
/// a non-English PC. This machine's robocopy speaks English, so its labels say what each line means; the
/// parsers have to reach exactly the same answer once those labels are gone.
/// </summary>
public class RobocopyLanguageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-lang-" + Guid.NewGuid().ToString("N")[..8]);
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");

    public RobocopyLanguageTests()
    {
        // Every kind of line a mirror listing prints: new, newer, older, changed, a file/folder clash both
        // ways round, a lone extra file, and an extra folder holding more folders and files.
        Write(Src, "new.txt", "a");
        Write(Src, "newer.txt", "new"); Write(Dst, "newer.txt", "old");
        File.SetLastWriteTime(Path.Combine(Dst, "newer.txt"), DateTime.Now.AddDays(-1));
        Write(Src, "older.txt", "older"); Write(Dst, "older.txt", "newer at destination");
        File.SetLastWriteTime(Path.Combine(Src, "older.txt"), DateTime.Now.AddDays(-2));
        Write(Src, "changed.txt", "longer text"); Write(Dst, "changed.txt", "short");
        var t = DateTime.Now.AddHours(-3);
        File.SetLastWriteTime(Path.Combine(Src, "changed.txt"), t); File.SetLastWriteTime(Path.Combine(Dst, "changed.txt"), t);
        Write(Dst, "extra.txt", "x");
        Write(Dst, @"extradir\inner\deep.txt", "x");
        Write(Dst, @"extradir\top.txt", "x");
        Write(Src, @"clash\in.txt", "y"); Write(Dst, "clash", "a file where the source has a folder");
        Write(Dst, @"clash2\in.txt", "y"); Write(Src, "clash2", "a file where the destination has a folder");
        Write(Src, @"sub\same.txt", "s"); Write(Dst, @"sub\same.txt", "s");
        File.SetLastWriteTime(Path.Combine(Dst, @"sub\same.txt"), File.GetLastWriteTime(Path.Combine(Src, @"sub\same.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Deletions_found_without_reading_the_labels_are_exactly_robocopys_extras()
    {
        var lines = MirrorListing();

        // The truth, from the English labels.
        var extras = lines.Where(l => l.Contains("*EXTRA")).ToList();
        var expectedAll = extras.Select(l => l[l.IndexOf(Dst, StringComparison.OrdinalIgnoreCase)..].Trim().TrimEnd('\\')).ToHashSet();
        int expectedFiles = extras.Count(l => l.Contains("*EXTRA File"));
        Assert.True(expectedAll.Count >= 6, "the listing should hold the extras set up above:\n" + string.Join("\n", lines));
        // extra.txt, deep.txt, top.txt, the file "clash", and clash2\in.txt inside the clashing folder.
        Assert.Equal(5, expectedFiles);

        foreach (var listing in new[] { lines, Untranslatable(lines) })
        {
            var found = new HashSet<string>();
            int files = 0;
            foreach (var line in listing)
            {
                if (!RobocopyOutput.TryParseExtraPath(line, Dst, out var path, out bool isDir)) continue;
                found.Add(path);
                if (!isDir) files++;
            }

            Assert.Equal(expectedAll.OrderBy(p => p), found.OrderBy(p => p));
            Assert.Equal(expectedFiles, files);
        }
    }

    [Fact]
    public void Overwrite_candidates_found_without_reading_the_labels_include_every_replaced_file()
    {
        // Same flags as the two-way pass. The engine then keeps only those the destination already has.
        var lines = Listing("/E /L /XO");
        foreach (var listing in new[] { lines, Untranslatable(lines) })
        {
            var candidates = new List<string>();
            foreach (var line in listing)
                if (RobocopyOutput.TryParseOverwrittenSourcePath(line, Src, out var p)) candidates.Add(p);

            var replaced = candidates.Where(p => File.Exists(Path.Combine(Dst, Path.GetRelativePath(Src, p))))
                                     .Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "changed.txt", "newer.txt" }, replaced);
        }
    }

    [Fact]
    public void The_totals_are_read_without_reading_the_labels()
    {
        var lines = Listing("/E /L", withSummary: true);
        foreach (var listing in new[] { lines, Untranslatable(lines) })
        {
            var totals = new RobocopyTotals();
            foreach (var line in listing) totals.Feed(line);

            Assert.True(totals.FoundFiles, string.Join("\n", listing));
            // Whatever robocopy counted - its own English "Files :" row is the reference.
            var english = lines.Single(l => Regex.IsMatch(l, @"^\s*Files\s*:\s*\d"));
            var expected = english[(english.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(int.Parse(expected[1]), totals.FilesCopied);
            Assert.Equal(int.Parse(expected[4]), totals.FilesFailed);
            Assert.True(totals.FilesCopied > 0);
        }
    }

    // ---------- helpers ----------

    private List<string> MirrorListing() => Listing("/MIR /L");

    private List<string> Listing(string mode, bool withSummary = false)
    {
        string log = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".log");
        string args = $"{SyncEngine.Quote(Src)} {SyncEngine.Quote(Dst)} {mode} /R:0 /W:0 /FFT /XJ /NP /NDL /NJH"
                    + (withSummary ? "" : " /NJS") + $" /UNILOG:{SyncEngine.Quote(log)}";
        using var p = Process.Start(new ProcessStartInfo("robocopy", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode < 16, $"robocopy exit {p.ExitCode}");
        return File.ReadAllLines(log, Encoding.Unicode).ToList();
    }

    /// <summary>
    /// Replaces every word robocopy itself prints - labels, table headings, error text - leaving only the
    /// paths, numbers and punctuation that are the same in every language.
    /// </summary>
    private List<string> Untranslatable(List<string> lines) => lines.Select(line =>
    {
        int pathAt = Math.Max(line.IndexOf(Src, StringComparison.OrdinalIgnoreCase), line.IndexOf(Dst, StringComparison.OrdinalIgnoreCase));
        string words = pathAt >= 0 ? line[..pathAt] : line;
        string rest = pathAt >= 0 ? line[pathAt..] : "";
        return Regex.Replace(words, @"\*?[A-Za-z][A-Za-z.]*", "Ωxyz") + rest;
    }).ToList();

    private static void Write(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
