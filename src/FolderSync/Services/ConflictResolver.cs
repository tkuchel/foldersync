using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IConflictResolver
{
    ConflictResolutionResult Resolve(
        FileComparisonResult comparison,
        string sourcePath,
        string destinationPath);
}

public sealed class ConflictResolver : IConflictResolver
{
    private readonly ConflictMode _mode;
    private readonly ILogger<ConflictResolver> _logger;

    public ConflictResolver(IOptions<SyncOptions> options, ILogger<ConflictResolver> logger)
    {
        _mode = options.Value.ConflictMode;
        _logger = logger;
    }

    public ConflictResolutionResult Resolve(
        FileComparisonResult comparison,
        string sourcePath,
        string destinationPath)
    {
        if (comparison is FileComparisonResult.MissingDestination)
            return new ConflictResolutionResult(true, "Destination does not exist");

        if (comparison is FileComparisonResult.Same or FileComparisonResult.DifferentMetadataOnly)
            return new ConflictResolutionResult(false, "Files are identical");

        return _mode switch
        {
            ConflictMode.SourceWins => new ConflictResolutionResult(true, "Source wins — overwriting destination"),

            ConflictMode.PreserveDestination => new ConflictResolutionResult(false,
                "Destination preserved — skipping (destination differs)"),

            ConflictMode.KeepNewest => ResolveByTimestamp(sourcePath, destinationPath),

            ConflictMode.SkipOnConflict => new ConflictResolutionResult(false,
                "Conflict detected — skipping"),

            _ => new ConflictResolutionResult(false, $"Unknown conflict mode: {_mode}")
        };
    }

    private ConflictResolutionResult ResolveByTimestamp(string sourcePath, string destinationPath)
    {
        var sourceTime = File.GetLastWriteTimeUtc(sourcePath);
        var destTime = File.GetLastWriteTimeUtc(destinationPath);

        if (sourceTime >= destTime)
        {
            return new ConflictResolutionResult(true, "Source is newer or same age — overwriting");
        }

        _logger.LogInformation(
            "Destination is newer than source for {Path}, skipping",
            sourcePath);

        return new ConflictResolutionResult(false, "Destination is newer — keeping destination");
    }
}
