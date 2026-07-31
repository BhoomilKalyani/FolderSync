using System.Text.Json;
using FolderSync;

namespace FolderSync.Tests;

/// <summary>
/// Folder-pair behaviour: which copy passes each direction produces, what may delete, and that an
/// old single-source config migrates without changing what the app actually does.
/// </summary>
public class SyncPairTests
{
    private const string L = @"C:\data\Left";
    private const string R = @"D:\Right";

    private static SyncPair Pair(SyncDirection dir, bool mirror = false) =>
        new() { Left = L, Right = R, Direction = dir, Mirror = mirror, Enabled = true };

    // ---------- passes ----------

    [Fact]
    public void ToRight_copies_left_to_right_only()
    {
        var passes = Pair(SyncDirection.ToRight).Passes();

        Assert.Single(passes);
        Assert.Equal(L, passes[0].From);
        Assert.Equal(R, passes[0].To);
        Assert.False(passes[0].SkipOlder);
    }

    [Fact]
    public void ToLeft_copies_right_to_left_only()
    {
        var passes = Pair(SyncDirection.ToLeft).Passes();

        Assert.Single(passes);
        Assert.Equal(R, passes[0].From);
        Assert.Equal(L, passes[0].To);
    }

    [Fact]
    public void One_directional_passes_honour_the_mirror_setting()
    {
        Assert.True(Pair(SyncDirection.ToRight, mirror: true).Passes()[0].Mirror);
        Assert.False(Pair(SyncDirection.ToRight, mirror: false).Passes()[0].Mirror);
        Assert.True(Pair(SyncDirection.ToLeft, mirror: true).Passes()[0].Mirror);
    }

    [Fact]
    public void TwoWay_runs_both_directions_and_never_deletes()
    {
        // Mirror is deliberately ignored: with no record of the previous run, a deletion on one
        // side is indistinguishable from a new file on the other.
        var passes = Pair(SyncDirection.TwoWay, mirror: true).Passes();

        Assert.Equal(2, passes.Count);
        Assert.Equal((L, R), (passes[0].From, passes[0].To));
        Assert.Equal((R, L), (passes[1].From, passes[1].To));
        Assert.All(passes, p => Assert.False(p.Mirror));
    }

    [Fact]
    public void TwoWay_skips_older_files_so_the_newer_copy_wins()
    {
        // /XO in both directions is what resolves a conflict in favour of the newer file.
        Assert.All(Pair(SyncDirection.TwoWay).Passes(), p => Assert.True(p.SkipOlder));
    }

    [Theory]
    [InlineData(SyncDirection.ToRight, true, true)]
    [InlineData(SyncDirection.ToRight, false, false)]
    [InlineData(SyncDirection.ToLeft, true, true)]
    [InlineData(SyncDirection.TwoWay, true, false)]   // two-way can never delete
    [InlineData(SyncDirection.TwoWay, false, false)]
    public void DeletesExtras_is_only_true_for_a_one_directional_mirror(SyncDirection dir, bool mirror, bool expected)
    {
        Assert.Equal(expected, Pair(dir, mirror).DeletesExtras);
    }

    // ---------- labels ----------

    [Theory]
    [InlineData(SyncDirection.ToRight, "Left → Right")]
    [InlineData(SyncDirection.ToLeft, "Right → Left")]
    [InlineData(SyncDirection.TwoWay, "Two-way")]
    public void DirectionText_reads_naturally(SyncDirection dir, string expected)
    {
        Assert.Equal(expected, Pair(dir).DirectionText);
    }

    [Fact]
    public void ModeText_says_two_way_never_deletes()
    {
        Assert.Equal("Merge (never deletes)", Pair(SyncDirection.TwoWay, mirror: true).ModeText);
        Assert.Equal("Mirror", Pair(SyncDirection.ToRight, mirror: true).ModeText);
        Assert.Equal("Additive", Pair(SyncDirection.ToRight, mirror: false).ModeText);
    }

    [Fact]
    public void DisplayName_falls_back_to_the_folder_names()
    {
        Assert.Equal("Documents", new SyncPair { Name = "Documents", Left = L, Right = R }.DisplayName);
        Assert.Equal("Left ↔ Right", new SyncPair { Left = L, Right = R }.DisplayName);
        Assert.Equal("Docs", new SyncPair { Left = @"C:\a\Docs", Right = @"D:\b\Docs" }.DisplayName);
        Assert.Equal("New pair", new SyncPair().DisplayName);
    }

