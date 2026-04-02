using System.Runtime.Versioning;
using System.Text.Json;
using FolderSync.Commands;
using FolderSync.Models;

namespace FolderSync.Tests;

public sealed class StatusCommandTests
{
    [Fact]
    public void SummarizeRecentActivity_PrefersRealErrorsOverWarnings()
    {
        var lines = new[]
        {
            "2026-04-02 20:23:33.883 +11:00 [INF] [cowork-workspace] Pipeline running",
            "2026-04-02 21:15:21.122 +11:00 [WRN] File not stable, skipping: C:\\temp\\example.tmp",
            "2026-04-02 21:15:43.865 +11:00 [WRN] [cowork-workspace] Failed to process \"Created\" for C:\\temp\\example.tmp: File not stable",
            "2026-04-02 21:23:33.943 +11:00 [INF] Reconciliation completed successfully (exit code 2)",
            "2026-04-02 21:25:00.000 +11:00 [ERR] Unhandled exception while processing change set",
            "2026-04-02 21:26:00.000 +11:00 [INF] Synced docs\\notes.md (\"DifferentContent\")"
        };

        var summary = StatusCommand.SummarizeRecentActivity(lines);

        Assert.Equal(lines[3], summary.LastReconcile);
        Assert.Equal(lines[5], summary.LastSync);
        Assert.Equal(lines[0], summary.LastLifecycle);
        Assert.Equal(lines[2], summary.LastWarning);
        Assert.Equal(lines[4], summary.LastError);
    }

    [Fact]
    public void SummarizeRecentActivity_DoesNotPromoteWarningsToErrors()
    {
        var lines = new[]
        {
            "2026-04-02 21:15:21.122 +11:00 [WRN] File not stable, skipping: C:\\temp\\example.tmp",
            "2026-04-02 21:15:43.865 +11:00 [WRN] [cowork-workspace] Failed to process \"Created\" for C:\\temp\\example.tmp: File not stable"
        };

        var summary = StatusCommand.SummarizeRecentActivity(lines);

        Assert.Equal(lines[1], summary.LastWarning);
        Assert.Null(summary.LastError);
    }

    [Fact]
    public void TryReadRuntimeHealthSnapshot_Reads_Shared_Health_File()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-health.json");
            File.WriteAllText(
                path,
                """
                {
                  "serviceName": "FolderSync",
                  "serviceState": "Running",
                  "startedAtUtc": "2026-04-02T10:00:00+00:00",
                  "updatedAtUtc": "2026-04-02T10:05:00+00:00",
                  "profiles": [
                    {
                      "name": "alpha",
                      "state": "Running",
                      "processedCount": 9,
                      "succeededCount": 8,
                      "skippedCount": 2,
                      "failedCount": 1,
                      "watcherOverflowCount": 0,
                      "reconciliation": {
                        "runCount": 3,
                        "lastTrigger": "Overflow",
                        "lastSuccess": true,
                        "lastExitCode": 2
                      }
                    }
                  ]
                }
                """);

            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(path);

            Assert.NotNull(snapshot);
            Assert.Equal("Running", snapshot!.ServiceState);
            var profile = Assert.Single(snapshot.Profiles);
            Assert.Equal("alpha", profile.Name);
            Assert.Equal(9, profile.ProcessedCount);
            Assert.Equal(3, profile.Reconciliation.RunCount);
            Assert.Equal("Overflow", profile.Reconciliation.LastTrigger);
            Assert.True(profile.Reconciliation.LastSuccess);
            Assert.Equal(2, profile.Reconciliation.LastExitCode);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void BuildStatusReport_IncludesRuntimeAndRecentActivity()
    {
        var installDir = Directory.CreateTempSubdirectory();

        try
        {
            var exePath = Path.Combine(installDir.FullName, "foldersync.exe");
            File.WriteAllBytes(exePath, [0x4D, 0x5A]);

            File.WriteAllText(Path.Combine(installDir.FullName, "appsettings.json"), """
            {
              "FolderSync": {
                "Profiles": [
                  { "Name": "alpha" }
                ]
              }
            }
            """);

            var logsDir = Path.Combine(installDir.FullName, "logs");
            Directory.CreateDirectory(logsDir);
            File.WriteAllText(Path.Combine(logsDir, "foldersync-20260402.log"), """
            2026-04-02 22:03:21.045 +11:00 [INF] Reconciliation completed successfully (exit code 2)
            2026-04-02 22:07:24.586 +11:00 [INF] Synced docs\file.txt ("DifferentContent")
            """);

            File.WriteAllText(Path.Combine(installDir.FullName, "foldersync-health.json"), """
            {
              "serviceName": "FolderSync",
              "serviceState": "Running",
              "startedAtUtc": "2026-04-02T10:00:00+00:00",
              "updatedAtUtc": "2026-04-02T10:05:00+00:00",
              "profiles": [
                {
                  "name": "alpha",
                  "state": "Running",
                  "processedCount": 9,
                  "succeededCount": 8,
                  "skippedCount": 2,
                  "failedCount": 1,
                  "watcherOverflowCount": 0,
                  "reconciliation": {
                    "runCount": 3,
                    "lastTrigger": "Overflow",
                    "lastSuccess": true,
                    "lastExitCode": 2,
                    "lastExitDescription": "Extra files or directories detected"
                  }
                }
              ]
            }
            """);

            var report = StatusCommand.BuildStatusReport("FolderSync", "RUNNING", "Running", $"\"{exePath}\"");

            Assert.Equal("FolderSync", report.ServiceName);
            Assert.Equal("Running", report.DisplayState);
            Assert.Equal("alpha", Assert.Single(report.Profiles));
            Assert.NotNull(report.Runtime);
            Assert.Equal(3, report.Runtime!.Profiles[0].Reconciliation.RunCount);
            Assert.NotNull(report.RecentActivity);
            Assert.Contains("Reconciliation completed", report.RecentActivity!.LastReconcile);
            Assert.Contains("Synced docs\\file.txt", report.RecentActivity.LastSync);
        }
        finally
        {
            installDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void HealthPayload_UsesRuntimeProfiles()
    {
        var report = new StatusReport
        {
            ServiceName = "FolderSync",
            DisplayState = "Running",
            Runtime = new RuntimeHealthSnapshot
            {
                ServiceName = "FolderSync",
                ServiceState = "Running",
                StartedAtUtc = DateTimeOffset.Parse("2026-04-02T10:00:00+00:00"),
                UpdatedAtUtc = DateTimeOffset.Parse("2026-04-02T10:05:00+00:00"),
                Profiles =
                [
                    new ProfileHealthSnapshot
                    {
                        Name = "alpha",
                        State = "Running",
                        ProcessedCount = 9,
                        FailedCount = 1,
                        WatcherOverflowCount = 2,
                        ConsecutiveFailureCount = 3,
                        AlertLevel = "warning",
                        AlertMessage = "Profile 'alpha' has 3 consecutive failed sync operations."
                    }
                ]
            }
        };

        var payload = HealthCommand.CreateHealthPayload(report);
        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"ServiceName\":\"FolderSync\"", json);
        Assert.Contains("\"Status\":\"Running\"", json);
        Assert.Contains("\"Name\":\"alpha\"", json);
        Assert.Contains("\"WatcherOverflowCount\":2", json);
        Assert.Contains("\"AlertLevel\":\"warning\"", json);
    }
}
