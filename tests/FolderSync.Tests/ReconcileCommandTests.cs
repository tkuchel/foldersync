using System.Text.Json;
using FolderSync.Commands;
using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Tests;

public sealed class ReconcileCommandTests
{
    [Fact]
    public void RecordReconciliationCompleted_AppendsCompletionWithoutResettingExistingSnapshot()
    {
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var snapshotPath = Path.Combine(tempDir.FullName, "foldersync-health.json");
            var startedAt = new DateTimeOffset(2026, 4, 3, 3, 0, 0, TimeSpan.Zero);
            var completedAt = startedAt.AddSeconds(4);
            var snapshot = new RuntimeHealthSnapshot
            {
                ServiceName = HostBuilderHelper.DefaultServiceName,
                ServiceState = "Running",
                StartedAtUtc = startedAt.AddHours(-2),
                UpdatedAtUtc = startedAt,
                Profiles =
                [
                    new ProfileHealthSnapshot
                    {
                        Name = "alpha",
                        State = "Running",
                        ProcessedCount = 17,
                        FailedCount = 2,
                        RecentActivities =
                        [
                            new ProfileActivitySnapshot
                            {
                                Kind = "reconcile",
                                Summary = "Reconciliation requested from dashboard",
                                TimestampUtc = startedAt,
                                Details = "Requested from dashboard"
                            }
                        ]
                    }
                ]
            };

            File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));

            ReconcileCommand.RecordReconciliationStarted(snapshotPath, "alpha", "Dashboard", startedAt);
            ReconcileCommand.RecordReconciliationCompleted(
                snapshotPath,
                "alpha",
                "Dashboard",
                new RobocopyResult(
                    Success: true,
                    ExitCode: 2,
                    Output: string.Empty,
                    ErrorOutput: string.Empty,
                    ExitDescription: "Extra files or directories detected",
                    Summary: new RobocopySummarySnapshot
                    {
                        DirectoriesTotal = 3,
                        DirectoriesExtras = 1,
                        FilesTotal = 10,
                        FilesSkipped = 10
                    }),
                TimeSpan.FromSeconds(4),
                completedAt);

            var persisted = StatusCommand.TryReadRuntimeHealthSnapshot(snapshotPath);
            var profile = Assert.Single(persisted!.Profiles);
            Assert.Equal(17, profile.ProcessedCount);
            Assert.Equal(2, profile.FailedCount);
            Assert.Equal("Dashboard", profile.Reconciliation.LastTrigger);
            Assert.Equal(1, profile.Reconciliation.RunCount);
            Assert.Equal(2, profile.Reconciliation.LastExitCode);
            Assert.Equal("Extra files or directories detected", profile.Reconciliation.LastExitDescription);
            Assert.Equal(1, profile.Reconciliation.LastSummary!.DirectoriesExtras);
            Assert.Contains(profile.RecentActivities, activity => activity.Summary == "Reconciliation requested from dashboard");
            Assert.Contains(profile.RecentActivities, activity => activity.Summary == "Reconciliation started (Dashboard)");
            Assert.Contains(profile.RecentActivities, activity => activity.Summary == "Reconciliation completed (2)");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
