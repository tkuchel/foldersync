using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSync.Tests;

public sealed class FileOperationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _destinationRoot;

    public FileOperationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-fileops-{Guid.NewGuid():N}");
        _destinationRoot = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(_destinationRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task CopyFileAsync_ThrowsWhenDestinationEscapesManagedRoot()
    {
        var source = Path.Combine(_tempDir, "source.txt");
        File.WriteAllText(source, "content");

        var service = CreateService();
        var escapedDestination = Path.Combine(_tempDir, "dest2", "file.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CopyFileAsync(source, escapedDestination));
    }

    [Fact]
    public async Task DeleteOrArchiveAsync_ThrowsWhenTargetIsManagedRoot()
    {
        var service = CreateService(syncDeletions: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteOrArchiveAsync(_destinationRoot, "file.txt"));
    }

    [Fact]
    public async Task DeleteOrArchiveAsync_ThrowsWhenArchiveRootIsDriveRoot()
    {
        var target = Path.Combine(_destinationRoot, "file.txt");
        File.WriteAllText(target, "content");

        var root = Path.GetPathRoot(_destinationRoot)!;
        var service = CreateService(syncDeletions: true, configure: o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.DeleteArchivePath = root;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteOrArchiveAsync(target, "file.txt"));
    }

    private FileOperationService CreateService(
        bool syncDeletions = false,
        Action<SyncOptions>? configure = null)
    {
        var options = TestOptions.Create(
            destinationPath: _destinationRoot,
            configure: o =>
            {
                o.SyncDeletions = syncDeletions;
                configure?.Invoke(o);
            });

        return new FileOperationService(
            new ImmediateRetryService(),
            options,
            NullLogger<FileOperationService>.Instance);
    }

    private sealed class ImmediateRetryService : IRetryService
    {
        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> action,
            string operationName,
            CancellationToken cancellationToken = default)
        {
            return action(cancellationToken);
        }

        public async Task ExecuteAsync(
            Func<CancellationToken, Task> action,
            string operationName,
            CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
        }
    }
}
