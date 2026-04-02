using System.Runtime.Versioning;
using System.CommandLine;

namespace FolderSync.Commands;

public static class InstallCommand
{
    public static Command Create()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Windows Service name",
            DefaultValueFactory = _ => HostBuilderHelper.DefaultServiceName
        };

        var displayNameOption = new Option<string>("--display-name")
        {
            Description = "Windows Service display name",
            DefaultValueFactory = _ => HostBuilderHelper.DefaultDisplayName
        };

        var command = new Command("install", "Install FolderSync as a Windows Service");
        command.Options.Add(nameOption);
        command.Options.Add(displayNameOption);

        command.SetAction(parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("install"))
                return;

            var name = parseResult.GetValue(nameOption)!;
            var displayName = parseResult.GetValue(displayNameOption)!;
            Execute(name, displayName);
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static void Execute(string serviceName, string displayName)
    {
        if (!ServiceHelper.IsElevated())
        {
            Console.Error.WriteLine("Error: This command requires administrator privileges.");
            Console.Error.WriteLine("Run this command from an elevated command prompt.");
            Environment.ExitCode = 1;
            return;
        }

        var exePath = ServiceHelper.GetExePath();

        Console.WriteLine($"Installing service '{serviceName}'...");
        Console.WriteLine($"  Executable: {exePath}");

        // Create the service
        var args = $"create \"{serviceName}\" binPath= \"\\\"{exePath}\\\"\" start= auto DisplayName= \"{displayName}\"";
        var (exitCode, output, error) = ServiceHelper.RunSc(args);

        if (exitCode == 0)
        {
            Console.WriteLine($"Service '{serviceName}' installed successfully.");
            Console.WriteLine();
            Console.WriteLine("To configure, edit appsettings.json in the application directory.");
            Console.WriteLine($"To start:   sc.exe start {serviceName}");
            Console.WriteLine($"To stop:    sc.exe stop {serviceName}");

            // Set description
            ServiceHelper.RunSc($"description \"{serviceName}\" \"One-way folder synchronisation service with file watching and periodic reconciliation.\"");

            // Configure recovery: restart on first and second failure
            ServiceHelper.RunSc($"failure \"{serviceName}\" reset= 86400 actions= restart/60000/restart/60000/\"\"");
        }
        else
        {
            Console.Error.WriteLine($"Failed to install service (exit code {exitCode}):");
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(error) ? output : error);
            Environment.ExitCode = 1;
        }
    }
}
