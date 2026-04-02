using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSync.Tests;

public sealed class SyncProcessorIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;

    public SyncProcessorIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-syncproc-{Guid.NewGuid():N}");
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
    public async Task ProcessAsync_CreatedFile_CopiesIntoDestination()
    {
        var sourcePath = Path.Combine(_sourceRoot, "notes", "todo.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "integration content", TestContext.Current.CancellationToken);

        var options = TestOptions.Create(
            _sourceRoot,
            _destinationRoot,
            o =>
            {
                o.StabilityCheck.Enabled = false;
                o.UseHashComparison = false;
            });

        var processor = CreateProcessor(options);
        var destinationPath = Path.Combine(_destinationRoot, "notes", "todo.txt");

        var result = await processor.ProcessAsync(
            new SyncWorkItem
            {
                Kind = WatcherChangeKind.Created,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                IsDirectory = false
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(File.Exists(destinationPath));
        Assert.Equal("integration content", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
    }

    private SyncProcessor CreateProcessor(Microsoft.Extensions.Options.IOptions<SyncOptions> options)
    {
        var hasher = new Sha256FileHasher();
        var stabilityChecker = new StabilityChecker(options, NullLogger<StabilityChecker>.Instance);
        var comparisonService = new FileComparisonService(hasher, options, NullLogger<FileComparisonService>.Instance);
        var conflictResolver = new ConflictResolver(options, NullLogger<ConflictResolver>.Instance);
        var retryService = new RetryService(options, NullLogger<RetryService>.Instance);
        var fileOperations = new FileOperationService(retryService, options, NullLogger<FileOperationService>.Instance);
        var pathMapping = new PathMappingService(options);
        var pathSafety = new PathSafetyService();

        return new SyncProcessor(
            stabilityChecker,
            comparisonService,
            conflictResolver,
            fileOperations,
            pathMapping,
            pathSafety,
            NullLogger<SyncProcessor>.Instance);
    }
}
