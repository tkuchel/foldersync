using System.CommandLine;
using System.Runtime.Versioning;
using FolderSync.Infrastructure;
using FolderSync.Services;

namespace FolderSync.Commands;

public static class ResumeCommand
{
    public static Command Create()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Windows Service name to control",
            DefaultValueFactory = _ => HostBuilderHelper.DefaultServiceName
        };

        var command = new Command("resume", "Resume FolderSync processing");
        command.Options.Add(nameOption);

        command.SetAction(parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("resume"))
                return;

            Execute(parseResult.GetValue(nameOption)!);
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static void Execute(string serviceName)
    {
        if (!PauseCommand.TryResolveInstallDirectory(serviceName, out var installDir, out var error))
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
            controlStore.SetPaused(false);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Access denied writing resume control in {installDir}. Re-run this command from an elevated PowerShell window.");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"Resumed FolderSync at {installDir}");
    }
}
