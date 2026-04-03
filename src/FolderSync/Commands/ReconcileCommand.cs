using System.CommandLine;
using System.Text.Json;
using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;

namespace FolderSync.Commands;

public static class ReconcileCommand
{
    public static Command Create()
    {
        var configOption = new Option<string?>("--config")
        {
            Description = "Path to custom appsettings.json file"
        };

        var profileOption = new Option<string?>("--profile")
        {
            Description = "Run reconciliation for a specific profile only"
        };

        var triggerOption = new Option<string>("--trigger")
        {
            Description = "Internal trigger label for runtime health history",
            DefaultValueFactory = _ => "Command"
        };
        triggerOption.Hidden = true;

        var command = new Command("reconcile", "Run a one-shot reconciliation for all (or a specific) profile, then exit");
        command.Options.Add(configOption);
        command.Options.Add(profileOption);
        command.Options.Add(triggerOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var configPath = parseResult.GetValue(configOption);
            var profile = parseResult.GetValue(profileOption);
            var trigger = parseResult.GetValue(triggerOption) ?? "Command";
            await ExecuteAsync(configPath, profile, trigger, cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(string? configPath, string? profileName, string trigger, CancellationToken cancellationToken)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var resolvedConfigPath = !string.IsNullOrWhiteSpace(configPath)
                ? Path.GetFullPath(configPath)
                : null;

            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.example.json", optional: false)
                .AddJsonFile("appsettings.json", optional: true);

            if (!string.IsNullOrWhiteSpace(resolvedConfigPath))
                configBuilder.AddJsonFile(resolvedConfigPath, optional: false);

            var configuration = configBuilder.Build();
            var config = new FolderSyncConfig();
            configuration.GetSection(FolderSyncConfig.SectionName).Bind(config);

            var profiles = config.ResolveProfiles();

            if (!string.IsNullOrWhiteSpace(profileName))
            {
                profiles = profiles
                    .Where(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (profiles.Count == 0)
                {
                    Log.Error("Profile '{ProfileName}' not found", profileName);
                    Environment.ExitCode = 1;
                    return;
                }
            }

            var validation = ProfileConfigurationValidator.Validate(profiles);
            foreach (var warning in validation.Warnings)
                Log.Warning("{Message}", warning.Message);
            if (validation.HasErrors)
            {
                foreach (var error in validation.Errors)
                    Log.Error("{Message}", error.Message);
                Environment.ExitCode = 1;
                return;
            }

            var processRunner = new ProcessRunner();
            var hasFailure = false;
            var snapshotPath = ResolveRuntimeHealthPath(resolvedConfigPath);

            foreach (var profile in profiles)
            {
                Log.Information("[{Profile}] Running reconciliation...", profile.Name);

                var startedAt = DateTimeOffset.UtcNow;
                RecordReconciliationStarted(snapshotPath, profile.Name, trigger, startedAt);

                var options = Options.Create(profile.Options);
                var robocopy = new RobocopyService(processRunner, options, NullLogger<RobocopyService>.Instance);
                var result = await robocopy.ReconcileAsync(cancellationToken);
                var completedAt = DateTimeOffset.UtcNow;
                RecordReconciliationCompleted(snapshotPath, profile.Name, trigger, result, completedAt - startedAt, completedAt);

                if (result.Success)
                {
                    Log.Information("[{Profile}] Reconciliation completed (exit code {ExitCode})",
                        profile.Name, result.ExitCode);
                }
                else
                {
                    Log.Error("[{Profile}] Reconciliation failed (exit code {ExitCode})",
                        profile.Name, result.ExitCode);
                    hasFailure = true;
                }
            }

            if (hasFailure)
                Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Reconciliation failed");
            Environment.ExitCode = 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    internal static string ResolveRuntimeHealthPath(string? resolvedConfigPath)
    {
        var baseDirectory = !string.IsNullOrWhiteSpace(resolvedConfigPath)
            ? Path.GetDirectoryName(resolvedConfigPath)
            : AppContext.BaseDirectory;

        return Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "foldersync-health.json");
    }

    internal static void RecordReconciliationStarted(string snapshotPath, string profileName, string trigger, DateTimeOffset startedAtUtc)
    {
        UpdateRuntimeHealthSnapshot(snapshotPath, snapshot =>
        {
            var profile = GetOrCreateProfile(snapshot, profileName, startedAtUtc);
            profile.Reconciliation.LastTrigger = trigger;
            profile.Reconciliation.LastStartedAtUtc = startedAtUtc;
            profile.AddActivity(new ProfileActivitySnapshot
            {
                Kind = "reconcile",
                Summary = $"Reconciliation started ({trigger})",
                TimestampUtc = startedAtUtc,
                Details = trigger
            });
        }, startedAtUtc);
    }

    internal static void RecordReconciliationCompleted(
        string snapshotPath,
        string profileName,
        string trigger,
        RobocopyResult result,
        TimeSpan duration,
        DateTimeOffset completedAtUtc)
    {
        UpdateRuntimeHealthSnapshot(snapshotPath, snapshot =>
        {
            var profile = GetOrCreateProfile(snapshot, profileName, completedAtUtc);
            var reconciliation = profile.Reconciliation;
            reconciliation.RunCount++;
            reconciliation.LastTrigger = trigger;
            reconciliation.LastCompletedAtUtc = completedAtUtc;
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
                TimestampUtc = completedAtUtc,
                Details = result.ExitDescription
            });
        }, completedAtUtc);
    }

    private static void UpdateRuntimeHealthSnapshot(string snapshotPath, Action<RuntimeHealthSnapshot> update, DateTimeOffset now)
    {
        try
        {
            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(snapshotPath) ?? new RuntimeHealthSnapshot
            {
                ServiceName = HostBuilderHelper.DefaultServiceName,
                ServiceState = "Running",
                StartedAtUtc = now
            };

            update(snapshot);
            snapshot.UpdatedAtUtc = now;
            PersistRuntimeHealthSnapshot(snapshotPath, snapshot);
        }
        catch
        {
        }
    }

    private static ProfileHealthSnapshot GetOrCreateProfile(RuntimeHealthSnapshot snapshot, string profileName, DateTimeOffset now)
    {
        snapshot.ServiceState = string.IsNullOrWhiteSpace(snapshot.ServiceState)
            ? "Running"
            : snapshot.ServiceState;
        snapshot.StartedAtUtc = snapshot.StartedAtUtc == default ? now : snapshot.StartedAtUtc;

        var profile = snapshot.Profiles.FirstOrDefault(item =>
            string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
            return profile;

        profile = new ProfileHealthSnapshot
        {
            Name = profileName,
            State = "Running"
        };
        snapshot.Profiles.Add(profile);
        return profile;
    }

    private static void PersistRuntimeHealthSnapshot(string snapshotPath, RuntimeHealthSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = snapshotPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, snapshotPath, overwrite: true);
    }
}
