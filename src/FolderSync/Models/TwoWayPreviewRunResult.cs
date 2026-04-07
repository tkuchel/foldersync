namespace FolderSync.Models;

public sealed class TwoWayPreviewRunResult
{
    public required string ProfileName { get; init; }
    public required string StateStorePath { get; init; }
    public int ChangeCount { get; init; }
    public int ConflictCount { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}

public sealed class TwoWayPreviewDetailedResult
{
    public required TwoWayPreviewResult PreviewResult { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}
