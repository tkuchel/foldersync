using System.Text.Json;
using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;

namespace FolderSync.Services;

public interface IRuntimeHealthStore
{
    string SnapshotPath { get; }
    void Initialize(IEnumerable<string> profileNames);
    void RecordServiceStarted();
    void RecordServiceStopped();
    void RecordServiceError(string message);
    void RecordProfileState(string profileName, string state);
    void RecordWatcherOverflow(string profileName);
    void RecordSyncResult(string profileName, SyncResult result);
    void RecordReconciliationStarted(string profileName, string trigger);
    void RecordReconciliationCompleted(string profileName, string trigger, RobocopyResult result, TimeSpan duration);
}

public sealed class RuntimeHealthStore : IRuntimeHealthStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly ILogger<RuntimeHealthStore> _logger;
    private RuntimeHealthSnapshot _snapshot;

    public RuntimeHealthStore(IClock clock, ILogger<RuntimeHealthStore> logger)
        : this(Path.Combine(Environment.CurrentDirectory, "foldersync-health.json"), clock, logger)
    {
    }

    internal RuntimeHealthStore(string snapshotPath, IClock clock, ILogger<RuntimeHealthStore> logger)
    {
        SnapshotPath = snapshotPath;
        _clock = clock;
        _logger = logger;
        _snapshot = new RuntimeHealthSnapshot
        {
            ServiceName = Commands.HostBuilderHelper.DefaultServiceName,
            ServiceState = "Starting",
            StartedAtUtc = _clock.UtcNow,
            UpdatedAtUtc = _clock.UtcNow
        };
    }

    public string SnapshotPath { get; }

    public void Initialize(IEnumerable<string> profileNames)
    {
        lock (_gate)
        {
            _snapshot = new RuntimeHealthSnapshot
            {
                ServiceName = Commands.HostBuilderHelper.DefaultServiceName,
                ServiceState = "Starting",
                StartedAtUtc = _clock.UtcNow,
                UpdatedAtUtc = _clock.UtcNow,
                Profiles = profileNames
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Select(name => new ProfileHealthSnapshot { Name = name })
                    .ToList()
            };

            PersistLocked();
        }
    }

    public void RecordServiceStarted()
    {
        lock (_gate)
        {
            _snapshot.ServiceState = "Running";
            _snapshot.LastError = null;
            PersistLocked();
        }
    }

    public void RecordServiceStopped()
    {
        lock (_gate)
        {
            _snapshot.ServiceState = "Stopped";
            foreach (var profile in _snapshot.Profiles)
                profile.State = "Stopped";
            PersistLocked();
        }
    }

    public void RecordServiceError(string message)
    {
        lock (_gate)
        {
            _snapshot.ServiceState = "Error";
            _snapshot.LastError = message;
            PersistLocked();
        }
    }

    public void RecordProfileState(string profileName, string state)
    {
        lock (_gate)
        {
            GetProfile(profileName).State = state;
            PersistLocked();
        }
    }

    public void RecordWatcherOverflow(string profileName)
    {
        lock (_gate)
        {
            GetProfile(profileName).WatcherOverflowCount++;
            PersistLocked();
        }
    }

    public void RecordSyncResult(string profileName, SyncResult result)
    {
        lock (_gate)
        {
            var profile = GetProfile(profileName);
            profile.ProcessedCount++;

            if (result.IsSkipped)
                profile.SkippedCount++;

            if (result.Success)
            {
                profile.SucceededCount++;
                if (!result.IsSkipped)
                {
                    profile.LastSuccessfulSyncUtc = _clock.UtcNow;
                }
            }
            else
            {
                profile.FailedCount++;
                profile.LastFailedSyncUtc = _clock.UtcNow;
                profile.LastFailure = result.ErrorMessage;
            }

            PersistLocked();
        }
    }

    public void RecordReconciliationStarted(string profileName, string trigger)
    {
        lock (_gate)
        {
            var reconciliation = GetProfile(profileName).Reconciliation;
            reconciliation.LastTrigger = trigger;
            reconciliation.LastStartedAtUtc = _clock.UtcNow;
            PersistLocked();
        }
    }

    public void RecordReconciliationCompleted(string profileName, string trigger, RobocopyResult result, TimeSpan duration)
    {
        lock (_gate)
        {
            var reconciliation = GetProfile(profileName).Reconciliation;
            reconciliation.RunCount++;
            reconciliation.LastTrigger = trigger;
            reconciliation.LastCompletedAtUtc = _clock.UtcNow;
            reconciliation.LastDurationMs = duration.TotalMilliseconds;
            reconciliation.LastSuccess = result.Success;
            reconciliation.LastExitCode = result.ExitCode;
            reconciliation.LastExitDescription = result.ExitDescription;
            reconciliation.LastSummary = result.Summary;
            PersistLocked();
        }
    }

    private ProfileHealthSnapshot GetProfile(string profileName)
    {
        var profile = _snapshot.Profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
            return profile;

        profile = new ProfileHealthSnapshot { Name = profileName };
        _snapshot.Profiles.Add(profile);
        return profile;
    }

    private void PersistLocked()
    {
        _snapshot.UpdatedAtUtc = _clock.UtcNow;

        try
        {
            var directory = Path.GetDirectoryName(SnapshotPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_snapshot, SerializerOptions);
            var tempPath = SnapshotPath + ".tmp";

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SnapshotPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist runtime health snapshot to {Path}", SnapshotPath);
        }
    }
}
