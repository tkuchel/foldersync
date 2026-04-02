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
    void RecordPauseState(bool paused, string? reason, DateTimeOffset? changedAtUtc);
    void RecordProfilePauseState(string profileName, bool paused, string? reason, DateTimeOffset? changedAtUtc);
    void RecordProfileState(string profileName, string state);
    void RecordWatcherOverflow(string profileName);
    void RecordSyncResult(string profileName, SyncResult result);
    void RecordReconciliationStarted(string profileName, string trigger);
    void RecordReconciliationCompleted(string profileName, string trigger, RobocopyResult result, TimeSpan duration);
}

public sealed class RuntimeHealthStore : IRuntimeHealthStore
{
    private const int ConsecutiveFailureAlertThreshold = 3;
    private const int OverflowAlertThreshold = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IAlertNotifier _alertNotifier;
    private readonly ILogger<RuntimeHealthStore> _logger;
    private RuntimeHealthSnapshot _snapshot;

    public RuntimeHealthStore(IClock clock, IAlertNotifier alertNotifier, ILogger<RuntimeHealthStore> logger)
        : this(Path.Combine(Environment.CurrentDirectory, "foldersync-health.json"), clock, alertNotifier, logger)
    {
    }

    internal RuntimeHealthStore(string snapshotPath, IClock clock, IAlertNotifier alertNotifier, ILogger<RuntimeHealthStore> logger)
    {
        SnapshotPath = snapshotPath;
        _clock = clock;
        _alertNotifier = alertNotifier;
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

    public void RecordPauseState(bool paused, string? reason, DateTimeOffset? changedAtUtc)
    {
        lock (_gate)
        {
            _snapshot.IsPaused = paused;
            _snapshot.PauseReason = paused ? reason : null;
            _snapshot.PausedAtUtc = paused ? changedAtUtc ?? _clock.UtcNow : null;
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

    public void RecordProfilePauseState(string profileName, bool paused, string? reason, DateTimeOffset? changedAtUtc)
    {
        lock (_gate)
        {
            var profile = GetProfile(profileName);
            profile.IsPaused = paused;
            profile.PauseReason = paused ? reason : null;
            profile.PausedAtUtc = paused ? changedAtUtc ?? _clock.UtcNow : null;
            PersistLocked();
        }
    }

    public void RecordWatcherOverflow(string profileName)
    {
        lock (_gate)
        {
            var profile = GetProfile(profileName);
            profile.WatcherOverflowCount++;
            profile.ConsecutiveOverflowCount++;
            profile.AddActivity(new ProfileActivitySnapshot
            {
                Kind = "overflow",
                Summary = "Watcher overflow triggered reconciliation",
                TimestampUtc = _clock.UtcNow
            });
            UpdateAlertState(profile);
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
                profile.ConsecutiveFailureCount = 0;
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
                profile.ConsecutiveFailureCount++;
            }

            if (!result.IsSkipped || result.Success)
                profile.ConsecutiveOverflowCount = 0;

            profile.AddActivity(CreateSyncActivity(result));
            UpdateAlertState(profile);

            PersistLocked();
        }
    }

    public void RecordReconciliationStarted(string profileName, string trigger)
    {
        lock (_gate)
        {
            var profile = GetProfile(profileName);
            var reconciliation = profile.Reconciliation;
            reconciliation.LastTrigger = trigger;
            reconciliation.LastStartedAtUtc = _clock.UtcNow;
            profile.AddActivity(new ProfileActivitySnapshot
            {
                Kind = "reconcile",
                Summary = $"Reconciliation started ({trigger})",
                TimestampUtc = _clock.UtcNow,
                Details = trigger
            });
            PersistLocked();
        }
    }

    public void RecordReconciliationCompleted(string profileName, string trigger, RobocopyResult result, TimeSpan duration)
    {
        lock (_gate)
        {
            var profile = GetProfile(profileName);
            var reconciliation = profile.Reconciliation;
            reconciliation.RunCount++;
            reconciliation.LastTrigger = trigger;
            reconciliation.LastCompletedAtUtc = _clock.UtcNow;
            reconciliation.LastDurationMs = duration.TotalMilliseconds;
            reconciliation.LastSuccess = result.Success;
            reconciliation.LastExitCode = result.ExitCode;
            reconciliation.LastExitDescription = result.ExitDescription;
            reconciliation.LastSummary = result.Summary;
            profile.AddActivity(new ProfileActivitySnapshot
            {
                Kind = "reconcile",
                Summary = result.Success
                    ? $"Reconciliation completed ({result.ExitCode})"
                    : $"Reconciliation failed ({result.ExitCode})",
                TimestampUtc = _clock.UtcNow,
                Details = result.ExitDescription
            });
            PersistLocked();
        }
    }

    private ProfileActivitySnapshot CreateSyncActivity(SyncResult result)
    {
        var relativePath = TryGetRelativePath(result.WorkItem);
        var kind = result.Success
            ? result.IsSkipped ? "skip" : "sync"
            : "failure";

        var summary = kind switch
        {
            "sync" => $"Synced {relativePath ?? result.WorkItem.SourcePath}",
            "skip" => $"Skipped {relativePath ?? result.WorkItem.SourcePath}",
            _ => $"Failed {relativePath ?? result.WorkItem.SourcePath}"
        };

        return new ProfileActivitySnapshot
        {
            Kind = kind,
            Summary = summary,
            TimestampUtc = _clock.UtcNow,
            RelativePath = relativePath,
            Details = result.ErrorMessage
        };
    }

    private static string? TryGetRelativePath(SyncWorkItem workItem)
    {
        if (!string.IsNullOrWhiteSpace(workItem.SourcePath))
            return Path.GetFileName(workItem.SourcePath);

        return null;
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

    private void UpdateAlertState(ProfileHealthSnapshot profile)
    {
        string? newLevel = null;
        string? newMessage = null;

        if (profile.ConsecutiveFailureCount >= ConsecutiveFailureAlertThreshold)
        {
            newLevel = "warning";
            newMessage = $"Profile '{profile.Name}' has {profile.ConsecutiveFailureCount} consecutive failed sync operations.";
        }
        else if (profile.ConsecutiveOverflowCount >= OverflowAlertThreshold)
        {
            newLevel = "warning";
            newMessage = $"Profile '{profile.Name}' has hit watcher overflow {profile.ConsecutiveOverflowCount} times in a row.";
        }

        if (newMessage is null)
        {
            profile.AlertLevel = null;
            profile.AlertMessage = null;
            return;
        }

        if (!string.Equals(profile.AlertMessage, newMessage, StringComparison.Ordinal))
        {
            _logger.LogWarning(newMessage);
            profile.LastAlertUtc = _clock.UtcNow;
            _alertNotifier.Publish(new AlertNotification
            {
                ServiceName = _snapshot.ServiceName,
                ProfileName = profile.Name,
                Level = newLevel ?? "warning",
                Message = newMessage,
                TimestampUtc = profile.LastAlertUtc.Value
            });
        }

        profile.AlertLevel = newLevel;
        profile.AlertMessage = newMessage;
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
