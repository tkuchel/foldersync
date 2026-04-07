using FolderSync.Models;
using Microsoft.Extensions.Logging;

namespace FolderSync.Services;

public interface ITwoWayApplyService
{
    Task<TwoWayApplyResult> ApplyAsync(
        TwoWayPreviewResult previewResult,
        string leftRoot,
        string rightRoot,
        ITwoWayStateStore stateStore,
        CancellationToken cancellationToken = default);
}

public sealed class TwoWayApplyService : ITwoWayApplyService
{
    private readonly IFileOperationService _leftToRightOps;
    private readonly IFileOperationService _rightToLeftOps;
    private readonly ILogger<TwoWayApplyService> _logger;

    public TwoWayApplyService(
        IFileOperationService leftToRightOps,
        IFileOperationService rightToLeftOps,
        ILogger<TwoWayApplyService> logger)
    {
        _leftToRightOps = leftToRightOps;
        _rightToLeftOps = rightToLeftOps;
        _logger = logger;
    }

    public async Task<TwoWayApplyResult> ApplyAsync(
        TwoWayPreviewResult previewResult,
        string leftRoot,
        string rightRoot,
        ITwoWayStateStore stateStore,
        CancellationToken cancellationToken = default)
    {
        var result = new TwoWayApplyResult();

        foreach (var change in previewResult.Changes)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            switch (change.Kind)
            {
                case TwoWayChangeKind.NoChange:
                    break;

                case TwoWayChangeKind.LeftOnly:
                case TwoWayChangeKind.LeftChanged:
                    await CopyLeftToRightAsync(change, leftRoot, rightRoot, stateStore, result, cancellationToken);
                    break;

                case TwoWayChangeKind.RightOnly:
                case TwoWayChangeKind.RightChanged:
                    await CopyRightToLeftAsync(change, leftRoot, rightRoot, stateStore, result, cancellationToken);
                    break;

                case TwoWayChangeKind.BothChanged:
                case TwoWayChangeKind.Conflict:
                    result.SkippedConflicts++;
                    _logger.LogWarning(
                        "[TwoWaySafe] Conflict skipped for {Path}: {Summary}",
                        change.RelativePath, change.Summary);
                    break;

                case TwoWayChangeKind.DeleteOnLeft:
                case TwoWayChangeKind.DeleteOnRight:
                    result.SkippedDeletes++;
                    _logger.LogInformation(
                        "[TwoWaySafe] Delete skipped for {Path}: {Kind}",
                        change.RelativePath, change.Kind);
                    break;
            }
        }

        _logger.LogInformation(
            "[TwoWaySafe] Apply complete — L→R: {LR}, R→L: {RL}, conflicts: {C}, deletes skipped: {D}, failed: {F}",
            result.CopiedLeftToRight, result.CopiedRightToLeft,
            result.SkippedConflicts, result.SkippedDeletes, result.Failed);

        return result;
    }

    private async Task CopyLeftToRightAsync(
        TwoWayPreviewChange change,
        string leftRoot,
        string rightRoot,
        ITwoWayStateStore stateStore,
        TwoWayApplyResult result,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(leftRoot, change.RelativePath);
        var destPath = Path.Combine(rightRoot, change.RelativePath);

        try
        {
            await _leftToRightOps.EnsureDirectoryExistsAsync(Path.GetDirectoryName(destPath)!);
            await _leftToRightOps.CopyFileAsync(sourcePath, destPath, cancellationToken);
            result.CopiedLeftToRight++;

            stateStore.UpdateEntry(change.RelativePath, DateTimeOffset.UtcNow);

            _logger.LogInformation(
                "[TwoWaySafe] Copied L→R: {Path}", change.RelativePath);
        }
        catch (Exception ex)
        {
            result.Failed++;
            result.Errors.Add(new TwoWayApplyError
            {
                RelativePath = change.RelativePath,
                Message = $"L→R copy failed: {ex.Message}"
            });
            _logger.LogError(ex, "[TwoWaySafe] Failed to copy L→R: {Path}", change.RelativePath);
        }
    }

    private async Task CopyRightToLeftAsync(
        TwoWayPreviewChange change,
        string leftRoot,
        string rightRoot,
        ITwoWayStateStore stateStore,
        TwoWayApplyResult result,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(rightRoot, change.RelativePath);
        var destPath = Path.Combine(leftRoot, change.RelativePath);

        try
        {
            await _rightToLeftOps.EnsureDirectoryExistsAsync(Path.GetDirectoryName(destPath)!);
            await _rightToLeftOps.CopyFileAsync(sourcePath, destPath, cancellationToken);
            result.CopiedRightToLeft++;

            stateStore.UpdateEntry(change.RelativePath, DateTimeOffset.UtcNow);

            _logger.LogInformation(
                "[TwoWaySafe] Copied R→L: {Path}", change.RelativePath);
        }
        catch (Exception ex)
        {
            result.Failed++;
            result.Errors.Add(new TwoWayApplyError
            {
                RelativePath = change.RelativePath,
                Message = $"R→L copy failed: {ex.Message}"
            });
            _logger.LogError(ex, "[TwoWaySafe] Failed to copy R→L: {Path}", change.RelativePath);
        }
    }
}
