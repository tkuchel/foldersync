namespace FolderSync.Models;

public sealed record WatcherEvent
{
    public required WatcherChangeKind Kind { get; init; }
    public required string FullPath { get; init; }
    public string? OldFullPath { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool IsDirectory { get; init; }
}
