using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Tests;

public sealed class TwoWayPreviewClassifierTests
{
    private readonly ITwoWayPreviewClassifier _classifier = new TwoWayPreviewClassifier();

    [Fact]
    public void Classify_WithoutHistory_ProducesConflictForDifferentBothSides()
    {
        var detectedAt = new DateTimeOffset(2026, 4, 4, 10, 0, 0, TimeSpan.Zero);
        var result = _classifier.Classify(
        [
            new TwoWayObservedEntry
            {
                RelativePath = "docs/file.txt",
                Left = new FileFingerprint(10, detectedAt, "left"),
                Right = new FileFingerprint(10, detectedAt, "right")
            }
        ],
        new TwoWayStateSnapshot(),
        detectedAt);

        var change = Assert.Single(result.Changes);
        Assert.Equal(TwoWayChangeKind.Conflict, change.Kind);
        Assert.Single(result.Conflicts);
    }

    [Fact]
    public void Classify_WithHistory_DetectsLeftOnlyChange()
    {
        var previousTime = new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero);
        var detectedAt = previousTime.AddMinutes(10);
        var result = _classifier.Classify(
        [
            new TwoWayObservedEntry
            {
                RelativePath = "docs/file.txt",
                Left = new FileFingerprint(10, detectedAt, "left-new"),
                Right = new FileFingerprint(10, previousTime, "shared")
            }
        ],
        new TwoWayStateSnapshot
        {
            Entries =
            [
                new TwoWayStateEntry
                {
                    RelativePath = "docs/file.txt",
                    LeftHash = "shared",
                    RightHash = "shared",
                    LastSeenLeftUtc = previousTime,
                    LastSeenRightUtc = previousTime
                }
            ]
        },
        detectedAt);

        var change = Assert.Single(result.Changes);
        Assert.Equal(TwoWayChangeKind.LeftChanged, change.Kind);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Classify_WithHistory_DetectsBothChangedConflict()
    {
        var previousTime = new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero);
        var detectedAt = previousTime.AddMinutes(10);
        var result = _classifier.Classify(
        [
            new TwoWayObservedEntry
            {
                RelativePath = "docs/file.txt",
                Left = new FileFingerprint(10, detectedAt, "left-new"),
                Right = new FileFingerprint(10, detectedAt, "right-new")
            }
        ],
        new TwoWayStateSnapshot
        {
            Entries =
            [
                new TwoWayStateEntry
                {
                    RelativePath = "docs/file.txt",
                    LeftHash = "shared",
                    RightHash = "shared",
                    LastSeenLeftUtc = previousTime,
                    LastSeenRightUtc = previousTime
                }
            ]
        },
        detectedAt);

        var change = Assert.Single(result.Changes);
        Assert.Equal(TwoWayChangeKind.BothChanged, change.Kind);
        Assert.Single(result.Conflicts);
        Assert.Equal("docs/file.txt", result.Conflicts[0].RelativePath);
    }
}
