using System.Diagnostics;
using FolderSync.Models;
using Microsoft.Extensions.Logging;

namespace FolderSync.Services;

public interface ISyncProcessor
{
    Task<SyncResult> ProcessAsync(SyncWorkItem workItem, CancellationToken cancellationToken = default);
}

public sealed class SyncProcessor : ISyncProcessor
{
    private readonly IStabilityChecker _stabilityChecker;
    private readonly IFileComparisonService _comparisonService;
    private readonly IConflictResolver _conflictResolver;
    private readonly IFileOperationService _fileOperations;
    private readonly IPathMappingService _pathMapping;
    private readonly ILogger<SyncProcessor> _logger;

    public SyncProcessor(
        IStabilityChecker stabilityChecker,
        IFileComparisonService comparisonService,
        IConflictResolver conflictResolver,
        IFileOperationService fileOperations,
        IPathMappingService pathMapping,
        ILogger<SyncProcessor> logger)
    {
        _stabilityChecker = stabilityChecker;
        _comparisonService = comparisonService;
        _conflictResolver = conflictResolver;
        _fileOperations = fileOperations;
        _pathMapping = pathMapping;
        _logger = logger;
    }

    public async Task<SyncResult> ProcessAsync(SyncWorkItem workItem, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var result = workItem.Kind switch
            {
                WatcherChangeKind.Created or WatcherChangeKind.Updated =>
                    await ProcessCreateOrUpdateAsync(workItem, cancellationToken),

                WatcherChangeKind.Deleted =>
                    await ProcessDeleteAsync(workItem, cancellationToken),

                WatcherChangeKind.Renamed =>
                    await ProcessRenameAsync(workItem, cancellationToken),

                _ => new SyncResult(true, workItem, sw.Elapsed, $"No action for {workItem.Kind}")
            };

            return result with { Duration = sw.Elapsed };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing {Kind} for {Path}", workItem.Kind, workItem.SourcePath);
            return new SyncResult(false, workItem, sw.Elapsed, ex.Message);
        }
    }

    private async Task<SyncResult> ProcessCreateOrUpdateAsync(SyncWorkItem workItem, CancellationToken ct)
    {
        // Handle directory creation
        if (workItem.IsDirectory)
        {
            await _fileOperations.EnsureDirectoryExistsAsync(workItem.DestinationPath);
            return new SyncResult(true, workItem, TimeSpan.Zero, "Directory ensured");
        }

        // Wait for file stability
        if (!await _stabilityChecker.WaitForFileReadyAsync(workItem.SourcePath, ct))
        {
            _logger.LogWarning("File not stable, skipping: {Path}", workItem.SourcePath);
            return new SyncResult(false, workItem, TimeSpan.Zero, "File not stable");
        }

        // Compare source and destination
        var comparison = await _comparisonService.CompareAsync(workItem.SourcePath, workItem.DestinationPath, ct);

        if (comparison == FileComparisonResult.Same)
        {
            _logger.LogDebug("Skipped unchanged file: {Path}", workItem.SourcePath);
            return new SyncResult(true, workItem, TimeSpan.Zero, "Unchanged");
        }

        // Resolve conflicts
        var resolution = _conflictResolver.Resolve(comparison, workItem.SourcePath, workItem.DestinationPath);

        if (!resolution.ShouldProceed)
        {
            _logger.LogInformation("Skipped {Path}: {Reason}", workItem.SourcePath, resolution.Reason);
            return new SyncResult(true, workItem, TimeSpan.Zero, resolution.Reason);
        }

        // Perform copy
        await _fileOperations.CopyFileAsync(workItem.SourcePath, workItem.DestinationPath, ct);

        var relativePath = _pathMapping.GetRelativePath(workItem.SourcePath);
        _logger.LogInformation("Synced {RelativePath} ({Comparison})", relativePath, comparison);

        return new SyncResult(true, workItem, TimeSpan.Zero);
    }

    private async Task<SyncResult> ProcessDeleteAsync(SyncWorkItem workItem, CancellationToken ct)
    {
        if (!File.Exists(workItem.DestinationPath) && !Directory.Exists(workItem.DestinationPath))
        {
            return new SyncResult(true, workItem, TimeSpan.Zero, "Destination already absent");
        }

        var relativePath = _pathMapping.GetRelativePath(workItem.SourcePath);
        await _fileOperations.DeleteOrArchiveAsync(workItem.DestinationPath, relativePath, ct);

        return new SyncResult(true, workItem, TimeSpan.Zero);
    }

    private async Task<SyncResult> ProcessRenameAsync(SyncWorkItem workItem, CancellationToken ct)
    {
        // Wait for stability of the new file
        if (!workItem.IsDirectory)
        {
            if (!await _stabilityChecker.WaitForFileReadyAsync(workItem.SourcePath, ct))
            {
                _logger.LogWarning("Renamed file not stable, treating as create: {Path}", workItem.SourcePath);
                return await ProcessCreateOrUpdateAsync(workItem with { Kind = WatcherChangeKind.Created }, ct);
            }
        }

        // If old destination exists, try to rename it
        if (workItem.OldDestinationPath is not null && (File.Exists(workItem.OldDestinationPath) || Directory.Exists(workItem.OldDestinationPath)))
        {
            try
            {
                await _fileOperations.RenameAsync(workItem.OldDestinationPath, workItem.DestinationPath, ct);
                _logger.LogInformation("Renamed {OldPath} -> {NewPath}", workItem.OldDestinationPath, workItem.DestinationPath);
                return new SyncResult(true, workItem, TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rename failed, falling back to copy for {Path}", workItem.SourcePath);
            }
        }

        // Fallback: treat as create
        return await ProcessCreateOrUpdateAsync(workItem with { Kind = WatcherChangeKind.Created }, ct);
    }
}
