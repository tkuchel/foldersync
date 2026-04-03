using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FolderSync.Models;

namespace FolderSync.Services;

internal sealed class DashboardProfileConfigSnapshot
{
    public required string ConfigPath { get; init; }
    public List<SyncProfileConfig> Profiles { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool RequiresServiceRestart { get; init; } = true;
}

internal sealed class DashboardProfileSaveResult
{
    public required DashboardProfileConfigSnapshot Snapshot { get; init; }
    public List<string> Errors { get; init; } = [];
    public bool Success => Errors.Count == 0;
}

internal sealed class DashboardProfileEditRequest
{
    public string? OriginalName { get; set; }
    public SyncProfileConfig Profile { get; set; } = new();
}

internal sealed class DashboardProfileDeleteRequest
{
    public string? ProfileName { get; set; }
}

internal static class DashboardProfileConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DashboardProfileConfigSnapshot Load(string configPath)
    {
        var (_, config) = LoadDocument(configPath);
        var profiles = GetEditableProfiles(config);
        var validation = ProfileConfigurationValidator.Validate(config.ResolveProfiles());

        return new DashboardProfileConfigSnapshot
        {
            ConfigPath = configPath,
            Profiles = profiles,
            Warnings = validation.Warnings.Select(issue => issue.Message).ToList()
        };
    }

    public static DashboardProfileSaveResult SaveProfile(string configPath, DashboardProfileEditRequest request)
    {
        var (root, config) = LoadDocument(configPath);
        var profiles = GetEditableProfiles(config);
        var incoming = CloneProfile(request.Profile);

        NormalizeProfile(incoming);

        if (string.IsNullOrWhiteSpace(incoming.Name))
            return Failed(configPath, profiles, "Profile name is required.");

        var existingIndex = FindProfileIndex(profiles, request.OriginalName ?? incoming.Name);
        var duplicateIndex = FindProfileIndex(profiles, incoming.Name);
        if (duplicateIndex >= 0 && duplicateIndex != existingIndex)
            return Failed(configPath, profiles, $"A profile named '{incoming.Name}' already exists.");

        if (existingIndex >= 0)
            profiles[existingIndex] = incoming;
        else
            profiles.Add(incoming);

        ApplyEditableProfiles(config, profiles);
        var validation = ProfileConfigurationValidator.Validate(config.ResolveProfiles());
        if (validation.HasErrors)
        {
            return new DashboardProfileSaveResult
            {
                Snapshot = new DashboardProfileConfigSnapshot
                {
                    ConfigPath = configPath,
                    Profiles = profiles,
                    Warnings = validation.Warnings.Select(issue => issue.Message).ToList()
                },
                Errors = validation.Errors.Select(issue => issue.Message).ToList()
            };
        }

        Persist(configPath, root, config);
        return new DashboardProfileSaveResult
        {
            Snapshot = new DashboardProfileConfigSnapshot
            {
                ConfigPath = configPath,
                Profiles = profiles,
                Warnings = validation.Warnings.Select(issue => issue.Message).ToList()
            }
        };
    }

    public static DashboardProfileSaveResult DeleteProfile(string configPath, string profileName)
    {
        var (root, config) = LoadDocument(configPath);
        var profiles = GetEditableProfiles(config);
        var index = FindProfileIndex(profiles, profileName);
        if (index < 0)
            return Failed(configPath, profiles, $"Profile '{profileName}' was not found.");

        profiles.RemoveAt(index);
        ApplyEditableProfiles(config, profiles);

        var validation = ProfileConfigurationValidator.Validate(config.ResolveProfiles());
        if (validation.HasErrors)
        {
            return new DashboardProfileSaveResult
            {
                Snapshot = new DashboardProfileConfigSnapshot
                {
                    ConfigPath = configPath,
                    Profiles = profiles,
                    Warnings = validation.Warnings.Select(issue => issue.Message).ToList()
                },
                Errors = validation.Errors.Select(issue => issue.Message).ToList()
            };
        }

        Persist(configPath, root, config);
        return new DashboardProfileSaveResult
        {
            Snapshot = new DashboardProfileConfigSnapshot
            {
                ConfigPath = configPath,
                Profiles = profiles,
                Warnings = validation.Warnings.Select(issue => issue.Message).ToList()
            }
        };
    }

    private static DashboardProfileSaveResult Failed(string configPath, List<SyncProfileConfig> profiles, string error)
    {
        return new DashboardProfileSaveResult
        {
            Snapshot = new DashboardProfileConfigSnapshot
            {
                ConfigPath = configPath,
                Profiles = profiles
            },
            Errors = [error]
        };
    }

    private static (JsonObject Root, FolderSyncConfig Config) LoadDocument(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new InvalidOperationException("Installed appsettings.json not found.");

        if (!File.Exists(configPath))
            throw new FileNotFoundException("Installed appsettings.json not found.", configPath);

        JsonObject root;
        using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            root = JsonNode.Parse(stream)?.AsObject() ?? new JsonObject();
        }

