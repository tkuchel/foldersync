using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json;
using FolderSync.Models;

namespace FolderSync.Commands;

public static class HealthCommand
{
    internal sealed class HealthPayload
    {
        public required string ServiceName { get; init; }
        public required string Status { get; init; }
        public DateTimeOffset? UpdatedAtUtc { get; init; }
        public bool IsPaused { get; init; }
        public string? PauseReason { get; init; }
        public List<HealthProfilePayload> Profiles { get; init; } = [];
    }

    internal sealed class HealthProfilePayload
    {
        public required string Name { get; init; }
        public required string State { get; init; }
        public bool IsPaused { get; init; }
        public string? PauseReason { get; init; }
        public DateTimeOffset? PausedAtUtc { get; init; }
        public long ProcessedCount { get; init; }
        public long FailedCount { get; init; }
        public long WatcherOverflowCount { get; init; }
        public long ConsecutiveFailureCount { get; init; }
        public long ConsecutiveOverflowCount { get; init; }
        public DateTimeOffset? LastSuccessfulSyncUtc { get; init; }
        public DateTimeOffset? LastFailedSyncUtc { get; init; }
        public string? LastFailure { get; init; }
        public string? AlertLevel { get; init; }
        public string? AlertMessage { get; init; }
        public DateTimeOffset? LastAlertUtc { get; init; }
        public ReconciliationHealthSnapshot Reconciliation { get; init; } = new();
    }

    public static Command Create()
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "Windows Service name to query",
            DefaultValueFactory = _ => HostBuilderHelper.DefaultServiceName
        };

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit compact health JSON output"
        };

        var command = new Command("health", "Show compact runtime health for FolderSync");
        command.Options.Add(nameOption);
        command.Options.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            if (!CommandPlatformGuard.EnsureWindows("health"))
                return;

            var name = parseResult.GetValue(nameOption)!;
            var json = parseResult.GetValue(jsonOption);
            Execute(name, json);
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static void Execute(string serviceName, bool json)
    {
        var report = StatusCommand.TryBuildStatusReport(serviceName, out var errorMessage);
        if (report is null)
        {
            if (!string.IsNullOrWhiteSpace(errorMessage))
                Console.Error.WriteLine(errorMessage);
            Environment.ExitCode = 1;
            return;
        }

        var payload = CreateHealthPayload(report);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"{payload.ServiceName}: {payload.Status}");
        foreach (var profile in payload.Profiles)
        {
            Console.WriteLine(
                $"{profile.Name}: state={profile.State}, processed={profile.ProcessedCount}, failed={profile.FailedCount}, overflows={profile.WatcherOverflowCount}, last-sync={profile.LastSuccessfulSyncUtc?.LocalDateTime}");
        }
    }

    internal static HealthPayload CreateHealthPayload(StatusReport report)
    {
        return new HealthPayload
        {
            ServiceName = report.ServiceName,
            Status = report.DisplayState,
            UpdatedAtUtc = report.Runtime?.UpdatedAtUtc,
            IsPaused = report.Control?.IsPaused ?? report.Runtime?.IsPaused ?? false,
            PauseReason = report.Control?.Reason ?? report.Runtime?.PauseReason,
            Profiles = report.Runtime?.Profiles.Select(profile => new HealthProfilePayload
            {
                Name = profile.Name,
                State = profile.State,
                IsPaused = profile.IsPaused,
                PauseReason = profile.PauseReason,
                PausedAtUtc = profile.PausedAtUtc,
                ProcessedCount = profile.ProcessedCount,
                FailedCount = profile.FailedCount,
                WatcherOverflowCount = profile.WatcherOverflowCount,
                ConsecutiveFailureCount = profile.ConsecutiveFailureCount,
                ConsecutiveOverflowCount = profile.ConsecutiveOverflowCount,
                LastSuccessfulSyncUtc = profile.LastSuccessfulSyncUtc,
                LastFailedSyncUtc = profile.LastFailedSyncUtc,
                LastFailure = profile.LastFailure,
                AlertLevel = profile.AlertLevel,
                AlertMessage = profile.AlertMessage,
                LastAlertUtc = profile.LastAlertUtc,
                Reconciliation = profile.Reconciliation
            }).ToList() ?? []
        };
    }
}
