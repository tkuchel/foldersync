using System.Security.Cryptography;
using System.Text;
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
    void EnqueueReconcileRequest(string profileName, string trigger = "Control");
    void EnqueueReconcileRequests(IEnumerable<string> profileNames, string trigger = "Control");
    ReconcileRequestSnapshot? TryDequeueReconcileRequest(string profileName);
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
        return WithControlLock(ReadUnlocked);
    }

    public void SetPaused(bool paused, string? reason = null)
    {
        WithControlLock(() =>
        {
            var snapshot = ReadUnlocked();
            snapshot.IsPaused = paused;
            snapshot.Reason = paused ? reason : null;
            snapshot.ChangedAtUtc = _clock.UtcNow;
            PersistUnlocked(snapshot);
            return 0;
        });
    }

    public void SetProfilePaused(string profileName, bool paused, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));

        WithControlLock(() =>
        {
            var snapshot = ReadUnlocked();
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

            PersistUnlocked(snapshot);
            return 0;
        });
    }

    public void EnqueueReconcileRequest(string profileName, string trigger = "Control")
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));

        WithControlLock(() =>
        {
            var snapshot = ReadUnlocked();
            snapshot.ReconcileRequests.Add(new ReconcileRequestSnapshot
            {
                Id = Guid.NewGuid().ToString("N"),
                ProfileName = profileName,
                Trigger = string.IsNullOrWhiteSpace(trigger) ? "Control" : trigger,
                RequestedAtUtc = _clock.UtcNow
            });
            PersistUnlocked(snapshot);
            return 0;
        });
    }

    public void EnqueueReconcileRequests(IEnumerable<string> profileNames, string trigger = "Control")
    {
        ArgumentNullException.ThrowIfNull(profileNames);

        var names = profileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
            return;

        WithControlLock(() =>
        {
            var snapshot = ReadUnlocked();
            var requestedAtUtc = _clock.UtcNow;
            foreach (var profileName in names)
            {
                snapshot.ReconcileRequests.Add(new ReconcileRequestSnapshot
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ProfileName = profileName,
                    Trigger = string.IsNullOrWhiteSpace(trigger) ? "Control" : trigger,
                    RequestedAtUtc = requestedAtUtc
                });
            }

            PersistUnlocked(snapshot);
            return 0;
        });
    }

    public ReconcileRequestSnapshot? TryDequeueReconcileRequest(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));

        return WithControlLock(() =>
        {
            var snapshot = ReadUnlocked();
            var request = snapshot.ReconcileRequests
                .OrderBy(item => item.RequestedAtUtc)
                .FirstOrDefault(item => string.Equals(item.ProfileName, profileName, StringComparison.OrdinalIgnoreCase));

            if (request is null)
                return null;

            snapshot.ReconcileRequests.RemoveAll(item => string.Equals(item.Id, request.Id, StringComparison.Ordinal));
            PersistUnlocked(snapshot);
            return request;
        });
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

    private RuntimeControlSnapshot ReadUnlocked()
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

    private void PersistUnlocked(RuntimeControlSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(ControlPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = ControlPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(tempPath, ControlPath, overwrite: true);
    }

    private T WithControlLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, BuildMutexName(ControlPath));
        mutex.WaitOne();
        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static string BuildMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return $@"Global\FolderSync-Control-{hash}";
    }
}
