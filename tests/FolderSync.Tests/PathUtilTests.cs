using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// These guard the check that stops a mirror from targeting its own source, which would let
/// /MIR delete the user's files or loop forever.
/// </summary>
public class PathUtilTests
{
    [Theory]
    [InlineData(@"C:\data\Docs", @"C:\data\Docs")]                    // identical
    [InlineData(@"C:\data\Docs", @"C:\data\Docs\")]                   // trailing separator
    [InlineData(@"C:\data\Docs\", @"C:\data\docs")]                   // different case
    [InlineData(@"C:\data\Docs", @"C:\data\Docs\Sub")]                // destination inside source
    [InlineData(@"C:\data\Docs\Sub", @"C:\data\Docs")]                // source inside destination
    [InlineData(@"C:\data\Docs", @"C:\data\Docs\a\b\c")]              // deeply nested
    public void SameOrNested_is_true_for_overlapping_paths(string a, string b)
    {
        Assert.True(PathUtil.SameOrNested(a, b));
    }

    [Theory]
    [InlineData(@"C:\data\Docs", @"C:\data\Photos")]                  // siblings
    [InlineData(@"C:\data\Docs", @"D:\Docs")]                         // different volume
    [InlineData(@"C:\data\Docs", @"C:\data\DocsBackup")]              // shared name prefix only
    [InlineData(@"C:\data\Docs", "")]
    [InlineData("", @"C:\data\Docs")]
    [InlineData("", "")]
    public void SameOrNested_is_false_for_independent_paths(string a, string b)
    {
        Assert.False(PathUtil.SameOrNested(a, b));
    }

    [Fact]
    public void SameOrNested_does_not_treat_a_name_prefix_as_nesting()
    {
        // "DocsBackup" starts with "Docs" but is a separate folder - a plain StartsWith would
        // wrongly refuse to sync here.
        Assert.False(PathUtil.SameOrNested(@"C:\data\Docs", @"C:\data\DocsBackup\Inner"));
    }

    [Fact]
    public void Cloud_storage_mounted_as_a_drive_is_compared_like_any_other_folder()
    {
        // Google Drive for Desktop appears as a drive letter, so nesting there is just as
        // destructive as nesting locally and must be caught the same way.
        Assert.True(PathUtil.SameOrNested(@"G:\My Drive\Docs", @"G:\My Drive\Docs"));
        Assert.True(PathUtil.SameOrNested(@"G:\My Drive", @"G:\My Drive\Docs"));
        Assert.False(PathUtil.SameOrNested(@"G:\My Drive\Docs", @"G:\My Drive\Photos"));
        Assert.False(PathUtil.SameOrNested(@"G:\My Drive\Docs", @"C:\Docs"));
    }

    [Fact]
    public void DriveAvailable_accepts_the_system_drive()
    {
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.True(PathUtil.DriveAvailable(Path.Combine(systemDrive, "Windows")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DriveAvailable_rejects_blank_paths(string path)
    {
        Assert.False(PathUtil.DriveAvailable(path));
    }

    [Fact]
    public void DriveAvailable_rejects_a_drive_letter_that_is_not_mounted()
    {
        // Find a letter with no drive attached; if the machine somehow uses them all, skip.
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        char? free = "ZYXWVU".FirstOrDefault(c => !used.Contains(c));
        if (free is null or '\0') return;

        Assert.False(PathUtil.DriveAvailable($@"{free}:\some\folder"));
    }
}
