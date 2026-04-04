using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Tests;

public sealed class TwoWayPreviewServiceTests : IDisposable
{
    private readonly string _tempDir;

    public TwoWayPreviewServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task RunAsync_PersistsPreviewStateAndConflictCount()
    {
        var source = Directory.CreateDirectory(Path.Combine(_tempDir, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_tempDir, "destination")).FullName;
        File.WriteAllText(Path.Combine(source, "file.txt"), "left");
        File.WriteAllText(Path.Combine(destination, "file.txt"), "right");
        var stateStorePath = Path.Combine(_tempDir, "state", "alpha.twoway.json");

        ITwoWayPreviewService service = new TwoWayPreviewService(
            new Sha256FileHasher(),
            new PathSafetyService(),
            new TwoWayPreviewClassifier());

        var result = await service.RunAsync(
            "alpha",
            new SyncOptions
            {
                SourcePath = source,
                DestinationPath = destination,
                TwoWay = new TwoWayOptions
                {
                    RequireHashComparison = true
                }
            },
            stateStorePath,
            TestContext.Current.CancellationToken);

        Assert.Equal("alpha", result.ProfileName);
        Assert.True(File.Exists(stateStorePath));
        Assert.Equal(1, result.ChangeCount);
        Assert.Equal(1, result.ConflictCount);

        var stored = new JsonTwoWayStateStore(stateStorePath).Load();
        Assert.Single(stored.Conflicts);
        Assert.Equal("file.txt", stored.Conflicts[0].RelativePath);
    }
}
