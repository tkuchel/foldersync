using System.CommandLine;
using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Commands;

public static class ValidateConfigCommand
{
    public static Command Create()
    {
        var configOption = new Option<string?>("--config")
        {
            Description = "Path to custom appsettings.json file"
        };

        var profileOption = new Option<string?>("--profile")
        {
            Description = "Validate a specific profile only"
        };

        var command = new Command("validate-config", "Validate FolderSync configuration and profile safety checks");
        command.Options.Add(configOption);
        command.Options.Add(profileOption);

        command.SetAction(parseResult =>
        {
            var configPath = parseResult.GetValue(configOption);
            var profileName = parseResult.GetValue(profileOption);
            Execute(configPath, profileName);
        });

        return command;
    }

    private static void Execute(string? configPath, string? profileName)
    {
        try
        {
            var resolvedConfigPath = !string.IsNullOrWhiteSpace(configPath)
                ? Path.GetFullPath(configPath)
                : null;

            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false);

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
                    Console.Error.WriteLine($"Error: Profile '{profileName}' not found.");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            var validation = ProfileConfigurationValidator.Validate(profiles);

            foreach (var warning in validation.Warnings)
                Console.WriteLine($"Warning: {warning.Message}");

            foreach (var error in validation.Errors)
                Console.Error.WriteLine($"Error: {error.Message}");

            if (validation.HasErrors)
            {
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Configuration valid for {profiles.Count} profile(s).");
            if (validation.Warnings.Count > 0)
                Console.WriteLine($"Completed with {validation.Warnings.Count} warning(s).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to validate configuration. {ex.Message}");
            Environment.ExitCode = 1;
        }
    }
}
