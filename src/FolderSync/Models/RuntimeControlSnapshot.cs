namespace FolderSync.Models;

public sealed class RuntimeControlSnapshot
{
    public bool IsPaused { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? ChangedAtUtc { get; set; }
}
