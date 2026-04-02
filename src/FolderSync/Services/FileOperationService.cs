using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IFileOperationService
{
    Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task RenameAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);
    Task DeleteOrArchiveAsync(string destinationPath, string relativePath, CancellationToken cancellationToken = default);
    Task EnsureDirectoryExistsAsync(string path);
}

public sealed class FileOperationService : IFileOperationService
{
    private readonly IRetryService _retry;
    private readonly SyncOptions _options;
    private readonly ILogger<FileOperationService> _logger;
    private readonly string _destinationRoot;

    public FileOperationService(
        IRetryService retry,
        IOptions<SyncOptions> options,
        ILogger<FileOperationService> logger)
    {
        _retry = retry;
        _options = options.Value;
        _logger = logger;
        _destinationRoot = NormalizePath(_options.DestinationPath);
    }

    public async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        ValidateManagedPath(destinationPath, _destinationRoot, nameof(destinationPath), allowRoot: false);

        if (_options.DryRun)
        {
            _logger.LogInformation("[DRY RUN] Would copy {Source} -> {Destination}", sourcePath, destinationPath);
            return;
        }

        await _retry.ExecuteAsync(async ct =>
        {
            await SafeFile.SafeCopyAsync(sourcePath, destinationPath, ct);
            _logger.LogInformation("Copied {Source} -> {Destination}", sourcePath, destinationPath);
        }, $"Copy {sourcePath}", cancellationToken);
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ValidateManagedPath(oldPath, _destinationRoot, nameof(oldPath), allowRoot: false);
        ValidateManagedPath(newPath, _destinationRoot, nameof(newPath), allowRoot: false);

        if (_options.DryRun)
        {
            _logger.LogInformation("[DRY RUN] Would rename {OldPath} -> {NewPath}", oldPath, newPath);
            return;
        }

        await _retry.ExecuteAsync(_ =>
        {
            SafeFile.EnsureDirectoryExists(newPath);
            File.Move(oldPath, newPath, overwrite: false);
            _logger.LogInformation("Renamed {OldPath} -> {NewPath}", oldPath, newPath);
            return Task.CompletedTask;
        }, $"Rename {oldPath}", cancellationToken);
    }

    public async Task DeleteOrArchiveAsync(string destinationPath, string relativePath, CancellationToken cancellationToken = default)
    {
        ValidateManagedPath(destinationPath, _destinationRoot, nameof(destinationPath), allowRoot: false);

        if (!_options.SyncDeletions)
        {
            _logger.LogDebug("Deletion sync disabled, skipping {Path}", destinationPath);
            return;
        }

        if (_options.DryRun)
        {
            _logger.LogInformation("[DRY RUN] Would {Action} {Path}",
                _options.DeleteMode == DeleteMode.Archive ? "archive" : "delete", destinationPath);
            return;
        }

        await _retry.ExecuteAsync(_ =>
        {
            if (_options.DeleteMode == DeleteMode.Archive)
            {
                var archiveRoot = !string.IsNullOrEmpty(_options.DeleteArchivePath)
                    ? _options.DeleteArchivePath
                    : Path.Combine(_options.DestinationPath, ".deleted");
                var normalizedArchiveRoot = NormalizePath(archiveRoot);
                ValidateArchiveRoot(normalizedArchiveRoot);

                var archivePath = SafeFile.GenerateArchivePath(destinationPath, archiveRoot, relativePath);
                ValidateManagedPath(archivePath, normalizedArchiveRoot, nameof(archivePath), allowRoot: false);
                SafeFile.EnsureDirectoryExists(archivePath);

                if (Directory.Exists(destinationPath) && !File.Exists(destinationPath))
                {
                    Directory.Move(destinationPath, archivePath);
                }
                else
                {
                    File.Move(destinationPath, archivePath, overwrite: false);
                }

                _logger.LogInformation("Archived {Path} -> {ArchivePath}", destinationPath, archivePath);
            }
            else
            {
                if (Directory.Exists(destinationPath) && !File.Exists(destinationPath))
                {
                    Directory.Delete(destinationPath, recursive: true);
                }
                else
                {
                    File.Delete(destinationPath);
                }

                _logger.LogInformation("Deleted {Path}", destinationPath);
            }

            return Task.CompletedTask;
        }, $"DeleteOrArchive {destinationPath}", cancellationToken);
    }

    public Task EnsureDirectoryExistsAsync(string path)
    {
        ValidateManagedPath(path, _destinationRoot, nameof(path), allowRoot: true);

        if (_options.DryRun)
        {
            _logger.LogInformation("[DRY RUN] Would ensure directory {Path}", path);
            return Task.CompletedTask;
        }

        SafeFile.EnsureDirectoryExists(path + Path.DirectorySeparatorChar);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            _logger.LogDebug("Created directory {Path}", path);
        }

        return Task.CompletedTask;
    }

    private static void ValidateManagedPath(string path, string root, string paramName, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", paramName);

        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(root);

        if (IsProtectedRoot(normalizedPath))
            throw new InvalidOperationException($"Refusing to operate on protected root path '{normalizedPath}'.");

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (allowRoot)
                return;

            throw new InvalidOperationException($"Refusing to operate on managed root '{normalizedPath}'.");
        }

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{normalizedPath}' is outside the managed root '{normalizedRoot}'.");
        }
    }

    private static void ValidateArchiveRoot(string archiveRoot)
    {
        if (IsProtectedRoot(archiveRoot))
            throw new InvalidOperationException($"Archive root '{archiveRoot}' cannot be a drive or share root.");
    }

    private static bool IsProtectedRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;

        return string.Equals(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
