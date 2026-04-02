using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSync.Tests;

public sealed class FileOperationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;

    public FileOperationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-fileops-{Guid.NewGuid():N}");
        _sourceRoot = Path.Combine(_tempDir, "source");
        _destinationRoot = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(_sourceRoot);
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
        var testToken = TestContext.Current.CancellationToken;
        var source = Path.Combine(_tempDir, "source.txt");
        File.WriteAllText(source, "content");

        var service = CreateService();
        var escapedDestination = Path.Combine(_tempDir, "dest2", "file.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CopyFileAsync(source, escapedDestination, testToken));
    }

    [Fact]
    public async Task DeleteOrArchiveAsync_ThrowsWhenTargetIsManagedRoot()
    {
        var testToken = TestContext.Current.CancellationToken;
        var service = CreateService(syncDeletions: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteOrArchiveAsync(_destinationRoot, "file.txt", testToken));
    }

    [Fact]
    public async Task DeleteOrArchiveAsync_ThrowsWhenArchiveRootIsDriveRoot()
    {
        var testToken = TestContext.Current.CancellationToken;
        var target = Path.Combine(_destinationRoot, "file.txt");
        File.WriteAllText(target, "content");

        var root = Path.GetPathRoot(_destinationRoot)!;
        var service = CreateService(syncDeletions: true, configure: o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.DeleteArchivePath = root;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteOrArchiveAsync(target, "file.txt", testToken));
    }

    [Fact]
    public async Task CopyFileAsync_CopiesFileAndPreservesLastWriteTime()
    {
        var testToken = TestContext.Current.CancellationToken;
        var source = Path.Combine(_sourceRoot, "nested", "source.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "copied content", testToken);
        var expectedTimestamp = new DateTime(2026, 4, 2, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, expectedTimestamp);

        var destination = Path.Combine(_destinationRoot, "nested", "source.txt");
        var service = CreateService();

        await service.CopyFileAsync(source, destination, testToken);

        Assert.True(File.Exists(destination));
        Assert.Equal("copied content", await File.ReadAllTextAsync(destination, testToken));
        Assert.Equal(expectedTimestamp, File.GetLastWriteTimeUtc(destination));
    }

    [Fact]
    public async Task DeleteOrArchiveAsync_ArchivesFileUnderDeletedFolder()
    {
        var testToken = TestContext.Current.CancellationToken;
        var target = Path.Combine(_destinationRoot, "docs", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "to archive", testToken);

        var service = CreateService(syncDeletions: true, configure: o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.DeleteArchivePath = string.Empty;
        });

        await service.DeleteOrArchiveAsync(target, Path.Combine("docs", "report.txt"), testToken);

        Assert.False(File.Exists(target));

        var archiveDir = Path.Combine(_destinationRoot, ".deleted", "docs");
        Assert.True(Directory.Exists(archiveDir));

        var archivedFile = Directory.GetFiles(archiveDir, "report (deleted *).txt").Single();
        Assert.Equal("to archive", await File.ReadAllTextAsync(archivedFile, testToken));
    }

    private FileOperationService CreateService(
        bool syncDeletions = false,
        Action<SyncOptions>? configure = null)
    {
        var options = TestOptions.Create(
            sourcePath: _sourceRoot,
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
