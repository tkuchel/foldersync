using System.CommandLine;
using System.Text.Json;
using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace FolderSync.Commands;

public static class TwoWayPreviewCommand
{
    public static Command Create()
    {
        var configOption = new Option<string?>("--config")
        {
            Description = "Path to custom appsettings.json file"
        };

        var profileOption = new Option<string?>("--profile")
        {
            Description = "Run preview for a specific profile only"
        };

        var triggerOption = new Option<string>("--trigger")
        {
            Description = "Internal trigger label for runtime health history",
            DefaultValueFactory = _ => "Command"
        };
        triggerOption.Hidden = true;

        var command = new Command("twoway-preview", "Run a read-only two-way preview scan for one or more profiles");
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

            var configuration = BuildConfiguration(resolvedConfigPath);
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

            if (profiles.Count == 0)
            {
                Log.Error("No profiles found to preview.");
                Environment.ExitCode = 1;
                return;
            }

            var pathSafety = new PathSafetyService();
            var hasher = new Sha256FileHasher();
            var classifier = new TwoWayPreviewClassifier();
            var previewService = new TwoWayPreviewService(hasher, pathSafety, classifier);
            var snapshotPath = ReconcileCommand.ResolveRuntimeHealthPath(resolvedConfigPath);
            var anyFailure = false;

            foreach (var profile in profiles)
            {
                var stateStorePath = ResolveStateStorePath(profile, resolvedConfigPath);
                Log.Information("[{Profile}] Running two-way preview scan...", profile.Name);
                RecordPreviewActivity(snapshotPath, profile.Name, "preview", $"Two-way preview requested ({trigger})", trigger);

                try
                {
                    var result = await previewService.RunAsync(profile.Name, profile.Options, stateStorePath, cancellationToken);
                    RecordPreviewActivity(
                        snapshotPath,
                        profile.Name,
                        "preview",
                        $"Two-way preview completed ({result.ConflictCount} conflict{(result.ConflictCount == 1 ? string.Empty : "s")})",
                        $"changes={result.ChangeCount}, conflicts={result.ConflictCount}");
                    Log.Information(
                        "[{Profile}] Two-way preview completed. Changes={ChangeCount}, Conflicts={ConflictCount}, State={StateStorePath}",
                        profile.Name,
                        result.ChangeCount,
                        result.ConflictCount,
                        result.StateStorePath);
                }
                catch (Exception ex)
                {
                    anyFailure = true;
                    RecordPreviewActivity(snapshotPath, profile.Name, "preview", "Two-way preview failed", ex.Message);
                    Log.Error(ex, "[{Profile}] Two-way preview failed", profile.Name);
                }
            }

            if (anyFailure)
                Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Two-way preview failed");
            Environment.ExitCode = 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static IConfigurationRoot BuildConfiguration(string? resolvedConfigPath)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.example.json", optional: false)
            .AddJsonFile("appsettings.json", optional: true);

        if (!string.IsNullOrWhiteSpace(resolvedConfigPath))
            builder.AddJsonFile(resolvedConfigPath, optional: false);

        return builder.Build();
    }

    internal static string ResolveStateStorePath(ResolvedProfile profile, string? resolvedConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(profile.Options.TwoWay.StateStorePath))
            return Path.GetFullPath(profile.Options.TwoWay.StateStorePath);

        var baseDirectory = !string.IsNullOrWhiteSpace(resolvedConfigPath)
            ? Path.GetDirectoryName(resolvedConfigPath)
            : AppContext.BaseDirectory;

        return Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "state", $"{profile.Name}.twoway.json");
    }

    internal static void RecordPreviewActivity(string snapshotPath, string profileName, string kind, string summary, string? details)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(snapshotPath) ?? new RuntimeHealthSnapshot
            {
                ServiceName = HostBuilderHelper.DefaultServiceName,
                ServiceState = "Running",
                StartedAtUtc = now
            };

            var profile = snapshot.Profiles.FirstOrDefault(item =>
                string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                profile = new ProfileHealthSnapshot
                {
                    Name = profileName,
                    State = "Running"
                };
                snapshot.Profiles.Add(profile);
            }

            profile.AddActivity(new ProfileActivitySnapshot
            {
                Kind = kind,
                Summary = summary,
                TimestampUtc = now,
                Details = details
            });

            snapshot.UpdatedAtUtc = now;
            PersistRuntimeHealthSnapshot(snapshotPath, snapshot);
        }
        catch
        {
        }
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
