namespace FolderSync.Models;

public sealed class AlertNotification
{
    public required string ServiceName { get; init; }
    public required string ProfileName { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
}