        var folderSyncNode = root[FolderSyncConfig.SectionName];
        var config = folderSyncNode?.Deserialize<FolderSyncConfig>(JsonOptions) ?? new FolderSyncConfig();
        return (root, config);
    }

    private static List<SyncProfileConfig> GetEditableProfiles(FolderSyncConfig config)
    {
        if (config.Profiles.Count > 0)
            return config.Profiles.Select(CloneProfile).ToList();

        if (!string.IsNullOrWhiteSpace(config.SourcePath) && !string.IsNullOrWhiteSpace(config.DestinationPath))
        {
            return
            [
                new SyncProfileConfig
                {
                    Name = "default",
                    SourcePath = config.SourcePath,
                    DestinationPath = config.DestinationPath
                }
            ];
        }

        return [];
    }

    private static void ApplyEditableProfiles(FolderSyncConfig config, List<SyncProfileConfig> profiles)
    {
        config.Profiles = profiles.Select(CloneProfile).ToList();
        config.SourcePath = string.Empty;
        config.DestinationPath = string.Empty;
    }

    private static SyncProfileConfig CloneProfile(SyncProfileConfig profile)
    {
        return new SyncProfileConfig
        {
            Name = profile.Name,
            SourcePath = profile.SourcePath,
            DestinationPath = profile.DestinationPath,
            IncludeSubdirectories = profile.IncludeSubdirectories,
            SyncDeletions = profile.SyncDeletions,
            DeleteMode = profile.DeleteMode,
            DeleteArchivePath = profile.DeleteArchivePath,
            ConflictMode = profile.ConflictMode,
            UseHashComparison = profile.UseHashComparison,
            IgnoreLastWriteTimeDriftSeconds = profile.IgnoreLastWriteTimeDriftSeconds,
            DebounceWindowMilliseconds = profile.DebounceWindowMilliseconds,
            DryRun = profile.DryRun,
            StabilityCheck = profile.StabilityCheck is null
                ? null
                : new StabilityCheckOptions
                {
                    Enabled = profile.StabilityCheck.Enabled,
                    PollingIntervalMilliseconds = profile.StabilityCheck.PollingIntervalMilliseconds,
                    RequiredStableObservations = profile.StabilityCheck.RequiredStableObservations,
                    MaxWaitMilliseconds = profile.StabilityCheck.MaxWaitMilliseconds
                },
            Retry = profile.Retry is null
                ? null
                : new RetryOptions
                {
                    MaxAttempts = profile.Retry.MaxAttempts,
                    InitialDelayMilliseconds = profile.Retry.InitialDelayMilliseconds,
                    BackoffMultiplier = profile.Retry.BackoffMultiplier,
                    MaxDelayMilliseconds = profile.Retry.MaxDelayMilliseconds
                },
            Reconciliation = profile.Reconciliation is null
                ? null
                : new ReconciliationOptions
                {
                    Enabled = profile.Reconciliation.Enabled,
                    IntervalMinutes = profile.Reconciliation.IntervalMinutes,
                    RunOnStartup = profile.Reconciliation.RunOnStartup,
                    UseRobocopy = profile.Reconciliation.UseRobocopy,
                    RobocopyOptions = profile.Reconciliation.RobocopyOptions
                },
            Exclusions = profile.Exclusions is null
                ? null
                : new ExclusionOptions
                {
                    DirectoryNames = [.. profile.Exclusions.DirectoryNames],
                    FilePatterns = [.. profile.Exclusions.FilePatterns],
                    Extensions = [.. profile.Exclusions.Extensions]
                }
        };
    }

    private static void NormalizeProfile(SyncProfileConfig profile)
    {
        profile.Name = profile.Name.Trim();
        profile.SourcePath = profile.SourcePath.Trim();
        profile.DestinationPath = profile.DestinationPath.Trim();
        profile.DeleteArchivePath = string.IsNullOrWhiteSpace(profile.DeleteArchivePath) ? null : profile.DeleteArchivePath.Trim();

        if (profile.Exclusions is not null)
        {
            profile.Exclusions.DirectoryNames = NormalizeList(profile.Exclusions.DirectoryNames);
            profile.Exclusions.FilePatterns = NormalizeList(profile.Exclusions.FilePatterns);
            profile.Exclusions.Extensions = NormalizeList(profile.Exclusions.Extensions);

            if (profile.Exclusions.DirectoryNames.Count == 0 &&
                profile.Exclusions.FilePatterns.Count == 0 &&
                profile.Exclusions.Extensions.Count == 0)
            {
                profile.Exclusions = null;
            }
        }

        if (profile.Reconciliation is not null &&
            profile.Reconciliation.UseRobocopy &&
            string.IsNullOrWhiteSpace(profile.Reconciliation.RobocopyOptions))
        {
            profile.Reconciliation.RobocopyOptions = new ReconciliationOptions().RobocopyOptions;
        }
    }

    private static List<string> NormalizeList(IEnumerable<string> values)
    {
        return values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private static int FindProfileIndex(List<SyncProfileConfig> profiles, string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return -1;

        return profiles.FindIndex(profile =>
            string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));
    }

    private static void Persist(string configPath, JsonObject root, FolderSyncConfig config)
    {
        root[FolderSyncConfig.SectionName] = JsonSerializer.SerializeToNode(config, JsonOptions);

        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = configPath + ".tmp";
        File.WriteAllText(tempPath, root.ToJsonString(JsonOptions));
        File.Move(tempPath, configPath, overwrite: true);
    }
}
