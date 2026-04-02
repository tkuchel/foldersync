using System.Text.Json;
using FolderSync.Infrastructure;
using FolderSync.Models;

namespace FolderSync.Services;

public interface IRuntimeControlStore
{
    string ControlPath { get; }
    RuntimeControlSnapshot Read();
    void SetPaused(bool paused, string? reason = null);
}

public sealed class RuntimeControlStore : IRuntimeControlStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IClock _clock;

    public RuntimeControlStore(IClock clock)
        : this(Path.Combine(Environment.CurrentDirectory, "foldersync-control.json"), clock)
    {
    }

    internal RuntimeControlStore(string controlPath, IClock clock)
    {
        ControlPath = controlPath;
        _clock = clock;
    }

    public string ControlPath { get; }

    public RuntimeControlSnapshot Read()
    {
        try
        {
            if (!File.Exists(ControlPath))
                return new RuntimeControlSnapshot();

            using var stream = new FileStream(ControlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<RuntimeControlSnapshot>(stream) ?? new RuntimeControlSnapshot();
        }
        catch
        {
            return new RuntimeControlSnapshot();
        }
    }

    public void SetPaused(bool paused, string? reason = null)
    {
        var snapshot = new RuntimeControlSnapshot
        {
            IsPaused = paused,
            Reason = paused ? reason : null,
            ChangedAtUtc = _clock.UtcNow
        };

        var directory = Path.GetDirectoryName(ControlPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = ControlPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(tempPath, ControlPath, overwrite: true);
    }
}
