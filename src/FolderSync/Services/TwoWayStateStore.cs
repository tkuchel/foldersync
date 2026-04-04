using System.Text.Json;
using FolderSync.Models;

namespace FolderSync.Services;

public interface ITwoWayStateStore
{
    TwoWayStateSnapshot Load();
    void Save(TwoWayStateSnapshot snapshot);
    void ApplyPreviewResult(TwoWayPreviewResult result, DateTimeOffset updatedAtUtc);
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
        snapshot.UpdatedAtUtc = updatedAtUtc;
        snapshot.Conflicts = result.Conflicts
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
}
