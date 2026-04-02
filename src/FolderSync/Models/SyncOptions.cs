using System.ComponentModel.DataAnnotations;

namespace FolderSync.Models;

/// <summary>
/// Top-level configuration section bound to "FolderSync".
/// Supports named profiles with shared defaults.
/// </summary>
public sealed class FolderSyncConfig
{
    public const string SectionName = "FolderSync";

    /// <summary>
    /// Default settings inherited by all profiles unless overridden.
    /// </summary>
    public SyncOptions Defaults { get; set; } = new();

    /// <summary>
    /// Named sync profiles. Each gets its own watcher + pipeline.
    /// </summary>
    public List<SyncProfileConfig> Profiles { get; set; } = [];

    // Backward compat: root-level SourcePath/DestinationPath
    // If Profiles is empty but these are set, creates an implicit "default" profile.
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// Resolves the effective list of profiles by merging each profile config
    /// with defaults. Handles backward-compat single-profile config.
    /// </summary>
    public List<ResolvedProfile> ResolveProfiles()
    {
        var profiles = new List<ResolvedProfile>();

        if (Profiles.Count > 0)
        {
            foreach (var profile in Profiles)
            {
                profiles.Add(new ResolvedProfile(
                    profile.Name,
                    profile.MergeWithDefaults(Defaults)));
            }
        }
        else if (!string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(DestinationPath))
        {
            // Backward compat: treat root-level source/dest as a single profile
            var options = Defaults.Clone();
            options.SourcePath = SourcePath;
            options.DestinationPath = DestinationPath;
            profiles.Add(new ResolvedProfile("default", options));
        }

        return profiles;
    }
}

/// <summary>
/// A resolved profile with its effective options ready to use.
/// </summary>
public sealed record ResolvedProfile(string Name, SyncOptions Options);

/// <summary>
/// Per-profile configuration. All fields except Name/SourcePath/DestinationPath
/// are nullable — null means "inherit from defaults".
/// </summary>
public sealed class SyncProfileConfig
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string SourcePath { get; set; } = string.Empty;

    [Required]
    public string DestinationPath { get; set; } = string.Empty;

    public bool? IncludeSubdirectories { get; set; }
    public bool? SyncDeletions { get; set; }
    public DeleteMode? DeleteMode { get; set; }
    public string? DeleteArchivePath { get; set; }
    public ConflictMode? ConflictMode { get; set; }
    public bool? UseHashComparison { get; set; }
    public int? IgnoreLastWriteTimeDriftSeconds { get; set; }
    public int? DebounceWindowMilliseconds { get; set; }
    public bool? DryRun { get; set; }

    public StabilityCheckOptions? StabilityCheck { get; set; }
    public RetryOptions? Retry { get; set; }
    public ReconciliationOptions? Reconciliation { get; set; }
    public ExclusionOptions? Exclusions { get; set; }

    /// <summary>
    /// Produces the effective SyncOptions by starting from defaults
    /// and overlaying any explicitly set profile values.
    /// Exclusion lists are merged (profile adds to defaults).
    /// </summary>
    public SyncOptions MergeWithDefaults(SyncOptions defaults)
    {
        var result = defaults.Clone();

        result.SourcePath = SourcePath;
        result.DestinationPath = DestinationPath;

        if (IncludeSubdirectories.HasValue) result.IncludeSubdirectories = IncludeSubdirectories.Value;
        if (SyncDeletions.HasValue) result.SyncDeletions = SyncDeletions.Value;
        if (DeleteMode.HasValue) result.DeleteMode = DeleteMode.Value;
        if (DeleteArchivePath is not null) result.DeleteArchivePath = DeleteArchivePath;
        if (ConflictMode.HasValue) result.ConflictMode = ConflictMode.Value;
        if (UseHashComparison.HasValue) result.UseHashComparison = UseHashComparison.Value;
        if (IgnoreLastWriteTimeDriftSeconds.HasValue) result.IgnoreLastWriteTimeDriftSeconds = IgnoreLastWriteTimeDriftSeconds.Value;
        if (DebounceWindowMilliseconds.HasValue) result.DebounceWindowMilliseconds = DebounceWindowMilliseconds.Value;
        if (DryRun.HasValue) result.DryRun = DryRun.Value;

        if (StabilityCheck is not null)
        {
            result.StabilityCheck = new StabilityCheckOptions
            {
                Enabled = StabilityCheck.Enabled,
                PollingIntervalMilliseconds = StabilityCheck.PollingIntervalMilliseconds,
                RequiredStableObservations = StabilityCheck.RequiredStableObservations,
                MaxWaitMilliseconds = StabilityCheck.MaxWaitMilliseconds
            };
        }

        if (Retry is not null)
        {
            result.Retry = new RetryOptions
            {
                MaxAttempts = Retry.MaxAttempts,
                InitialDelayMilliseconds = Retry.InitialDelayMilliseconds,
                BackoffMultiplier = Retry.BackoffMultiplier,
                MaxDelayMilliseconds = Retry.MaxDelayMilliseconds
            };
        }

        if (Reconciliation is not null)
        {
            result.Reconciliation = new ReconciliationOptions
            {
                Enabled = Reconciliation.Enabled,
                IntervalMinutes = Reconciliation.IntervalMinutes,
                RunOnStartup = Reconciliation.RunOnStartup,
                UseRobocopy = Reconciliation.UseRobocopy,
                RobocopyOptions = Reconciliation.RobocopyOptions
            };
        }

        // Exclusions are MERGED — profile adds to defaults
        if (Exclusions is not null)
        {
            result.Exclusions = new ExclusionOptions
            {
                DirectoryNames = [.. defaults.Exclusions.DirectoryNames, .. Exclusions.DirectoryNames],
                FilePatterns = [.. defaults.Exclusions.FilePatterns, .. Exclusions.FilePatterns],
                Extensions = [.. defaults.Exclusions.Extensions, .. Exclusions.Extensions]
            };

            // Deduplicate
            result.Exclusions.DirectoryNames = result.Exclusions.DirectoryNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Exclusions.FilePatterns = result.Exclusions.FilePatterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Exclusions.Extensions = result.Exclusions.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return result;
    }
}

