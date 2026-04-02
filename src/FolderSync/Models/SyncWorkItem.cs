namespace FolderSync.Models;

public sealed record SyncWorkItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required WatcherChangeKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public string? OldSourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public string? OldDestinationPath { get; init; }
    public DateTimeOffset EnqueuedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsDirectory { get; init; }
}
