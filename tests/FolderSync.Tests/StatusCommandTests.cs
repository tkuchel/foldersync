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
}
