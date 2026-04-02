namespace FolderSync.Models;

public sealed class RuntimeHealthSnapshot
{
    public required string ServiceName { get; init; }
    public required string ServiceState { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? LastError { get; set; }
    public List<ProfileHealthSnapshot> Profiles { get; init; } = [];
}

public sealed class ProfileHealthSnapshot
{
    public required string Name { get; init; }
    public string State { get; set; } = "Starting";
    public long ProcessedCount { get; set; }
    public long SucceededCount { get; set; }
    public long SkippedCount { get; set; }
    public long FailedCount { get; set; }
    public long WatcherOverflowCount { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public DateTimeOffset? LastFailedSyncUtc { get; set; }
    public string? LastFailure { get; set; }
    public ReconciliationHealthSnapshot Reconciliation { get; init; } = new();
}

public sealed class ReconciliationHealthSnapshot
{
    public long RunCount { get; set; }
    public string? LastTrigger { get; set; }
    public DateTimeOffset? LastStartedAtUtc { get; set; }
    public DateTimeOffset? LastCompletedAtUtc { get; set; }
    public double? LastDurationMs { get; set; }
    public bool? LastSuccess { get; set; }
    public int? LastExitCode { get; set; }
    public string? LastExitDescription { get; set; }
    public RobocopySummarySnapshot? LastSummary { get; set; }
}

public sealed class RobocopySummarySnapshot
{
    public int? DirectoriesTotal { get; set; }
    public int? DirectoriesCopied { get; set; }
    public int? DirectoriesSkipped { get; set; }
    public int? DirectoriesExtras { get; set; }
    public int? FilesTotal { get; set; }
    public int? FilesCopied { get; set; }
    public int? FilesSkipped { get; set; }
    public int? FilesExtras { get; set; }
    public int? FilesFailed { get; set; }
}
