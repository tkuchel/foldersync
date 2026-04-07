using FolderSync.Infrastructure;
using FolderSync.Models;

namespace FolderSync.Services;

public interface ITwoWayPreviewService
{
    Task<TwoWayPreviewRunResult> RunAsync(
        string profileName,
        SyncOptions options,
        string stateStorePath,
        CancellationToken cancellationToken = default);

    Task<TwoWayPreviewDetailedResult> RunDetailedAsync(
        SyncOptions options,
        ITwoWayStateStore stateStore,
        CancellationToken cancellationToken = default);
}

public sealed class TwoWayPreviewService : ITwoWayPreviewService
{
    private readonly IFileHasher _fileHasher;
    private readonly IPathSafetyService _pathSafetyService;
    private readonly ITwoWayPreviewClassifier _classifier;

    public TwoWayPreviewService(
        IFileHasher fileHasher,
        IPathSafetyService pathSafetyService,
        ITwoWayPreviewClassifier classifier)
    {
        _fileHasher = fileHasher;
        _pathSafetyService = pathSafetyService;
        _classifier = classifier;
    }

    public async Task<TwoWayPreviewRunResult> RunAsync(
        string profileName,
        SyncOptions options,
        string stateStorePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.SourcePath) || !Directory.Exists(options.SourcePath))
            throw new DirectoryNotFoundException($"Source directory does not exist: {options.SourcePath}");

        if (string.IsNullOrWhiteSpace(options.DestinationPath) || !Directory.Exists(options.DestinationPath))
            throw new DirectoryNotFoundException($"Destination directory does not exist: {options.DestinationPath}");

        var observedEntries = await EnumerateObservedEntriesAsync(options, cancellationToken);
        ITwoWayStateStore stateStore = new JsonTwoWayStateStore(stateStorePath);
        var previous = stateStore.Load();
        var completedAtUtc = DateTimeOffset.UtcNow;
        var result = _classifier.Classify(observedEntries, previous, completedAtUtc);
        stateStore.ApplyPreviewResult(result, completedAtUtc);

        return new TwoWayPreviewRunResult
        {
            ProfileName = profileName,
            StateStorePath = stateStorePath,
            ChangeCount = result.Changes.Count,
            ConflictCount = result.Conflicts.Count,
            CompletedAtUtc = completedAtUtc
        };
    }

    public async Task<TwoWayPreviewDetailedResult> RunDetailedAsync(
        SyncOptions options,
        ITwoWayStateStore stateStore,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.SourcePath) || !Directory.Exists(options.SourcePath))
            throw new DirectoryNotFoundException($"Source directory does not exist: {options.SourcePath}");

        if (string.IsNullOrWhiteSpace(options.DestinationPath) || !Directory.Exists(options.DestinationPath))
            throw new DirectoryNotFoundException($"Destination directory does not exist: {options.DestinationPath}");

        var observedEntries = await EnumerateObservedEntriesAsync(options, cancellationToken);
        var previous = stateStore.Load();
        var completedAtUtc = DateTimeOffset.UtcNow;
        var result = _classifier.Classify(observedEntries, previous, completedAtUtc);
        stateStore.ApplyPreviewResult(result, completedAtUtc);

        return new TwoWayPreviewDetailedResult
        {
            PreviewResult = result,
            CompletedAtUtc = completedAtUtc
        };
    }

    private async Task<List<TwoWayObservedEntry>> EnumerateObservedEntriesAsync(SyncOptions options, CancellationToken cancellationToken)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var allPaths = new HashSet<string>(comparer);
        var leftEntries = await ScanRootAsync(options.SourcePath, options, cancellationToken);
        var rightEntries = await ScanRootAsync(options.DestinationPath, options, cancellationToken);

        foreach (var path in leftEntries.Keys)
            allPaths.Add(path);
        foreach (var path in rightEntries.Keys)
            allPaths.Add(path);

        return allPaths
            .OrderBy(path => path, comparer)
            .Select(path => new TwoWayObservedEntry
            {
                RelativePath = path,
                Left = leftEntries.GetValueOrDefault(path),
                Right = rightEntries.GetValueOrDefault(path)
            })
            .ToList();
    }

    private async Task<Dictionary<string, FileFingerprint>> ScanRootAsync(
        string root,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        var directoryQueue = new Queue<string>();
        directoryQueue.Enqueue(root);

        while (directoryQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = directoryQueue.Dequeue();

            if (!string.Equals(currentDirectory, root, StringComparison.OrdinalIgnoreCase))
            {
                var relativeDirectory = Path.GetRelativePath(root, currentDirectory);
                entries[relativeDirectory] = CreateDirectoryFingerprint(currentDirectory);
            }

            IEnumerable<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(currentDirectory);
            }
            catch
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                if (_pathSafetyService.IsReparsePoint(subDirectory))
                    continue;

                directoryQueue.Enqueue(subDirectory);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDirectory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (_pathSafetyService.IsReparsePoint(file))
                    continue;

                var relativePath = Path.GetRelativePath(root, file);
                entries[relativePath] = await CreateFileFingerprintAsync(file, options, cancellationToken);
            }
        }

        return entries;
    }

    private static FileFingerprint CreateDirectoryFingerprint(string path)
    {
        var info = new DirectoryInfo(path);
        return new FileFingerprint(-1, info.LastWriteTimeUtc);
    }

    private async Task<FileFingerprint> CreateFileFingerprintAsync(string path, SyncOptions options, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var hash = options.TwoWay.RequireHashComparison || options.UseHashComparison
            ? await _fileHasher.ComputeHashAsync(path, cancellationToken)
            : null;

        return new FileFingerprint(info.Length, info.LastWriteTimeUtc, hash);
    }
}
