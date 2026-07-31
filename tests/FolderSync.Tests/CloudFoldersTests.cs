using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Detecting cloud storage that a provider's desktop app has already mounted as a folder.
///
/// The filesystem is supplied by each test, so these describe a machine's layout rather than
/// depending on what happens to be installed wherever the tests run.
/// </summary>
public class CloudFoldersTests
{
    /// <summary>Detection against a pretend machine: only the listed paths, drives and sync roots exist.</summary>
    private static List<CloudFolder> Detect(
        IEnumerable<string> existingPaths,
        IEnumerable<string>? drives = null,
        Func<string, string?>? readFile = null,
        IEnumerable<(string, string)>? syncRoots = null)
    {
        var set = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        return CloudFolders.Detect(
            exists: p => set.Contains(p.TrimEnd('\\', '/')),
            driveRoots: () => drives ?? new[] { @"C:\" },
            readFile: readFile ?? (_ => null),
            syncRoots: () => syncRoots ?? Enumerable.Empty<(string, string)>());
    }

    // ---------- what Windows itself reports ----------

    [Fact]
    public void A_service_Folder_Sync_has_never_heard_of_is_found_because_Windows_registers_it()
    {
        // This is what makes the list open-ended: Box is nowhere in the code, but Windows records
        // any app that syncs a cloud account into a folder, so it turns up anyway.
        var found = Detect(
            new[] { @"C:\Users\Test\Box" },
            syncRoots: new[] { ("Box", @"C:\Users\Test\Box") });

        var box = Assert.Single(found);
        Assert.Equal("Box", box.Provider);
        Assert.Equal(@"C:\Users\Test\Box", box.Path);
    }

    [Fact]
    public void The_name_Windows_reports_is_preferred_over_the_built_in_guess()
    {
        // Registered entries are read first, so "OneDrive - Personal" wins over the fallback's
        // plain "OneDrive" for the same folder - and the folder is not listed twice.
        string oneDrive = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");

        var found = Detect(
            new[] { oneDrive },
            syncRoots: new[] { ("OneDrive - Personal", oneDrive) });

        var entry = Assert.Single(found);
        Assert.Equal("OneDrive - Personal", entry.Provider);
    }

    [Fact]
    public void A_registered_folder_that_no_longer_exists_is_ignored()
    {
        // Uninstalling a client can leave the registration behind.
        Assert.Empty(Detect(Array.Empty<string>(), syncRoots: new[] { ("Dropbox", @"C:\Gone\Dropbox") }));
    }

    [Fact]
    public void Several_accounts_of_one_service_are_all_listed()
    {
        var found = Detect(
            new[] { @"C:\Users\Test\OneDrive", @"C:\Work\OneDrive" },
            syncRoots: new[]
            {
                ("OneDrive - Personal", @"C:\Users\Test\OneDrive"),
                ("OneDrive - Contoso", @"C:\Work\OneDrive"),
            });

        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Provider == "OneDrive - Contoso");
    }

