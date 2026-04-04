using System.CommandLine;
using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Commands;

public static class ValidateConfigCommand
{
    internal sealed record ValidationExecutionResult(
        int ProfileCount,
        List<string> Warnings,
        List<string> Errors,
        bool Strict);

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

        var strictOption = new Option<bool>("--strict")
        {
            Description = "Treat risky settings and operator warnings as validation errors"
        };

        var command = new Command("validate-config", "Validate FolderSync configuration and profile safety checks");
        command.Options.Add(configOption);
        command.Options.Add(profileOption);
        command.Options.Add(strictOption);

        command.SetAction(parseResult =>
        {
            var configPath = parseResult.GetValue(configOption);
            var profileName = parseResult.GetValue(profileOption);
            var strict = parseResult.GetValue(strictOption);
            Execute(configPath, profileName, strict);
        });

        return command;
    }

    private static void Execute(string? configPath, string? profileName, bool strict)
    {
        try
        {
            var result = ValidateConfiguration(configPath, profileName, strict);

            foreach (var warning in result.Warnings)
                Console.WriteLine($"Warning: {warning}");

            foreach (var error in result.Errors)
                Console.Error.WriteLine($"Error: {error}");

            if (result.Errors.Count > 0)
            {
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Configuration valid for {result.ProfileCount} profile(s).");
            if (result.Warnings.Count > 0)
                Console.WriteLine($"Completed with {result.Warnings.Count} warning(s).");
            if (result.Strict)
                Console.WriteLine("Strict validation enabled.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to validate configuration. {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    internal static ValidationExecutionResult ValidateConfiguration(string? configPath, string? profileName, bool strict)
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
                return new ValidationExecutionResult(0, [], [$"Profile '{profileName}' not found."], strict);
        }

        var validation = ProfileConfigurationValidator.Validate(
            profiles,
            new ProfileValidationOptions { Strict = strict });

        return new ValidationExecutionResult(
            profiles.Count,
            validation.Warnings.Select(issue => issue.Message).ToList(),
            validation.Errors.Select(issue => issue.Message).ToList(),
            strict);
    }
}
