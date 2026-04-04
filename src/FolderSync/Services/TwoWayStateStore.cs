using System.Text.Json;
using FolderSync.Models;

namespace FolderSync.Services;

public interface ITwoWayStateStore
{
    TwoWayStateSnapshot Load();
    void Save(TwoWayStateSnapshot snapshot);
    void ApplyPreviewResult(TwoWayPreviewResult result, DateTimeOffset updatedAtUtc);
    bool AcknowledgeConflict(string relativePath, DateTimeOffset acknowledgedAtUtc);
}

public sealed class JsonTwoWayStateStore(string path) : ITwoWayStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public TwoWayStateSnapshot Load()
    {
        if (!File.Exists(path))
            return new TwoWayStateSnapshot();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<TwoWayStateSnapshot>(stream, JsonOptions) ?? new TwoWayStateSnapshot();
    }

    public void Save(TwoWayStateSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    public void ApplyPreviewResult(TwoWayPreviewResult result, DateTimeOffset updatedAtUtc)
    {
        var snapshot = Load();
        var existingConflicts = snapshot.Conflicts.ToDictionary(
            conflict => GetConflictKey(conflict.RelativePath, conflict.Reason),
            conflict => conflict,
            StringComparer.OrdinalIgnoreCase);

        snapshot.UpdatedAtUtc = updatedAtUtc;
        snapshot.Conflicts = result.Conflicts
            .Select(conflict =>
            {
                if (existingConflicts.TryGetValue(GetConflictKey(conflict.RelativePath, conflict.Reason), out var existing))
                {
                    conflict.IsAcknowledged = existing.IsAcknowledged;
                    conflict.AcknowledgedAtUtc = existing.AcknowledgedAtUtc;
                }

                return conflict;
            })
            .OrderBy(conflict => conflict.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nextEntries = new List<TwoWayStateEntry>();
        foreach (var change in result.Changes)
        {
            var existing = snapshot.Entries.FirstOrDefault(entry =>
                string.Equals(entry.RelativePath, change.RelativePath, StringComparison.OrdinalIgnoreCase));

            nextEntries.Add(existing ?? new TwoWayStateEntry
            {
                RelativePath = change.RelativePath,
                ConflictState = change.Kind is TwoWayChangeKind.Conflict or TwoWayChangeKind.BothChanged
                    ? "PreviewConflict"
                    : null
            });
        }

        snapshot.Entries = nextEntries
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Save(snapshot);
    }

    public bool AcknowledgeConflict(string relativePath, DateTimeOffset acknowledgedAtUtc)
    {
        var snapshot = Load();
        var conflict = snapshot.Conflicts.FirstOrDefault(item =>
            string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (conflict is null)
            return false;

        conflict.IsAcknowledged = true;
        conflict.AcknowledgedAtUtc = acknowledgedAtUtc;
        snapshot.UpdatedAtUtc = acknowledgedAtUtc;
        Save(snapshot);
        return true;
    }

    private static string GetConflictKey(string relativePath, string reason)
    {
        return $"{relativePath}|{reason}";
    }
}