    // ---------- validation ----------

    [Fact]
    public void A_pair_needs_two_separate_folders()
    {
        Assert.NotNull(new SyncPair { Left = "", Right = R }.Validate());
        Assert.NotNull(new SyncPair { Left = L, Right = "" }.Validate());
        Assert.NotNull(new SyncPair { Left = L, Right = L }.Validate());
        Assert.NotNull(new SyncPair { Left = L, Right = Path.Combine(L, "Inner") }.Validate());
        Assert.Null(new SyncPair { Left = L, Right = R }.Validate());
    }

    // ---------- folder validation (uses real folders) ----------

    /// <summary>
    /// The rule is symmetric: whichever side is being READ must exist, whichever side is being
    /// WRITTEN may be created. An earlier version only ever checked the left folder, so a mistyped
    /// right-hand path produced no error at all.
    /// </summary>
    public sealed class FolderValidation : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FSPairVal", Guid.NewGuid().ToString("N"));
        private readonly string _real;
        private readonly string _missing;

        public FolderValidation()
        {
            _real = Path.Combine(_root, "real");
            _missing = Path.Combine(_root, "not-created-yet");
            Directory.CreateDirectory(_real);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        private static string Unmounted()
        {
            var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
            char free = "ZYXWVU".First(c => !used.Contains(c));
            return $@"{free}:\nowhere";
        }

        [Fact]
        public void A_missing_RIGHT_folder_is_an_error_when_the_right_side_is_read()
        {
            var pair = new SyncPair { Left = _real, Right = _missing, Direction = SyncDirection.ToLeft };

            var problem = pair.ValidateFolders();

            Assert.NotNull(problem);
            Assert.Contains("Right", problem);
        }

        [Fact]
        public void A_missing_LEFT_folder_is_an_error_when_the_left_side_is_read()
        {
            var pair = new SyncPair { Left = _missing, Right = _real, Direction = SyncDirection.ToRight };

            var problem = pair.ValidateFolders();

            Assert.NotNull(problem);
            Assert.Contains("Left", problem);
        }

        [Fact]
        public void TwoWay_requires_both_folders_because_it_reads_from_both()
        {
            Assert.NotNull(new SyncPair { Left = _real, Right = _missing, Direction = SyncDirection.TwoWay }.ValidateFolders());
            Assert.NotNull(new SyncPair { Left = _missing, Right = _real, Direction = SyncDirection.TwoWay }.ValidateFolders());
        }

        [Theory]
        [InlineData(SyncDirection.ToRight)]   // right is written, so it may be created
        [InlineData(SyncDirection.ToLeft)]    // left is written, so it may be created
        public void A_receiving_folder_that_does_not_exist_yet_is_allowed(SyncDirection direction)
        {
            var pair = direction == SyncDirection.ToRight
                ? new SyncPair { Left = _real, Right = _missing, Direction = direction }
                : new SyncPair { Left = _missing, Right = _real, Direction = direction };

            Assert.Null(pair.ValidateFolders());
            Assert.NotNull(pair.FolderNote());          // but the user is told it will be created
        }

        [Fact]
        public void An_unmounted_drive_is_an_error_even_on_the_receiving_side()
        {
            // Catches a typo such as "X:\backup" that no amount of creating could fix.
            var pair = new SyncPair { Left = _real, Right = Unmounted(), Direction = SyncDirection.ToRight };

            var problem = pair.ValidateFolders();

            Assert.NotNull(problem);
            Assert.Contains("drive is not available", problem);
        }

        [Fact]
        public void A_pair_with_two_real_folders_has_no_problem_and_no_note()
        {
            var other = Path.Combine(_root, "other");
            Directory.CreateDirectory(other);
            var pair = new SyncPair { Left = _real, Right = other, Direction = SyncDirection.TwoWay };

            Assert.Null(pair.ValidateFolders());
            Assert.Null(pair.FolderNote());
        }
    }

    // ---------- change notification ----------

