using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using FolderSync.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSync.Tests;

public sealed class RuntimeHealthStoreTests
{
    [Fact]
    public void Store_Persists_Profile_Counters_And_Reconciliation_State()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.Zero));

        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var snapshotPath = Path.Combine(tempDir.FullName, "foldersync-health.json");
            var store = new RuntimeHealthStore(snapshotPath, clock, NullLogger<RuntimeHealthStore>.Instance);

            store.Initialize(["alpha"]);
            store.RecordServiceStarted();
            store.RecordProfileState("alpha", "Running");

            var workItem = new SyncWorkItem
            {
                Kind = WatcherChangeKind.Created,
                SourcePath = @"C:\source\file.txt",
                DestinationPath = @"C:\dest\file.txt"
            };

            store.RecordSyncResult("alpha", new SyncResult(true, workItem, TimeSpan.FromMilliseconds(12)));
            store.RecordSyncResult("alpha", new SyncResult(true, workItem, TimeSpan.Zero, "Unchanged", IsSkipped: true));
            store.RecordSyncResult("alpha", new SyncResult(false, workItem, TimeSpan.Zero, "File not stable", IsSkipped: true));
            store.RecordWatcherOverflow("alpha");
            store.RecordReconciliationStarted("alpha", "Startup");

            clock.Advance(TimeSpan.FromSeconds(3));
            store.RecordReconciliationCompleted("alpha", "Startup", success: true, exitCode: 2, duration: TimeSpan.FromSeconds(3));

            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(snapshotPath);

            Assert.NotNull(snapshot);
            Assert.Equal("Running", snapshot!.ServiceState);
            Assert.Single(snapshot.Profiles);

            var profile = Assert.Single(snapshot.Profiles);
            Assert.Equal("alpha", profile.Name);
            Assert.Equal("Running", profile.State);
            Assert.Equal(3, profile.ProcessedCount);
            Assert.Equal(2, profile.SucceededCount);
            Assert.Equal(2, profile.SkippedCount);
            Assert.Equal(1, profile.FailedCount);
            Assert.Equal(1, profile.WatcherOverflowCount);
            Assert.NotNull(profile.LastSuccessfulSyncUtc);
            Assert.NotNull(profile.LastFailedSyncUtc);
            Assert.Equal("File not stable", profile.LastFailure);
            Assert.Equal(1, profile.Reconciliation.RunCount);
            Assert.Equal("Startup", profile.Reconciliation.LastTrigger);
            Assert.True(profile.Reconciliation.LastSuccess);
            Assert.Equal(2, profile.Reconciliation.LastExitCode);
            Assert.Equal(3000d, profile.Reconciliation.LastDurationMs);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