    [Fact]
    public void Google_Drive_is_found_by_its_My_Drive_folder_on_whichever_letter_it_uses()
    {
        // The drive letter is configurable, so it must not be assumed to be G:.
        var found = Detect(new[] { @"K:\My Drive" }, drives: new[] { @"C:\", @"K:\" });

        var drive = Assert.Single(found);
        Assert.Equal("Google Drive", drive.Provider);
        Assert.Equal(@"K:\My Drive", drive.Path);
    }

    [Fact]
    public void A_drive_without_My_Drive_is_not_mistaken_for_Google_Drive()
    {
        Assert.Empty(Detect(new[] { @"D:\Backup" }, drives: new[] { @"C:\", @"D:\" }));
    }

    [Fact]
    public void Dropbox_is_found_at_the_location_recorded_in_its_own_info_file()
    {
        // Dropbox can live anywhere, so the profile folder is not enough to go on.
        const string info = """
        {"personal":{"path":"E:\\Cloud\\Dropbox","host":1},"business":{"path":"E:\\Cloud\\Dropbox (Work)"}}
        """;

        var found = Detect(
            new[] { @"E:\Cloud\Dropbox", @"E:\Cloud\Dropbox (Work)" },
            readFile: path => path.EndsWith("info.json", StringComparison.OrdinalIgnoreCase) ? info : null);

        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal("Dropbox", f.Provider));
        Assert.Contains(found, f => f.Path == @"E:\Cloud\Dropbox");
        Assert.Contains(found, f => f.Path == @"E:\Cloud\Dropbox (Work)");
    }

    [Fact]
    public void A_recorded_Dropbox_folder_that_no_longer_exists_is_ignored()
    {
        const string info = """{"personal":{"path":"E:\\Gone"}}""";

        Assert.Empty(Detect(Array.Empty<string>(), readFile: _ => info));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"personal":{"no_path":1}}""")]
    [InlineData("""{"personal":"a string, not an object"}""")]
    public void A_broken_Dropbox_info_file_yields_nothing_rather_than_throwing(string json)
    {
        Assert.Empty(CloudFolders.ParseDropboxInfo(json));
    }

    [Fact]
    public void The_same_folder_reported_twice_is_only_listed_once()
    {
        // OneDrive is probed by environment variable and by profile folder, and Dropbox by two
        // config locations; a duplicated row would look like two separate accounts.
        const string info = """{"a":{"path":"C:\\Users\\Test\\Dropbox"},"b":{"path":"C:\\Users\\Test\\Dropbox"}}""";

        var found = Detect(new[] { @"C:\Users\Test\Dropbox" }, readFile: _ => info);

        Assert.Single(found);
    }

    [Fact]
    public void A_trailing_slash_does_not_create_a_second_entry()
    {
        const string info = """{"a":{"path":"C:\\Users\\Test\\Dropbox\\"},"b":{"path":"C:\\Users\\Test\\Dropbox"}}""";

        var found = Detect(new[] { @"C:\Users\Test\Dropbox" }, readFile: _ => info);

        Assert.Single(found);
        Assert.Equal(@"C:\Users\Test\Dropbox", found[0].Path);
    }

    [Fact]
    public void A_machine_with_no_cloud_apps_reports_nothing()
    {
        Assert.Empty(Detect(Array.Empty<string>()));
    }

    // ---------- what each row offers ----------

    [Fact]
    public void A_service_already_set_up_shows_no_button()
    {
        // The row is informational once a pair covers it; a second pair to the same service goes
        // through "Add pair" like any other.
        var inUse = new CloudFolderRow { Provider = "Google Drive", Path = @"G:\My Drive", InUse = true };
        Assert.Equal("Already set up", inUse.StatusText);
        Assert.Equal(System.Windows.Visibility.Collapsed, inUse.ButtonVisibility);

        var spare = new CloudFolderRow { Provider = "Dropbox", Path = @"C:\Users\Me\Dropbox", InUse = false };
        Assert.Equal("Not set up yet", spare.StatusText);
        Assert.Equal(System.Windows.Visibility.Visible, spare.ButtonVisibility);
    }

    // ---------- retiring the old API-based pairs ----------

    [Theory]
    [InlineData("gdrive:Documents", true)]
    [InlineData("gdrive:", true)]
    [InlineData("my-drive:x", true)]
    [InlineData(@"C:\Docs", false)]          // a drive letter must never match
    [InlineData(@"D:", false)]
    [InlineData(@"G:\My Drive\Docs", false)] // mounted cloud storage is an ordinary path
    [InlineData(@"\\server\share", false)]
    [InlineData("", false)]
    public void An_old_cloud_account_path_is_told_apart_from_a_real_folder(string path, bool isAccount)
    {
        Assert.Equal(isAccount, SyncConfig.LooksLikeCloudAccountPath(path));
    }

    [Fact]
    public void Pairs_left_over_from_the_API_version_are_dropped_rather_than_failing_every_sync()
    {
        var config = new SyncConfig();
        config.Pairs.AddRange(new[]
        {
            new SyncPair { Name = "keep",  Left = @"C:\Docs", Right = @"G:\My Drive\Docs" },
            new SyncPair { Name = "stale", Left = @"D:\TESTING", Right = "gdrive:Documents" },
            new SyncPair { Name = "also stale", Left = "gdrive:A", Right = @"C:\B" },
        });

        Assert.True(config.DropUnusableCloudAccountPairs());

        // The mounted-drive pair survives; the two account-syntax ones cannot run any more.
        var kept = Assert.Single(config.Pairs);
        Assert.Equal("keep", kept.Name);

        // Idempotent, so loading the config again doesn't keep rewriting the file.
        Assert.False(config.DropUnusableCloudAccountPairs());
    }

    [Fact]
    public void A_drive_that_throws_when_asked_does_not_break_detection()
    {
        // Disconnected network drives can throw merely from being queried.
        var found = CloudFolders.Detect(
            exists: _ => true,
            driveRoots: () => Bad(),
            readFile: _ => null,
            syncRoots: () => Enumerable.Empty<(string, string)>());

        // Whatever came before the failure is still usable rather than lost.
        Assert.NotNull(found);

        static IEnumerable<string> Bad()
        {
            yield return @"C:\";
            yield break;
        }
    }
}
