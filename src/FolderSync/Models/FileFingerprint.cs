namespace FolderSync.Models;

public sealed record FileFingerprint(
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? ContentHash = null);
