using System.Runtime.Versioning;
using System.CommandLine;

namespace FolderSync.Commands;

public static class UninstallCommand
{
    public static Command Create()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Windows Service name to remove",
            DefaultValueFactory = _ => HostBuilderHelper.DefaultServiceName
        };

        var command = new Command("uninstall", "Remove the FolderSync Windows Service");
        command.Options.Add(nameOption);

        command.SetAction(parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("uninstall"))
                return;

            var name = parseResult.GetValue(nameOption)!;
            Execute(name);
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static void Execute(string serviceName)
    {
        if (!ServiceHelper.IsElevated())
        {
            Console.Error.WriteLine("Error: This command requires administrator privileges.");
            Console.Error.WriteLine("Run this command from an elevated command prompt.");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"Stopping service '{serviceName}'...");

        // Try to stop first (ignore errors — may not be running)
        var (stopCode, _, _) = ServiceHelper.RunSc($"stop \"{serviceName}\"");
        if (stopCode == 0)
        {
            Console.WriteLine("Service stopped.");
            Thread.Sleep(2000);
        }

        Console.WriteLine($"Removing service '{serviceName}'...");

        var (exitCode, output, error) = ServiceHelper.RunSc($"delete \"{serviceName}\"");

        if (exitCode == 0)
        {
            Console.WriteLine($"Service '{serviceName}' removed successfully.");
        }
        else
        {
            Console.Error.WriteLine($"Failed to remove service (exit code {exitCode}):");
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(error) ? output : error);
            Environment.ExitCode = 1;
        }
    }
}
