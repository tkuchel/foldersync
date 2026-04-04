namespace FolderSync.Models;

public sealed class TwoWayStateSnapshot
{
    public string SchemaVersion { get; set; } = "1";
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public List<TwoWayStateEntry> Entries { get; set; } = [];
    public List<TwoWayConflictRecord> Conflicts { get; set; } = [];
}

public sealed class TwoWayStateEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public string? LeftHash { get; set; }
    public string? RightHash { get; set; }
    public DateTimeOffset? LastSeenLeftUtc { get; set; }
    public DateTimeOffset? LastSeenRightUtc { get; set; }
    public DateTimeOffset? LastResolvedUtc { get; set; }
    public string? LastResolution { get; set; }
    public string? ConflictState { get; set; }
}
