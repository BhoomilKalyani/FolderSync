using System.ComponentModel;
using System.Text.Json.Serialization;

namespace FolderSync;

/// <summary>Which way files travel between the two folders of a pair.</summary>
public enum SyncDirection
{
    /// <summary>Left is the source; changes are copied to the right folder.</summary>
    ToRight,
    /// <summary>Right is the source; changes are copied to the left folder.</summary>
    ToLeft,
    /// <summary>
    /// Both folders are merged: each side receives whatever is newer on the other.
    /// Deletions are never propagated - see <see cref="SyncPair"/> for why.
    /// </summary>
    TwoWay,
}

/// <summary>One folder pair: two folders and the rule for keeping them in step.</summary>
public sealed class SyncPair : INotifyPropertyChanged
{
    private string _name = "";
    private string _left = "";
    private string _right = "";
    private SyncDirection _direction = SyncDirection.ToRight;
    private bool _enabled = true;
    private bool _mirror;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Raises the computed labels too, since they all derive from the same few fields.</summary>
    private void NotifyWithLabels(string name)
    {
        Notify(name);
        Notify(nameof(DirectionText));
        Notify(nameof(ModeText));
        Notify(nameof(Summary));
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Notify(nameof(Name)); } }
    }

    public string Left
    {
        get => _left;
        set { if (_left != value) { _left = value; NotifyWithLabels(nameof(Left)); } }
    }

    public string Right
    {
        get => _right;
        set { if (_right != value) { _right = value; NotifyWithLabels(nameof(Right)); } }
    }

    public SyncDirection Direction
    {
        get => _direction;
        set { if (_direction != value) { _direction = value; NotifyWithLabels(nameof(Direction)); } }
    }

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Notify(nameof(Enabled)); } }
    }

    /// <summary>
    /// For a one-directional pair, whether the destination becomes an exact copy (extras deleted).
    /// Ignored for <see cref="SyncDirection.TwoWay"/>, which never deletes: with no record of the
    /// previous run there is no way to tell "deleted on one side" from "added on the other", so
    /// deleting would risk destroying files the user had just created.
    /// </summary>
    public bool Mirror
    {
        get => _mirror;
        set { if (_mirror != value) { _mirror = value; NotifyWithLabels(nameof(Mirror)); } }
    }

    /// <summary>True when this pair may delete files from the receiving side.</summary>
    [JsonIgnore]
    public bool DeletesExtras => Direction != SyncDirection.TwoWay && Mirror;

    [JsonIgnore]
    public string DirectionText => Direction switch
    {
        SyncDirection.ToRight => "Left → Right",
        SyncDirection.ToLeft => "Right → Left",
        _ => "Two-way",
    };

    [JsonIgnore]
    public string ModeText => Direction == SyncDirection.TwoWay
        ? "Merge (never deletes)"
        : Mirror ? "Mirror" : "Additive";

    [JsonIgnore]
    public string Summary => $"{DirectionText}  ·  {ModeText}";

    /// <summary>A display name, falling back to the folder names when none was given.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? SuggestName(Left, Right) : Name;

    public static string SuggestName(string left, string right)
    {
        string l = SafeLeaf(left);
        string r = SafeLeaf(right);
        if (l.Length == 0 && r.Length == 0) return "New pair";
        if (r.Length == 0) return l;
        if (l.Length == 0) return r;
        return l.Equals(r, StringComparison.OrdinalIgnoreCase) ? l : $"{l} ↔ {r}";
    }

    private static string SafeLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            return leaf.Length > 0 ? leaf : trimmed;   // e.g. "D:\" has no leaf
        }
        catch { return ""; }
    }

    /// <summary>
    /// The copy passes this pair performs, in order. Two-way is two non-deleting passes that each
    /// skip files the receiving side has newer, which gives "newer file wins" in both directions.
    /// </summary>
    public IReadOnlyList<SyncPass> Passes() => Direction switch
    {
        SyncDirection.ToRight => new[] { new SyncPass(Left, Right, Mirror, false) },
        SyncDirection.ToLeft => new[] { new SyncPass(Right, Left, Mirror, false) },
        _ => new[]
        {
            new SyncPass(Left, Right, false, true),
            new SyncPass(Right, Left, false, true),
        },
    };

    /// <summary>Why this pair cannot run, ignoring the filesystem, or null when it looks usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Left) || string.IsNullOrWhiteSpace(Right))
            return "both folders must be set";
        if (PathUtil.SameOrNested(Left, Right))
            return "the two folders are the same, or one is inside the other";
        return null;
    }

    /// <summary>
    /// The same check plus the folders themselves, or null when the pair can run. Used by both the
    /// pair dialog and the engine so they can never disagree about what is acceptable.
    ///
    /// The rule is direction-aware and symmetric: a folder being *read from* must already exist,
    /// while a folder being *written to* only needs a reachable drive, since it can be created.
    /// Two-way reads from both sides, so both must exist.
    /// </summary>
    public string? ValidateFolders()
    {
        if (Validate() is { } basic) return basic;

        foreach (var pass in Passes())
        {
            if (!Directory.Exists(pass.From))
                return $"{SideOf(pass.From)} folder does not exist: {pass.From}";

            // Catches a mistyped or unmounted drive, e.g. "X:\backup".
            if (!PathUtil.DriveAvailable(pass.To))
                return $"{SideOf(pass.To)} drive is not available: {pass.To}";
        }
        return null;
    }

    /// <summary>A non-blocking note, e.g. that the receiving folder will be created.</summary>
    public string? FolderNote()
    {
        if (ValidateFolders() is not null) return null;

        foreach (var pass in Passes())
            if (!Directory.Exists(pass.To))
                return $"{SideOf(pass.To)} folder does not exist yet and will be created: {pass.To}";
        return null;
    }

    private string SideOf(string path) =>
        string.Equals(path, Left, StringComparison.OrdinalIgnoreCase) ? "Left" : "Right";
}

/// <summary>A single one-directional copy that the engine runs.</summary>
/// <param name="From">Folder read from.</param>
/// <param name="To">Folder written to.</param>
/// <param name="Mirror">Delete items in <paramref name="To"/> that are absent from <paramref name="From"/>.</param>
/// <param name="SkipOlder">Never overwrite a file in <paramref name="To"/> that is newer (robocopy /XO).</param>
public readonly record struct SyncPass(string From, string To, bool Mirror, bool SkipOlder);
