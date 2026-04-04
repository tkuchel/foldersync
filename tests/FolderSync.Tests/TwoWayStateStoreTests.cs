using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Tests;

public sealed class TwoWayStateStoreTests : IDisposable
{
    private readonly string _tempDir;

    public TwoWayStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-twoway-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void JsonStateStore_RoundTripsEntries()
    {
        var path = Path.Combine(_tempDir, "twoway-state.json");
        ITwoWayStateStore store = new JsonTwoWayStateStore(path);

        var snapshot = new TwoWayStateSnapshot
        {
            Entries =
            [
                new TwoWayStateEntry
                {
                    RelativePath = "docs/file.txt",
                    LeftHash = "left",
                    RightHash = "right",
                    ConflictState = "ManualReview"
                }
            ]
        };

        store.Save(snapshot);
        var loaded = store.Load();

        var entry = Assert.Single(loaded.Entries);
        Assert.Equal("docs/file.txt", entry.RelativePath);
        Assert.Equal("left", entry.LeftHash);
        Assert.Equal("right", entry.RightHash);
        Assert.Equal("ManualReview", entry.ConflictState);
    }

    [Fact]
    public void JsonStateStore_AppliesPreviewResultAndPersistsConflicts()
    {
        var path = Path.Combine(_tempDir, "twoway-state.json");
        ITwoWayStateStore store = new JsonTwoWayStateStore(path);
        var updatedAt = new DateTimeOffset(2026, 4, 4, 11, 0, 0, TimeSpan.Zero);

        store.ApplyPreviewResult(
            new TwoWayPreviewResult
            {
                Changes =
                [
                    new TwoWayPreviewChange
                    {
                        RelativePath = "docs/file.txt",
                        Kind = TwoWayChangeKind.BothChanged,
                        Summary = "Changed on both sides since the last known state"
                    }
                ],
                Conflicts =
                [
                    new TwoWayConflictRecord
                    {
                        RelativePath = "docs/file.txt",
                        Reason = "Changed on both sides since the last known state",
                        DetectedAtUtc = updatedAt
                    }
                ]
            },
            updatedAt);

        var loaded = store.Load();
        Assert.Equal(updatedAt, loaded.UpdatedAtUtc);
        Assert.Single(loaded.Entries);
        Assert.Single(loaded.Conflicts);
        Assert.Equal("docs/file.txt", loaded.Conflicts[0].RelativePath);
    }
}
