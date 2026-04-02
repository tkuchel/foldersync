using FolderSync.Infrastructure;
using FolderSync.Commands;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FolderSync.Tests;

public sealed class ReconciliationServiceTests
{
    [Fact]
    public async Task RunReconciliationAsync_RecordsTriggerExitDescriptionAndSummary()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var healthPath = Path.Combine(tempDir.FullName, "foldersync-health.json");
            var healthStore = new RuntimeHealthStore(healthPath, clock, NullLogger<RuntimeHealthStore>.Instance);
            healthStore.Initialize(["alpha"]);
            healthStore.RecordServiceStarted();
            healthStore.RecordProfileState("alpha", "Running");

            var robocopy = Substitute.For<IRobocopyService>();
            robocopy.ReconcileAsync(Arg.Any<CancellationToken>()).Returns(new RobocopyResult(
                Success: true,
                ExitCode: 3,
                Output: string.Empty,
                ErrorOutput: string.Empty,
                ExitDescription: "Files copied + extras detected",
                Summary: new RobocopySummarySnapshot
                {
                    FilesTotal = 10,
                    FilesCopied = 2,
                    FilesSkipped = 8,
                    FilesExtras = 1,
                    FilesFailed = 0
                }));

            var options = TestOptions.Create(
                "C:\\Source",
                "C:\\Dest",
                o =>
                {
                    o.Reconciliation.Enabled = true;
                    o.Reconciliation.UseRobocopy = true;
                });

            var service = new ReconciliationService(
                "alpha",
                robocopy,
                options,
                healthStore,
                clock,
                NullLogger<ReconciliationService>.Instance);

            clock.Advance(TimeSpan.FromSeconds(2));
            await service.RunReconciliationAsync("Overflow", TestContext.Current.CancellationToken);

            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(healthPath);
            var profile = Assert.Single(snapshot!.Profiles);
            Assert.Equal(1, profile.Reconciliation.RunCount);
            Assert.Equal("Overflow", profile.Reconciliation.LastTrigger);
            Assert.Equal(3, profile.Reconciliation.LastExitCode);
            Assert.Equal("Files copied + extras detected", profile.Reconciliation.LastExitDescription);
            Assert.Equal(2, profile.Reconciliation.LastSummary!.FilesCopied);
            Assert.Equal(1, profile.Reconciliation.LastSummary!.FilesExtras);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
