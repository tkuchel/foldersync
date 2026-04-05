using System.Runtime.Versioning;
using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FolderSync.Models;

namespace FolderSync.Commands;

public static class StatusCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal sealed record RecentActivitySummary(
        string? LastReconcile,
        string? LastSync,
        string? LastLifecycle,
        string? LastWarning,
        string? LastError);

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

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit structured JSON status output"
        };

        var command = new Command("status", "Show FolderSync Windows Service status");
        command.Options.Add(nameOption);
        command.Options.Add(verboseOption);
        command.Options.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("status"))
                return;

            var name = parseResult.GetValue(nameOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var json = parseResult.GetValue(jsonOption);
            Execute(name, verbose, json);
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    internal static StatusReport? TryBuildStatusReport(string serviceName, out string? errorMessage)
    {
        var (exitCode, output, error) = ServiceHelper.RunSc($"query \"{serviceName}\"");
        errorMessage = null;

        if (exitCode != 0)
        {
            if (output.Contains("1060") || error.Contains("1060"))
            {
                errorMessage = $"Service '{serviceName}' is not installed.";
            }
            else
            {
                errorMessage = $"Failed to query service (exit code {exitCode}): {(string.IsNullOrWhiteSpace(error) ? output : error)}";
            }
            return null;
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

        var (qcExitCode, qcOutput, _) = ServiceHelper.RunSc($"qc \"{serviceName}\"");
        string? binPath = null;
        if (qcExitCode == 0)
            binPath = ParseBinPath(qcOutput);
        
        return BuildStatusReport(serviceName, state, displayState, binPath);
    }

    [SupportedOSPlatform("windows")]
    private static void Execute(string serviceName, bool verbose, bool json)
    {
        var report = TryBuildStatusReport(serviceName, out var errorMessage);
        if (report is null)
        {
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                if (errorMessage.Contains("not installed", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(errorMessage);
                    Console.WriteLine();
                    Console.WriteLine("To install: foldersync install");
                }
                else
                {
                    Console.Error.WriteLine(errorMessage);
                    Environment.ExitCode = 1;
                }
            }

            return;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"Service: {report.ServiceName}");
        Console.WriteLine($"Status:  {report.DisplayState}");

        if (!string.IsNullOrWhiteSpace(report.BinaryPath))
            Console.WriteLine($"Path:    {report.BinaryPath}");

        if (verbose)
        {
            Console.WriteLine();
            PrintVerboseStatus(report);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static StatusReport BuildStatusReport(string serviceName, string? state, string displayState, string? binPath)
    {
        var report = new StatusReport
        {
            ServiceName = serviceName,
            RawState = state,
            DisplayState = displayState,
            BinaryPath = binPath
        };

        if (string.IsNullOrWhiteSpace(binPath))
            return report;

        var executablePath = NormalizeExecutablePath(binPath);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return report;

        var installDir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(installDir))
            return report;

        report.InstallDirectory = installDir;
        report.Version = GetFileVersion(executablePath);

        var configPath = Path.Combine(installDir, "appsettings.json");
        report.ConfigPath = File.Exists(configPath) ? configPath : null;
        var controlPath = Path.Combine(installDir, "foldersync-control.json");
        report.Control = TryReadRuntimeControlSnapshot(controlPath);
        report.Profiles = TryReadProfileNames(configPath);
        report.TwoWayPreviewStatuses = TryReadTwoWayPreviewStatuses(configPath, installDir);

        var logsDir = Path.Combine(installDir, "logs");
        report.LogsDirectory = Directory.Exists(logsDir) ? logsDir : null;

        var healthPath = Path.Combine(installDir, "foldersync-health.json");
        report.Runtime = TryReadRuntimeHealthSnapshot(healthPath);
        ApplyControlOverlay(report.Control, report.Runtime);

        if (!Directory.Exists(logsDir))
            return report;

        var latestLog = Directory.GetFiles(logsDir, "*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latestLog is null)
            return report;

        report.RecentLog = new LogFileReport
        {
            Name = latestLog.Name,
            Path = latestLog.FullName,
            LastWriteTime = latestLog.LastWriteTime
        };

        var logLines = ReadTail(latestLog.FullName, 200);
        if (logLines.Count == 0)
            return report;

        var activity = SummarizeRecentActivity(logLines);
        report.RecentActivity = new RecentActivityReport
        {
            LastReconcile = activity.LastReconcile,
            LastSync = activity.LastSync,
            LastLifecycle = activity.LastLifecycle,
            LastWarning = activity.LastWarning,
            LastError = activity.LastError
        };

        return report;
    }

    private static void ApplyControlOverlay(RuntimeControlSnapshot? control, RuntimeHealthSnapshot? runtime)
    {
        if (control is null || runtime is null)
            return;

        runtime.IsPaused = control.IsPaused;
        runtime.PauseReason = control.IsPaused ? control.Reason : null;
        runtime.PausedAtUtc = control.IsPaused ? control.ChangedAtUtc : null;

        foreach (var profile in runtime.Profiles)
        {
            var effectivePause = control.GetEffectivePause(profile.Name);
            profile.IsPaused = effectivePause is not null;
            profile.PauseReason = effectivePause?.Reason;
            profile.PausedAtUtc = effectivePause?.ChangedAtUtc;

            if (profile.IsPaused)
                profile.State = "Paused";
        }
    }

    private static void PrintVerboseStatus(StatusReport report)
    {
        if (string.IsNullOrWhiteSpace(report.InstallDirectory))
        {
            Console.WriteLine("Verbose: Installed executable not found.");
            return;
        }

        Console.WriteLine("Health");
        Console.WriteLine($"Install: {report.InstallDirectory}");
        Console.WriteLine($"State:   {report.RawState ?? "Unknown"}");

        if (!string.IsNullOrWhiteSpace(report.Version))
            Console.WriteLine($"Version: {report.Version}");

        Console.WriteLine($"Config:  {report.ConfigPath ?? "missing"}");
        if (report.Control?.IsPaused is true)
            Console.WriteLine($"Control: paused ({report.Control.Reason ?? "no reason"})");
        else
            Console.WriteLine("Control: active");

        var pausedProfiles = report.Control?.Profiles
            .Where(profile => profile.IsPaused)
            .Select(profile => $"{profile.Name} ({profile.Reason ?? "no reason"})")
            .ToList();
        if (pausedProfiles is { Count: > 0 })
            Console.WriteLine($"Paused profiles: {string.Join(", ", pausedProfiles)}");

        var pendingReconcileRequests = report.Control?.ReconcileRequests
            .OrderBy(request => request.RequestedAtUtc)
            .Select(request => $"{request.ProfileName} ({request.Trigger} @ {request.RequestedAtUtc.LocalDateTime})")
            .ToList();
        if (pendingReconcileRequests is { Count: > 0 })
            Console.WriteLine($"Pending reconcile requests: {string.Join(", ", pendingReconcileRequests)}");

        if (report.Profiles.Count > 0)
            Console.WriteLine($"Profiles: {string.Join(", ", report.Profiles)}");

        Console.WriteLine($"Logs:    {report.LogsDirectory ?? "missing"}");
        PrintRuntimeMetrics(report.Runtime, report.Control, report.RawState);

        if (report.RecentLog is null)
        {
            Console.WriteLine("Recent log: none");
            return;
        }

        Console.WriteLine($"Recent log: {report.RecentLog.Name} ({report.RecentLog.LastWriteTime.LocalDateTime})");

        if (report.RecentActivity is null)
        {
            Console.WriteLine("Recent activity: unavailable");
            return;
        }

        if (report.RecentActivity.LastReconcile is not null)
            Console.WriteLine($"Last reconcile: {TrimLogLine(report.RecentActivity.LastReconcile)}");
        if (report.RecentActivity.LastSync is not null)
            Console.WriteLine($"Last sync:      {TrimLogLine(report.RecentActivity.LastSync)}");
        if (report.RecentActivity.LastLifecycle is not null)
            Console.WriteLine($"Last lifecycle: {TrimLogLine(report.RecentActivity.LastLifecycle)}");
        if (report.RecentActivity.LastWarning is not null)
            Console.WriteLine($"Last warning:   {TrimLogLine(report.RecentActivity.LastWarning)}");
        if (report.RecentActivity.LastError is not null)
            Console.WriteLine($"Last error:     {TrimLogLine(report.RecentActivity.LastError)}");
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

    private static List<TwoWayPreviewStatus> TryReadTwoWayPreviewStatuses(string configPath, string installDir)
    {
        try
        {
            if (!File.Exists(configPath))
                return [];

            using var stream = File.OpenRead(configPath);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty(FolderSyncConfig.SectionName, out var folderSync))
                return [];

            var config = folderSync.Deserialize<FolderSyncConfig>(JsonOptions);
            if (config is null)
                return [];

            return config.Profiles
                .Select(profile =>
                {
                    var syncMode = profile.SyncMode ?? SyncMode.OneWay;
                    var stateStorePath = profile.TwoWay?.StateStorePath;
                    if (string.IsNullOrWhiteSpace(stateStorePath) && syncMode != SyncMode.OneWay)
                        stateStorePath = Path.Combine(installDir, "state", $"{profile.Name}.twoway.json");

                    var snapshot = !string.IsNullOrWhiteSpace(stateStorePath) && File.Exists(stateStorePath)
                        ? TryReadTwoWayStateSnapshot(stateStorePath)
                        : null;

                    return new TwoWayPreviewStatus
                    {
                        ProfileName = profile.Name,
                        SyncMode = syncMode.ToString(),
                        StateStorePath = stateStorePath,
                        UpdatedAtUtc = snapshot?.UpdatedAtUtc,
                        ConflictCount = snapshot?.Conflicts.Count ?? 0,
                        AcknowledgedConflictCount = snapshot?.Conflicts.Count(conflict => conflict.IsAcknowledged) ?? 0,
                        Conflicts = snapshot?.Conflicts
                            .OrderByDescending(conflict => conflict.DetectedAtUtc)
                            .Select(conflict => new TwoWayConflictSummary
                            {
                                RelativePath = conflict.RelativePath,
                                Reason = conflict.Reason,
                                DetectedAtUtc = conflict.DetectedAtUtc,
                                RecommendedMode = conflict.RecommendedMode.ToString(),
                                IsAcknowledged = conflict.IsAcknowledged,
                                AcknowledgedAtUtc = conflict.AcknowledgedAtUtc
                            })
                            .ToList() ?? []
                    };
                })
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
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var lines = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == maxLines)
                    lines.Dequeue();
                lines.Enqueue(line);
            }

            return lines.ToList();
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

    private static bool ContainsAny(string line, params string[] patterns)
    {
        return patterns.Any(pattern => line.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static void PrintRuntimeMetrics(RuntimeHealthSnapshot? snapshot, RuntimeControlSnapshot? control, string? rawState)
    {
        if (snapshot is null)
            return;

        Console.WriteLine($"Runtime: {snapshot.ServiceState} (updated {snapshot.UpdatedAtUtc.LocalDateTime})");
        var serviceAvailable = string.Equals(rawState, "RUNNING", StringComparison.OrdinalIgnoreCase) || control?.IsPaused is true;

        foreach (var profile in snapshot.Profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Profile {profile.Name}:");
            Console.WriteLine(
                $"  state={profile.State}, processed={profile.ProcessedCount}, succeeded={profile.SucceededCount}, skipped={profile.SkippedCount}, failed={profile.FailedCount}, overflows={profile.WatcherOverflowCount}");
            Console.WriteLine($"  watcher={profile.WatcherState}");
            if (profile.WatcherStartedAtUtc is not null)
                Console.WriteLine($"  watcher started={profile.WatcherStartedAtUtc.Value.LocalDateTime}");
            if (profile.LastWatcherEventUtc is not null)
                Console.WriteLine($"  watcher last event={profile.LastWatcherEventUtc.Value.LocalDateTime} ({profile.LastWatcherEventKind ?? "Unknown"})");
            if (profile.LastWatcherRestartUtc is not null)
                Console.WriteLine($"  watcher last restart={profile.LastWatcherRestartUtc.Value.LocalDateTime}");
            if (profile.LastWatcherErrorUtc is not null && !string.IsNullOrWhiteSpace(profile.LastWatcherError))
                Console.WriteLine($"  watcher last error={profile.LastWatcherErrorUtc.Value.LocalDateTime}: {profile.LastWatcherError}");
            if (profile.IsPaused)
                Console.WriteLine($"  pause={profile.PauseReason ?? "Paused by operator"}");
            if (!string.IsNullOrWhiteSpace(profile.AlertMessage))
                Console.WriteLine($"  alert={profile.AlertLevel}: {profile.AlertMessage}");

            if (profile.LastSuccessfulSyncUtc is not null)
                Console.WriteLine($"  last sync={profile.LastSuccessfulSyncUtc.Value.LocalDateTime}");
            if (profile.LastFailedSyncUtc is not null)
                Console.WriteLine($"  last failure={profile.LastFailedSyncUtc.Value.LocalDateTime}: {profile.LastFailure}");

            if (profile.Reconciliation.RunCount > 0)
            {
                var reconciliation = profile.Reconciliation;
                var pendingRequests = control?.ReconcileRequests.Count(request =>
                    string.Equals(request.ProfileName, profile.Name, StringComparison.OrdinalIgnoreCase)) ?? 0;
                var status = !serviceAvailable
                    ? "service unavailable"
                    : reconciliation.IsRunning
                        ? $"running ({reconciliation.CurrentTrigger ?? reconciliation.LastTrigger ?? "unknown"})"
                        : pendingRequests > 0
                            ? $"queued ({pendingRequests})"
                            : reconciliation.LastSuccess is true ? "idle (last success)" : "idle (last failure)";
                var duration = reconciliation.LastDurationMs is null
                    ? "n/a"
                    : $"{TimeSpan.FromMilliseconds(reconciliation.LastDurationMs.Value).TotalSeconds:F1}s";
                Console.WriteLine(
                    $"  reconcile={status}, runs={reconciliation.RunCount}, trigger={reconciliation.LastTrigger}, exit={reconciliation.LastExitCode}, duration={duration}");
                if (!string.IsNullOrWhiteSpace(reconciliation.LastExitDescription))
                    Console.WriteLine($"  reconcile note={reconciliation.LastExitDescription}");
                if (reconciliation.LastSummary?.FilesCopied is not null)
                    Console.WriteLine($"  reconcile files copied={reconciliation.LastSummary.FilesCopied}, extras={reconciliation.LastSummary.FilesExtras}, failed={reconciliation.LastSummary.FilesFailed}");
                if (reconciliation.LastCompletedAtUtc is not null)
                    Console.WriteLine($"  last reconcile={reconciliation.LastCompletedAtUtc.Value.LocalDateTime}");
            }
            else
            {
                var pendingRequests = control?.ReconcileRequests.Count(request =>
                    string.Equals(request.ProfileName, profile.Name, StringComparison.OrdinalIgnoreCase)) ?? 0;
                var status = !serviceAvailable
                    ? "service unavailable"
                    : pendingRequests > 0 ? $"queued ({pendingRequests})" : "idle";
                Console.WriteLine($"  reconcile={status}, runs=0");
            }
        }
    }

    internal static RuntimeHealthSnapshot? TryReadRuntimeHealthSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return JsonSerializer.Deserialize<RuntimeHealthSnapshot>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    internal static RuntimeControlSnapshot? TryReadRuntimeControlSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new RuntimeControlSnapshot();

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return JsonSerializer.Deserialize<RuntimeControlSnapshot>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    internal static TwoWayStateSnapshot? TryReadTwoWayStateSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return JsonSerializer.Deserialize<TwoWayStateSnapshot>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    internal static RecentActivitySummary SummarizeRecentActivity(IEnumerable<string> logLines)
    {
        var lines = logLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        return new RecentActivitySummary(
            LastReconcile: lines.LastOrDefault(line => ContainsAny(line, "Reconciliation completed", "Starting reconciliation")),
            LastSync: lines.LastOrDefault(line => ContainsAny(line, "Synced ", "Copied ")),
            LastLifecycle: lines.LastOrDefault(line =>
                line.Contains("FolderSync starting", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("FolderSync shutting down", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Pipeline running", StringComparison.OrdinalIgnoreCase)),
            LastWarning: lines.LastOrDefault(IsWarningLine),
            LastError: lines.LastOrDefault(IsErrorLine));
    }

    private static bool IsWarningLine(string line)
    {
        return line.Contains("[WRN]", StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(line, "warning", "skipping");
    }

    private static bool IsErrorLine(string line)
    {
        if (line.Contains("[ERR]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[FTL]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains("[WRN]", StringComparison.OrdinalIgnoreCase))
            return false;

        return Regex.IsMatch(
            line,
            @"\b(error|exception|fatal|crash(ed)?|unhandled)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
