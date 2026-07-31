using System.Text.Json;
using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Config behaviour, including the per-destination mode override and the email send rules.
///
/// Note: these tests never call SyncConfig.Load()/Save(), because those read and write the real
/// %LOCALAPPDATA%\FolderSync\config.json of whoever runs the tests.
/// </summary>
public class SyncConfigTests
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private static SyncConfig RoundTrip(SyncConfig cfg) =>
        JsonSerializer.Deserialize<SyncConfig>(JsonSerializer.Serialize(cfg, Opts))!;

    [Fact]
    public void Settings_survive_a_json_round_trip()
    {
        var cfg = new SyncConfig
        {
            Source = @"C:\data\Docs",
            Mirror = true,
            MinMinutesBetweenAutoSyncs = 42,
            RecycleRetentionDays = 12,
            RecycleFolderName = "_Trash",
            ExcludeFiles = new List<string> { "~$*", "*.tmp" },
            ExcludeFolders = new List<string> { "node_modules" },
            Targets = new List<SyncTarget>
            {
                new() { Name = "Cloud", Destination = @"G:\My Drive\Docs", Enabled = true, Mirror = true },
                new() { Name = "Backup", Destination = @"D:\Docs", Enabled = true, Mirror = false },
                new() { Name = "Other", Destination = @"E:\Docs", Enabled = false },
            },
        };

        var back = RoundTrip(cfg);

        Assert.Equal(@"C:\data\Docs", back.Source);
        Assert.Equal(42, back.MinMinutesBetweenAutoSyncs);
        Assert.Equal(12, back.RecycleRetentionDays);
        Assert.Equal("_Trash", back.RecycleFolderName);
        Assert.Equal(new[] { "~$*", "*.tmp" }, back.ExcludeFiles);
        Assert.Equal(new[] { "node_modules" }, back.ExcludeFolders);
        Assert.Equal(3, back.Targets.Count);
        Assert.True(back.Targets[0].Mirror);
        Assert.False(back.Targets[1].Mirror);
        Assert.Null(back.Targets[2].Mirror);       // no override stored
        Assert.False(back.Targets[2].Enabled);
    }

    [Fact]
    public void A_config_written_by_an_older_version_still_loads()
    {
        // No Mirror override on the target, and none of the newer top-level keys.
        const string json = """
        {
          "Source": "C:\\data\\Docs",
          "Mirror": true,
          "Targets": [ { "Name": "Cloud", "Destination": "G:\\My Drive\\Docs", "Enabled": true } ]
        }
        """;

        var cfg = JsonSerializer.Deserialize<SyncConfig>(json)!;

        Assert.Single(cfg.Targets);
        Assert.Null(cfg.Targets[0].Mirror);                       // inherits the global mode
        Assert.True(cfg.Targets[0].MirrorEffective(cfg.Mirror));
        Assert.True(cfg.RecycleDeletedFiles);                     // sensible default
        Assert.Equal(15, cfg.MinMinutesBetweenAutoSyncs);
    }

    [Theory]
    [InlineData(null, true, true)]      // no override -> follows global mirror
    [InlineData(null, false, false)]    // no override -> follows global additive
    [InlineData(true, false, true)]     // override wins over global
    [InlineData(false, true, false)]    // override wins over global
    public void MirrorEffective_resolves_the_override_against_the_global_setting(bool? overrideValue, bool global, bool expected)
    {
        var target = new SyncTarget { Mirror = overrideValue };

        Assert.Equal(expected, target.MirrorEffective(global));
    }

    [Theory]
    [InlineData(true, "Mirror")]
    [InlineData(false, "Additive")]
    [InlineData(null, "(not set)")]   // only until SyncConfig.Load fills it in
    public void ModeText_describes_the_mode(bool? mirror, string expected)
    {
        Assert.Equal(expected, new SyncTarget { Mirror = mirror }.ModeText);
    }

    [Fact]
    public void IsUnconfigured_is_true_until_a_source_and_a_destination_exist()
    {
        Assert.True(new SyncConfig().IsUnconfigured);
        Assert.True(new SyncConfig { Source = @"C:\data" }.IsUnconfigured);       // no destinations

        var configured = new SyncConfig
        {
            Source = @"C:\data",
            Targets = new List<SyncTarget> { new() { Destination = @"D:\data" } },
        };
        Assert.False(configured.IsUnconfigured);
    }

    [Theory]
    [InlineData(EmailMode.Always, true, true)]
    [InlineData(EmailMode.Always, false, true)]
    [InlineData(EmailMode.OnError, true, false)]
    [InlineData(EmailMode.OnError, false, true)]
    [InlineData(EmailMode.OnSuccess, true, true)]
    [InlineData(EmailMode.OnSuccess, false, false)]
    public void ShouldSend_follows_the_selected_mode(EmailMode mode, bool syncSucceeded, bool expected)
    {
        Assert.Equal(expected, new EmailSettings { Mode = mode }.ShouldSend(syncSucceeded));
    }

    [Fact]
    public void EffectiveFrom_falls_back_to_the_username()
    {
        Assert.Equal("me@example.com", new EmailSettings { Username = "me@example.com" }.EffectiveFrom);
        Assert.Equal("other@example.com",
            new EmailSettings { Username = "me@example.com", From = "other@example.com" }.EffectiveFrom);
    }

    [Fact]
    public void The_password_round_trips_and_is_not_stored_in_clear_text()
    {
        var e = new EmailSettings();
        Assert.False(e.HasPassword);

        e.SetPassword("abcd efgh ijkl mnop");

        Assert.True(e.HasPassword);
        Assert.Equal("abcd efgh ijkl mnop", e.GetPassword());
        Assert.DoesNotContain("abcd", e.ProtectedPassword);
    }

    [Fact]
    public void Clearing_the_password_empties_the_stored_value()
    {
        var e = new EmailSettings();
        e.SetPassword("secret");
        e.SetPassword("");

        Assert.False(e.HasPassword);
        Assert.Equal("", e.GetPassword());
    }
}
