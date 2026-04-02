using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IFileComparisonService
{
    Task<FileComparisonResult> CompareAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default);
}

public sealed class FileComparisonService : IFileComparisonService
{
    private readonly IFileHasher _hasher;
    private readonly SyncOptions _options;
    private readonly ILogger<FileComparisonService> _logger;

    public FileComparisonService(
        IFileHasher hasher,
        IOptions<SyncOptions> options,
        ILogger<FileComparisonService> logger)
    {
        _hasher = hasher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FileComparisonResult> CompareAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(destinationPath))
            return FileComparisonResult.MissingDestination;

        var sourceIsDir = Directory.Exists(sourcePath) && !File.Exists(sourcePath);
        var destIsDir = Directory.Exists(destinationPath) && !File.Exists(destinationPath);

        if (sourceIsDir != destIsDir)
            return FileComparisonResult.TypeMismatch;

        if (sourceIsDir)
            return FileComparisonResult.Same;

        var sourceInfo = new FileInfo(sourcePath);
        var destInfo = new FileInfo(destinationPath);

        // Fast path: different sizes means different content
        if (sourceInfo.Length != destInfo.Length)
        {
            _logger.LogDebug(
                "Size mismatch for {Path}: source={SourceSize}, dest={DestSize}",
                sourcePath, sourceInfo.Length, destInfo.Length);
            return FileComparisonResult.DifferentContent;
        }

        // Check timestamps within drift tolerance
        var timeDiff = Math.Abs((sourceInfo.LastWriteTimeUtc - destInfo.LastWriteTimeUtc).TotalSeconds);
        if (timeDiff <= _options.IgnoreLastWriteTimeDriftSeconds)
        {
            return FileComparisonResult.Same;
        }

        // Size matches but timestamps differ — use hash if configured
        if (_options.UseHashComparison)
        {
            var sourceHash = await _hasher.ComputeHashAsync(sourcePath, cancellationToken);
            var destHash = await _hasher.ComputeHashAsync(destinationPath, cancellationToken);

            if (string.Equals(sourceHash, destHash, StringComparison.Ordinal))
            {
                _logger.LogDebug("Hash match for {Path} despite timestamp difference", sourcePath);
                return FileComparisonResult.DifferentMetadataOnly;
            }

            return FileComparisonResult.DifferentContent;
        }

        // No hash comparison — timestamps differ so assume different
        return FileComparisonResult.DifferentContent;
    }
}
