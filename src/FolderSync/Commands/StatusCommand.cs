using System.Runtime.Versioning;
using System.CommandLine;

namespace FolderSync.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Windows Service name to query",
            DefaultValueFactory = _ => HostBuilderHelper.DefaultServiceName
        };

        var command = new Command("status", "Show FolderSync Windows Service status");
        command.Options.Add(nameOption);

        command.SetAction(parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("status"))
                return;

            var name = parseResult.GetValue(nameOption)!;
            Execute(name);
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static void Execute(string serviceName)
    {
        var (exitCode, output, error) = ServiceHelper.RunSc($"query \"{serviceName}\"");

        if (exitCode != 0)
        {
            if (output.Contains("1060") || error.Contains("1060"))
            {
                Console.WriteLine($"Service '{serviceName}' is not installed.");
                Console.WriteLine();
                Console.WriteLine("To install: foldersync install");
            }
            else
            {
                Console.Error.WriteLine($"Failed to query service (exit code {exitCode}):");
                Console.Error.WriteLine(string.IsNullOrWhiteSpace(error) ? output : error);
                Environment.ExitCode = 1;
            }
            return;
        }

        var state = ParseState(output);
        var displayState = state switch
        {
            "RUNNING" => "Running",
            "STOPPED" => "Stopped",
            "PAUSED" => "Paused",
            "START_PENDING" => "Starting...",
            "STOP_PENDING" => "Stopping...",
            "PAUSE_PENDING" => "Pausing...",
            "CONTINUE_PENDING" => "Resuming...",
            _ => state ?? "Unknown"
        };

        Console.WriteLine($"Service: {serviceName}");
        Console.WriteLine($"Status:  {displayState}");

        var (qcExitCode, qcOutput, _) = ServiceHelper.RunSc($"qc \"{serviceName}\"");
        if (qcExitCode == 0)
        {
            var binPath = ParseBinPath(qcOutput);
            if (binPath is not null)
                Console.WriteLine($"Path:    {binPath}");
        }
    }

    private static string? ParseState(string scOutput)
    {
        foreach (var line in scOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 ? parts[^1] : null;
            }
        }
        return null;
    }

    private static string? ParseBinPath(string qcOutput)
    {
        foreach (var line in qcOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex >= 0 && colonIndex + 1 < trimmed.Length)
                    return trimmed[(colonIndex + 1)..].Trim();
            }
        }
        return null;
    }
}
