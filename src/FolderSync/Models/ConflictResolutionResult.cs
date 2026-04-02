namespace FolderSync.Models;

public sealed record ConflictResolutionResult(
    bool ShouldProceed,
    string Reason);
