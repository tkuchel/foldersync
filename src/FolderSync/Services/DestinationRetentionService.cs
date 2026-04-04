using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IDestinationRetentionService
{
    Task ApplyAsync(RetentionExecutionTrigger trigger, CancellationToken cancellationToken = default);
}

public enum RetentionExecutionTrigger
{
    Reconciliation,
    Sync
}

public sealed class DestinationRetentionService : IDestinationRetentionService
{
    private sealed record RetentionCandidate(string FullPath, string RelativePath, string Name, DateTime LastWriteTimeUtc);

    private readonly string _profileName;
    private readonly SyncOptions _options;
    private readonly IFileOperationService _fileOperations;
    private readonly ILogger<DestinationRetentionService> _logger;

    public DestinationRetentionService(
        string profileName,
        IOptions<SyncOptions> options,
        IFileOperationService fileOperations,
        ILogger<DestinationRetentionService> logger)
    {
        _profileName = profileName;
        _options = options.Value;
        _fileOperations = fileOperations;
        _logger = logger;
    }

    public async Task ApplyAsync(RetentionExecutionTrigger trigger, CancellationToken cancellationToken = default)
    {
        var retention = _options.Retention;
        if (!retention.Enabled || retention.KeepNewestCount <= 0 || !ShouldRunForTrigger(retention, trigger))
            return;

        var destinationRoot = Path.GetFullPath(_options.DestinationPath);
        var retentionRoot = ResolveRetentionRoot(destinationRoot, retention.RelativePath);
        if (!Directory.Exists(retentionRoot))
            return;

        var candidates = GetCandidates(destinationRoot, retentionRoot, retention)
            .ToList();

        if (candidates.Count <= retention.KeepNewestCount)
            return;

        var orderedCandidates = retention.SortBy switch
        {
            RetentionSortMode.LastWriteTimeUtcDescending => candidates
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                .ThenByDescending(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => candidates
                .OrderByDescending(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var itemsToPrune = orderedCandidates
            .Skip(retention.KeepNewestCount)
            .Where(item => IsOldEnough(item, retention))
            .ToList();

        foreach (var item in itemsToPrune)
        {
            _logger.LogInformation(
                "[{Profile}] Pruning retained backup {ItemType} {ItemName} from destination (keeping newest {KeepCount})",
                _profileName,
                retention.ItemType == RetentionItemType.Files ? "file" : "directory",
                item.Name,
                retention.KeepNewestCount);

            await _fileOperations.DeleteOrArchiveForRetentionAsync(item.FullPath, item.RelativePath, cancellationToken);
        }
    }

    private static bool ShouldRunForTrigger(DestinationRetentionOptions retention, RetentionExecutionTrigger trigger)
    {
        return retention.TriggerMode switch
        {
            RetentionTriggerMode.SyncOnly => trigger == RetentionExecutionTrigger.Sync,
            RetentionTriggerMode.ReconciliationAndSync => true,
            _ => trigger == RetentionExecutionTrigger.Reconciliation
        };
    }

    private static bool IsOldEnough(RetentionCandidate item, DestinationRetentionOptions retention)
    {
        if (retention.MinAgeHours <= 0)
            return true;

        return item.LastWriteTimeUtc <= DateTime.UtcNow.AddHours(-retention.MinAgeHours);
    }

    private IEnumerable<RetentionCandidate> GetCandidates(string destinationRoot, string retentionRoot, DestinationRetentionOptions retention)
    {
        var searchOption = retention.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return retention.ItemType switch
        {
            RetentionItemType.Files => Directory.EnumerateFiles(retentionRoot, retention.SearchPattern, searchOption)
                .Select(path => new FileInfo(path))
                .Select(file => new RetentionCandidate(file.FullName, Path.GetRelativePath(destinationRoot, file.FullName), file.Name, file.LastWriteTimeUtc)),
            _ => Directory.EnumerateDirectories(retentionRoot, retention.SearchPattern, searchOption)
                .Where(path => !IsExcludedDirectory(path, destinationRoot))
                .Select(path => new DirectoryInfo(path))
                .Select(directory => new RetentionCandidate(directory.FullName, Path.GetRelativePath(destinationRoot, directory.FullName), directory.Name, directory.LastWriteTimeUtc))
        };
    }

    private static string ResolveRetentionRoot(string destinationRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return destinationRoot;

        var combinedPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
        var normalizedDestinationRoot = destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.Equals(combinedPath, normalizedDestinationRoot, StringComparison.OrdinalIgnoreCase) &&
            !combinedPath.StartsWith(normalizedDestinationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Retention RelativePath must stay within the destination root: {relativePath}");
        }

        return combinedPath;
    }

    private bool IsExcludedDirectory(string path, string destinationRoot)
    {
        if (_options.DeleteMode != DeleteMode.Archive)
            return false;

        var archiveRoot = string.IsNullOrWhiteSpace(_options.DeleteArchivePath)
            ? Path.Combine(destinationRoot, ".deleted")
            : Path.GetFullPath(_options.DeleteArchivePath);

        return string.Equals(
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            archiveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
