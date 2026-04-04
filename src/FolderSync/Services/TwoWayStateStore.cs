using System.Text.Json;
using FolderSync.Models;

namespace FolderSync.Services;

public interface ITwoWayStateStore
{
    TwoWayStateSnapshot Load();
    void Save(TwoWayStateSnapshot snapshot);
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
}
