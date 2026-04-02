using System.Text.Json;
using FolderSync.Infrastructure;
using FolderSync.Models;

namespace FolderSync.Services;

public interface IRuntimeControlStore
{
    string ControlPath { get; }
    RuntimeControlSnapshot Read();
    void SetPaused(bool paused, string? reason = null);
    void SetProfilePaused(string profileName, bool paused, string? reason = null);
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
        var snapshot = Read();
        snapshot.IsPaused = paused;
        snapshot.Reason = paused ? reason : null;
        snapshot.ChangedAtUtc = _clock.UtcNow;
        Persist(snapshot);
    }

    public void SetProfilePaused(string profileName, bool paused, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));

        var snapshot = Read();
        var profile = snapshot.Profiles.FirstOrDefault(existing =>
            string.Equals(existing.Name, profileName, StringComparison.OrdinalIgnoreCase));

        if (paused)
        {
            profile ??= CreateProfile(profileName, snapshot);
            profile.IsPaused = true;
            profile.Reason = reason;
            profile.ChangedAtUtc = _clock.UtcNow;
        }
        else if (profile is not null)
        {
            snapshot.Profiles.Remove(profile);
        }

        Persist(snapshot);
    }

    private static ProfileRuntimeControlSnapshot CreateProfile(string profileName, RuntimeControlSnapshot snapshot)
    {
        var profile = new ProfileRuntimeControlSnapshot
        {
            Name = profileName,
            IsPaused = false
        };
        snapshot.Profiles.Add(profile);
        return profile;
    }

    private void Persist(RuntimeControlSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(ControlPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = ControlPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(tempPath, ControlPath, overwrite: true);
    }
}