/// <summary>
/// Effective per-profile sync options. All services consume this.
/// </summary>
public sealed class SyncOptions
{
    public const string SectionName = "FolderSync";

    [Required]
    public string SourcePath { get; set; } = string.Empty;

    [Required]
    public string DestinationPath { get; set; } = string.Empty;

    public bool IncludeSubdirectories { get; set; } = true;
    public bool SyncDeletions { get; set; }
    public DeleteMode DeleteMode { get; set; } = DeleteMode.Archive;
    public string DeleteArchivePath { get; set; } = string.Empty;
    public ConflictMode ConflictMode { get; set; } = ConflictMode.SourceWins;
    public bool UseHashComparison { get; set; } = true;
    public int IgnoreLastWriteTimeDriftSeconds { get; set; } = 2;
    public int DebounceWindowMilliseconds { get; set; } = 1500;
    public bool DryRun { get; set; }

    public StabilityCheckOptions StabilityCheck { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public ReconciliationOptions Reconciliation { get; set; } = new();
    public ExclusionOptions Exclusions { get; set; } = new();

    public SyncOptions Clone()
    {
        return new SyncOptions
        {
            SourcePath = SourcePath,
            DestinationPath = DestinationPath,
            IncludeSubdirectories = IncludeSubdirectories,
            SyncDeletions = SyncDeletions,
            DeleteMode = DeleteMode,
            DeleteArchivePath = DeleteArchivePath,
            ConflictMode = ConflictMode,
            UseHashComparison = UseHashComparison,
            IgnoreLastWriteTimeDriftSeconds = IgnoreLastWriteTimeDriftSeconds,
            DebounceWindowMilliseconds = DebounceWindowMilliseconds,
            DryRun = DryRun,
            StabilityCheck = new StabilityCheckOptions
            {
                Enabled = StabilityCheck.Enabled,
                PollingIntervalMilliseconds = StabilityCheck.PollingIntervalMilliseconds,
                RequiredStableObservations = StabilityCheck.RequiredStableObservations,
                MaxWaitMilliseconds = StabilityCheck.MaxWaitMilliseconds
            },
            Retry = new RetryOptions
            {
                MaxAttempts = Retry.MaxAttempts,
                InitialDelayMilliseconds = Retry.InitialDelayMilliseconds,
                BackoffMultiplier = Retry.BackoffMultiplier,
                MaxDelayMilliseconds = Retry.MaxDelayMilliseconds
            },
            Reconciliation = new ReconciliationOptions
            {
                Enabled = Reconciliation.Enabled,
                IntervalMinutes = Reconciliation.IntervalMinutes,
                RunOnStartup = Reconciliation.RunOnStartup,
                UseRobocopy = Reconciliation.UseRobocopy,
                RobocopyOptions = Reconciliation.RobocopyOptions
            },
            Exclusions = new ExclusionOptions
            {
                DirectoryNames = [.. Exclusions.DirectoryNames],
                FilePatterns = [.. Exclusions.FilePatterns],
                Extensions = [.. Exclusions.Extensions]
            }
        };
    }
}

public sealed class StabilityCheckOptions
{
    public bool Enabled { get; set; } = true;
    public int PollingIntervalMilliseconds { get; set; } = 1000;
    public int RequiredStableObservations { get; set; } = 2;
    public int MaxWaitMilliseconds { get; set; } = 60000;
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 5;
    public int InitialDelayMilliseconds { get; set; } = 2000;
    public double BackoffMultiplier { get; set; } = 2.0;
    public int MaxDelayMilliseconds { get; set; } = 30000;
}

public sealed class ReconciliationOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;
    public bool RunOnStartup { get; set; } = true;
    public bool UseRobocopy { get; set; } = true;
    public string RobocopyOptions { get; set; } = "/E /FFT /Z /R:2 /W:5 /XO /NFL /NDL /NP";
}

public sealed class ExclusionOptions
{
    public List<string> DirectoryNames { get; set; } = [];
    public List<string> FilePatterns { get; set; } = [];
    public List<string> Extensions { get; set; } = [];
}
