using FolderSync.Models;

namespace FolderSync.Services;

public interface ITwoWayPreviewClassifier
{
    TwoWayPreviewResult Classify(IEnumerable<TwoWayObservedEntry> observedEntries, TwoWayStateSnapshot stateSnapshot, DateTimeOffset detectedAtUtc);
}

public sealed class TwoWayPreviewClassifier : ITwoWayPreviewClassifier
{
    public TwoWayPreviewResult Classify(IEnumerable<TwoWayObservedEntry> observedEntries, TwoWayStateSnapshot stateSnapshot, DateTimeOffset detectedAtUtc)
    {
        var result = new TwoWayPreviewResult();
        var prior = stateSnapshot.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var observed in observedEntries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            prior.TryGetValue(observed.RelativePath, out var previous);
            var change = ClassifyEntry(observed, previous, detectedAtUtc);
            result.Changes.Add(change);

            if (change.Kind == TwoWayChangeKind.Conflict || change.Kind == TwoWayChangeKind.BothChanged)
            {
                result.Conflicts.Add(new TwoWayConflictRecord
                {
                    RelativePath = observed.RelativePath,
                    Reason = change.Summary,
                    DetectedAtUtc = detectedAtUtc,
                    RecommendedMode = TwoWayConflictMode.Manual
                });
            }
        }

        return result;
    }

    private static TwoWayPreviewChange ClassifyEntry(TwoWayObservedEntry observed, TwoWayStateEntry? previous, DateTimeOffset detectedAtUtc)
    {
        var left = observed.Left;
        var right = observed.Right;

        if (left is null && right is null)
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.NoChange,
                Summary = "Not present on either side"
            };
        }

        if (previous is null)
        {
            return ClassifyWithoutHistory(observed);
        }

        var leftChanged = HasChanged(left, previous.LeftHash, previous.LastSeenLeftUtc);
        var rightChanged = HasChanged(right, previous.RightHash, previous.LastSeenRightUtc);

        if (!leftChanged && !rightChanged)
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.NoChange,
                Summary = "Matches the last known synchronized state"
            };
        }

        if (left is null && rightChanged)
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.DeleteOnLeft,
                Summary = "Deleted on left while the right side changed or still exists"
            };
        }

        if (right is null && leftChanged)
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.DeleteOnRight,
                Summary = "Deleted on right while the left side changed or still exists"
            };
        }

        if (leftChanged && rightChanged)
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.BothChanged,
                Summary = "Changed on both sides since the last known state"
            };
        }

        if (leftChanged)
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.LeftChanged,
                Summary = "Changed on left only"
            };
        }

        return new TwoWayPreviewChange
        {
            RelativePath = observed.RelativePath,
            Kind = TwoWayChangeKind.RightChanged,
            Summary = "Changed on right only"
        };
    }

    private static TwoWayPreviewChange ClassifyWithoutHistory(TwoWayObservedEntry observed)
    {
        if (observed.Left is not null && observed.Right is null)
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.LeftOnly,
                Summary = "Exists on left only"
            };
        }

        if (observed.Left is null && observed.Right is not null)
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.RightOnly,
                Summary = "Exists on right only"
            };
        }

        if (FingerprintsMatch(observed.Left, observed.Right))
        {
            return new TwoWayPreviewChange
            {
                RelativePath = observed.RelativePath,
                Kind = TwoWayChangeKind.NoChange,
                Summary = "Already identical on both sides"
            };
        }

        return new TwoWayPreviewChange
        {
            RelativePath = observed.RelativePath,
            Kind = TwoWayChangeKind.Conflict,
            Summary = "Different on both sides without prior sync state"
        };
    }

    private static bool HasChanged(FileFingerprint? current, string? previousHash, DateTimeOffset? previousTimestampUtc)
    {
        if (current is null)
            return previousHash is not null || previousTimestampUtc is not null;

        if (!string.IsNullOrWhiteSpace(previousHash) && !string.IsNullOrWhiteSpace(current.ContentHash))
            return !string.Equals(current.ContentHash, previousHash, StringComparison.OrdinalIgnoreCase);

        return previousTimestampUtc is null || current.LastWriteTimeUtc != previousTimestampUtc.Value;
    }

    private static bool FingerprintsMatch(FileFingerprint? left, FileFingerprint? right)
    {
        if (left is null || right is null)
            return false;

        if (!string.IsNullOrWhiteSpace(left.ContentHash) && !string.IsNullOrWhiteSpace(right.ContentHash))
            return string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase);

        return left.Length == right.Length && left.LastWriteTimeUtc == right.LastWriteTimeUtc;
    }
}
