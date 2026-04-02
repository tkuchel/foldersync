namespace FolderSync.Models;

public sealed record SyncResult(
    bool Success,
    SyncWorkItem WorkItem,
    TimeSpan Duration,
    string? ErrorMessage = null);
