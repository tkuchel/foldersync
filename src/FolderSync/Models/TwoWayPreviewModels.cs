namespace FolderSync.Models;

public enum TwoWayChangeKind
{
    NoChange,
    LeftOnly,
    RightOnly,
    LeftChanged,
    RightChanged,
    BothChanged,
    DeleteOnLeft,
    DeleteOnRight,
    Conflict
}

public sealed class TwoWayObservedEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public FileFingerprint? Left { get; set; }
    public FileFingerprint? Right { get; set; }
}

public sealed class TwoWayPreviewChange
{
    public string RelativePath { get; set; } = string.Empty;
    public TwoWayChangeKind Kind { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class TwoWayConflictRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset DetectedAtUtc { get; set; }
    public TwoWayConflictMode RecommendedMode { get; set; } = TwoWayConflictMode.Manual;
}

public sealed class TwoWayPreviewResult
{
    public List<TwoWayPreviewChange> Changes { get; init; } = [];
    public List<TwoWayConflictRecord> Conflicts { get; init; } = [];
}