    [Fact]
    public void Editing_a_pair_notifies_the_derived_labels()
    {
        var pair = Pair(SyncDirection.ToRight);
        var raised = new List<string>();
        pair.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        pair.Direction = SyncDirection.TwoWay;

        Assert.Contains(nameof(SyncPair.Direction), raised);
        Assert.Contains(nameof(SyncPair.DirectionText), raised);
        Assert.Contains(nameof(SyncPair.ModeText), raised);
    }

    // ---------- migration ----------

    [Fact]
    public void An_old_single_source_config_becomes_one_pair_per_destination()
    {
        var cfg = new SyncConfig
        {
            Source = @"C:\Docs",
            Mirror = true,
            Targets = new List<SyncTarget>
            {
                new() { Name = "Cloud", Destination = @"G:\My Drive\Docs", Enabled = true, Mirror = true },
                new() { Name = "Backup", Destination = @"D:\Docs", Enabled = true, Mirror = false },
                new() { Name = "Off", Destination = @"E:\Docs", Enabled = false },
            },
        };

        Assert.True(cfg.MigrateTargetsToPairs());

        Assert.Equal(3, cfg.Pairs.Count);
        Assert.All(cfg.Pairs, p => Assert.Equal(@"C:\Docs", p.Left));
        Assert.All(cfg.Pairs, p => Assert.Equal(SyncDirection.ToRight, p.Direction));

        Assert.True(cfg.Pairs[0].Mirror);                       // kept its own mirror setting
        Assert.False(cfg.Pairs[1].Mirror);
        Assert.False(cfg.Pairs[2].Enabled);                     // kept its disabled state

        // The legacy fields are cleared so they can't drift out of step with the pairs.
        Assert.Empty(cfg.Targets);
        Assert.Equal("", cfg.Source);
    }

    [Fact]
    public void A_destination_with_no_mode_inherits_the_old_global_mirror()
    {
        var cfg = new SyncConfig
        {
            Source = @"C:\Docs",
            Mirror = true,
            Targets = new List<SyncTarget> { new() { Name = "Cloud", Destination = @"D:\Docs" } },
        };

        cfg.MigrateTargetsToPairs();

        Assert.True(cfg.Pairs[0].Mirror);
    }

    [Fact]
    public void Migration_does_not_run_twice_or_on_an_empty_config()
    {
        var already = new SyncConfig
        {
            Source = @"C:\Docs",
            Targets = new List<SyncTarget> { new() { Destination = @"D:\Docs" } },
            Pairs = new List<SyncPair> { new() { Left = @"C:\x", Right = @"D:\y" } },
        };
        Assert.False(already.MigrateTargetsToPairs());
        Assert.Single(already.Pairs);

        Assert.False(new SyncConfig().MigrateTargetsToPairs());
    }

    [Fact]
    public void Pairs_survive_a_json_round_trip()
    {
        var cfg = new SyncConfig
        {
            Pairs = new List<SyncPair>
            {
                new() { Name = "Two-way", Left = L, Right = R, Direction = SyncDirection.TwoWay, Enabled = true },
                new() { Name = "Mirror", Left = L, Right = @"E:\M", Direction = SyncDirection.ToLeft, Mirror = true, Enabled = false },
            },
        };

        var back = JsonSerializer.Deserialize<SyncConfig>(JsonSerializer.Serialize(cfg))!;

        Assert.Equal(2, back.Pairs.Count);
        Assert.Equal(SyncDirection.TwoWay, back.Pairs[0].Direction);
        Assert.Equal(SyncDirection.ToLeft, back.Pairs[1].Direction);
        Assert.True(back.Pairs[1].Mirror);
        Assert.False(back.Pairs[1].Enabled);
    }

    [Fact]
    public void ActivePairs_and_IsUnconfigured_track_the_enabled_pairs()
    {
        var cfg = new SyncConfig();
        Assert.True(cfg.IsUnconfigured);

        cfg.Pairs.Add(new SyncPair { Left = L, Right = R, Enabled = false });
        Assert.False(cfg.IsUnconfigured);      // configured, just not switched on
        Assert.Empty(cfg.ActivePairs);

        cfg.Pairs.Add(new SyncPair { Left = L, Right = @"E:\z", Enabled = true });
        Assert.Single(cfg.ActivePairs);
    }
}
