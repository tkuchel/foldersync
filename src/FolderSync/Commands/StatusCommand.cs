using System.Runtime.Versioning;
using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;

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

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Show service health, config, and recent log activity"
        };

        var command = new Command("status", "Show FolderSync Windows Service status");
        command.Options.Add(nameOption);
        command.Options.Add(verboseOption);

        command.SetAction(parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("status"))
                return;

            var name = parseResult.GetValue(nameOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            Execute(name, verbose);
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static void Execute(string serviceName, bool verbose)
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
        string? binPath = null;
        if (qcExitCode == 0)
        {
            binPath = ParseBinPath(qcOutput);
            if (binPath is not null)
                Console.WriteLine($"Path:    {binPath}");
        }

        if (verbose)
        {
            Console.WriteLine();
            PrintVerboseStatus(serviceName, state, binPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PrintVerboseStatus(string serviceName, string? state, string? binPath)
    {
        if (string.IsNullOrWhiteSpace(binPath))
        {
            Console.WriteLine("Verbose: Service binary path unavailable.");
            return;
        }

        var executablePath = NormalizeExecutablePath(binPath);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            Console.WriteLine("Verbose: Installed executable not found.");
            return;
        }

        var installDir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            Console.WriteLine("Verbose: Could not determine install directory.");
            return;
        }

        Console.WriteLine("Health");
        Console.WriteLine($"Install: {installDir}");
        Console.WriteLine($"State:   {state ?? "Unknown"}");

        var version = GetFileVersion(executablePath);
        if (!string.IsNullOrWhiteSpace(version))
            Console.WriteLine($"Version: {version}");

        var configPath = Path.Combine(installDir, "appsettings.json");
        Console.WriteLine($"Config:  {(File.Exists(configPath) ? configPath : "missing")}");

        var profileNames = TryReadProfileNames(configPath);
        if (profileNames.Count > 0)
            Console.WriteLine($"Profiles: {string.Join(", ", profileNames)}");

        var logsDir = Path.Combine(installDir, "logs");
        Console.WriteLine($"Logs:    {(Directory.Exists(logsDir) ? logsDir : "missing")}");

        if (!Directory.Exists(logsDir))
            return;

        var latestLog = Directory.GetFiles(logsDir, "*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latestLog is null)
        {
            Console.WriteLine("Recent log: none");
            return;
        }

        Console.WriteLine($"Recent log: {latestLog.Name} ({latestLog.LastWriteTime})");

        var logLines = ReadTail(latestLog.FullName, 200);
        var lastError = logLines.LastOrDefault(line => line.Contains("[ERR]") || line.Contains("[FTL]"));
        var lastWarning = logLines.LastOrDefault(line => line.Contains("[WRN]"));
        var lastReconcile = logLines.LastOrDefault(line => line.Contains("Reconciliation completed", StringComparison.OrdinalIgnoreCase));
        var lastSync = logLines.LastOrDefault(line => line.Contains("Synced ", StringComparison.OrdinalIgnoreCase));

        if (lastReconcile is not null)
            Console.WriteLine($"Last reconcile: {TrimLogLine(lastReconcile)}");
        if (lastSync is not null)
            Console.WriteLine($"Last sync:      {TrimLogLine(lastSync)}");
        if (lastWarning is not null)
            Console.WriteLine($"Last warning:   {TrimLogLine(lastWarning)}");
        if (lastError is not null)
            Console.WriteLine($"Last error:     {TrimLogLine(lastError)}");
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

    private static string NormalizeExecutablePath(string binPath)
    {
        var trimmed = binPath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
                return trimmed[1..closingQuote];
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
            return trimmed[..(exeIndex + 4)];

        return trimmed;
    }

    private static string? GetFileVersion(string executablePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> TryReadProfileNames(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
                return [];

            using var stream = File.OpenRead(configPath);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("FolderSync", out var folderSync))
                return [];

            if (!folderSync.TryGetProperty("Profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
                return [];

            return profiles.EnumerateArray()
                .Select(profile => profile.TryGetProperty("Name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ReadTail(string path, int maxLines)
    {
        try
        {
            return File.ReadLines(path).TakeLast(maxLines).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string TrimLogLine(string line)
    {
        const int maxLength = 140;
        var trimmed = line.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..(maxLength - 3)] + "...";
    }
}
