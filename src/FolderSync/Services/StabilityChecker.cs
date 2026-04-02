using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IStabilityChecker
{
    Task<bool> WaitForFileReadyAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class StabilityChecker : IStabilityChecker
{
    private readonly StabilityCheckOptions _options;
    private readonly ILogger<StabilityChecker> _logger;

    public StabilityChecker(IOptions<SyncOptions> options, ILogger<StabilityChecker> logger)
    {
        _options = options.Value.StabilityCheck;
        _logger = logger;
    }

    public async Task<bool> WaitForFileReadyAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return true;

        var deadline = DateTime.UtcNow.AddMilliseconds(_options.MaxWaitMilliseconds);
        var stableCount = 0;
        long lastLength = -1;
        DateTime lastWriteTime = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                _logger.LogDebug("Stability check: file no longer exists {Path}", path);
                return false;
            }

            try
            {
                var info = new FileInfo(path);
                var currentLength = info.Length;
                var currentWriteTime = info.LastWriteTimeUtc;

                if (currentLength == lastLength && currentWriteTime == lastWriteTime)
                {
                    // Try to open for exclusive read to confirm no writers
                    if (TryOpenForRead(path))
                    {
                        stableCount++;
                        _logger.LogDebug(
                            "Stability check: {Path} stable observation {Count}/{Required}",
                            path, stableCount, _options.RequiredStableObservations);

                        if (stableCount >= _options.RequiredStableObservations)
                            return true;
                    }
                    else
                    {
                        _logger.LogDebug("Stability check: {Path} locked, resetting count", path);
                        stableCount = 0;
                    }
                }
                else
                {
                    stableCount = 0;
                    lastLength = currentLength;
                    lastWriteTime = currentWriteTime;
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Stability check: IO error for {Path}", path);
                stableCount = 0;
            }

            await Task.Delay(_options.PollingIntervalMilliseconds, cancellationToken);
        }

        _logger.LogWarning("Stability check: timed out waiting for {Path}", path);
        return false;
    }

    private static bool TryOpenForRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
