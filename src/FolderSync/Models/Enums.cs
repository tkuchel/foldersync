namespace FolderSync.Models;

public enum WatcherChangeKind
{
    Created,
    Updated,
    Deleted,
    Renamed,
    Overflow,
    ReconcileRequested
}

public enum FileComparisonResult
{
    MissingDestination,
    Same,
    DifferentMetadataOnly,
    DifferentContent,
    TypeMismatch
}

public enum ConflictMode
{
    SourceWins,
    PreserveDestination,
    KeepNewest,
    SkipOnConflict
}

public enum DeleteMode
{
    Archive,
    Delete
}

public enum SyncMode
{
    OneWay,
    TwoWayPreview,
    TwoWaySafe,
    TwoWay
}

public enum TwoWayConflictMode
{
    Manual,
    KeepNewest,
    KeepBoth,
    PreferLeft,
    PreferRight
}
