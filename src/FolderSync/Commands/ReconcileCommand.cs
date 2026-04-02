using System.CommandLine;
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

        var command = new Command("reconcile", "Run a one-shot reconciliation for all (or a specific) profile, then exit");
        command.Options.Add(configOption);
        command.Options.Add(profileOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var configPath = parseResult.GetValue(configOption);
            var profile = parseResult.GetValue(profileOption);
            await ExecuteAsync(configPath, profile);
        });

        return command;
    }

    private static async Task ExecuteAsync(string? configPath, string? profileName)
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

            foreach (var profile in profiles)
            {
                Log.Information("[{Profile}] Running reconciliation...", profile.Name);

                var options = Options.Create(profile.Options);
                var robocopy = new RobocopyService(processRunner, options, NullLogger<RobocopyService>.Instance);
                var result = await robocopy.ReconcileAsync();

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
}
