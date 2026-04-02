using System.CommandLine;
using System.Runtime.Versioning;
using FolderSync.Infrastructure;
using FolderSync.Services;

namespace FolderSync.Commands;

public static class PauseCommand
{
    public static Command Create()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Windows Service name to control",
            DefaultValueFactory = _ => HostBuilderHelper.DefaultServiceName
        };

        var reasonOption = new Option<string?>("--reason")
        {
            Description = "Optional pause reason"
        };

        var profileOption = new Option<string?>("--profile")
        {
            Description = "Optional profile name to pause without pausing the whole service"
        };

        var command = new Command("pause", "Pause FolderSync processing without stopping the service");
        command.Options.Add(nameOption);
        command.Options.Add(reasonOption);
        command.Options.Add(profileOption);

        command.SetAction(parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("pause"))
                return;

            Execute(
                parseResult.GetValue(nameOption)!,
                parseResult.GetValue(reasonOption),
                parseResult.GetValue(profileOption));
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static void Execute(string serviceName, string? reason, string? profileName)
    {
        if (!TryResolveInstallDirectory(serviceName, out var installDir, out var error))
        {
            Console.Error.WriteLine(error);
            Environment.ExitCode = 1;
            return;
        }

        var controlStore = new RuntimeControlStore(
            Path.Combine(installDir!, "foldersync-control.json"),
            new SystemClock());

        try
        {
            var pauseReason = string.IsNullOrWhiteSpace(reason) ? "Paused by operator" : reason;
            if (string.IsNullOrWhiteSpace(profileName))
                controlStore.SetPaused(true, pauseReason);
            else
                controlStore.SetProfilePaused(profileName, true, pauseReason);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Access denied writing pause control in {installDir}. Re-run this command from an elevated PowerShell window.");
            Environment.ExitCode = 1;
            return;
        }

        if (string.IsNullOrWhiteSpace(profileName))
            Console.WriteLine($"Paused FolderSync at {installDir}");
        else
            Console.WriteLine($"Paused profile '{profileName}' at {installDir}");
    }

    [SupportedOSPlatform("windows")]
    internal static bool TryResolveInstallDirectory(string serviceName, out string? installDir, out string? error)
    {
        installDir = null;
        error = null;

        var report = StatusCommand.TryBuildStatusReport(serviceName, out error);
        if (report?.InstallDirectory is null)
            return false;

        installDir = report.InstallDirectory;
        return true;
    }
}
