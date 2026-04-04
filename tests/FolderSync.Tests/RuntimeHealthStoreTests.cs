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
            var store = new RuntimeHealthStore(snapshotPath, clock, new FakeAlertNotifier(), NullLogger<RuntimeHealthStore>.Instance);

            store.Initialize(["alpha"]);
            store.RecordServiceStarted();
            store.RecordProfileState("alpha", "Running");
            store.RecordWatcherStarted("alpha");

            var workItem = new SyncWorkItem
            {
                Kind = WatcherChangeKind.Created,
                SourcePath = @"C:\source\file.txt",
                DestinationPath = @"C:\dest\file.txt"
            };

            store.RecordWatcherEventObserved("alpha", new WatcherEvent
            {
                Kind = WatcherChangeKind.Created,
                FullPath = workItem.SourcePath,
                Timestamp = clock.UtcNow,
                IsDirectory = false
            });

            store.RecordSyncResult("alpha", new SyncResult(true, workItem, TimeSpan.FromMilliseconds(12)));
            store.RecordSyncResult("alpha", new SyncResult(true, workItem, TimeSpan.Zero, "Unchanged", IsSkipped: true));
            store.RecordSyncResult("alpha", new SyncResult(true, workItem, TimeSpan.Zero, "File not stable", IsSkipped: true));
            store.RecordWatcherOverflow("alpha");
            store.RecordReconciliationStarted("alpha", "Startup");

            clock.Advance(TimeSpan.FromSeconds(3));
            store.RecordReconciliationCompleted(
                "alpha",
                "Startup",
                new RobocopyResult(
                    Success: true,
                    ExitCode: 2,
                    Output: string.Empty,
                    ErrorOutput: string.Empty,
                    ExitDescription: "Extra files or directories detected",
                    Summary: new RobocopySummarySnapshot
                    {
                        FilesCopied = 5,
                        FilesFailed = 0,
                        FilesExtras = 1
                    }),
                TimeSpan.FromSeconds(3));

            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(snapshotPath);

            Assert.NotNull(snapshot);
            Assert.Equal("Running", snapshot!.ServiceState);
            Assert.Single(snapshot.Profiles);

            var profile = Assert.Single(snapshot.Profiles);
            Assert.Equal("alpha", profile.Name);
            Assert.Equal("Running", profile.State);
            Assert.Equal("Recovering", profile.WatcherState);
            Assert.NotNull(profile.WatcherStartedAtUtc);
            Assert.NotNull(profile.LastWatcherEventUtc);
            Assert.Equal("Created", profile.LastWatcherEventKind);
            Assert.Equal(workItem.SourcePath, profile.LastWatcherPath);
            Assert.NotNull(profile.LastWatcherErrorUtc);
            Assert.Equal("Watcher overflow triggered reconciliation", profile.LastWatcherError);
            Assert.Equal(3, profile.ProcessedCount);
            Assert.Equal(3, profile.SucceededCount);
            Assert.Equal(2, profile.SkippedCount);
            Assert.Equal(0, profile.FailedCount);
            Assert.Equal(1, profile.WatcherOverflowCount);
            Assert.Equal(0, profile.ConsecutiveFailureCount);
            Assert.NotNull(profile.LastSuccessfulSyncUtc);
            Assert.Null(profile.LastFailedSyncUtc);
            Assert.Null(profile.LastFailure);
            Assert.Equal(1, profile.Reconciliation.RunCount);
            Assert.Equal("Startup", profile.Reconciliation.LastTrigger);
            Assert.True(profile.Reconciliation.LastSuccess);
            Assert.Equal(2, profile.Reconciliation.LastExitCode);
            Assert.Equal("Extra files or directories detected", profile.Reconciliation.LastExitDescription);
            Assert.Equal(5, profile.Reconciliation.LastSummary!.FilesCopied);
            Assert.Equal(3000d, profile.Reconciliation.LastDurationMs);
            Assert.NotEmpty(profile.RecentActivities);
            Assert.Contains(profile.RecentActivities, activity => activity.Kind == "sync");
            Assert.Contains(profile.RecentActivities, activity => activity.Kind == "skip");
            Assert.Contains(profile.RecentActivities, activity => activity.Kind == "overflow");
            Assert.Contains(profile.RecentActivities, activity => activity.Kind == "reconcile");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Records_Watcher_Restart_Metadata()
    {
        var clock = new FakeClock();
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var snapshotPath = Path.Combine(tempDir.FullName, "foldersync-health.json");
            var store = new RuntimeHealthStore(snapshotPath, clock, new FakeAlertNotifier(), NullLogger<RuntimeHealthStore>.Instance);
            store.Initialize(["alpha"]);
            store.RecordWatcherStarted("alpha");

            clock.Advance(TimeSpan.FromSeconds(10));
            store.RecordWatcherRestarted("alpha", "The directory name is invalid.");

            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(snapshotPath);
            var profile = Assert.Single(snapshot!.Profiles);

            Assert.Equal("Watching", profile.WatcherState);
            Assert.NotNull(profile.LastWatcherRestartUtc);
            Assert.NotNull(profile.LastWatcherErrorUtc);
            Assert.Equal("The directory name is invalid.", profile.LastWatcherError);
            Assert.Contains(profile.RecentActivities, activity => activity.Kind == "watcher");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Trims_Profile_Activity_History()
    {
        var clock = new FakeClock();
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var snapshotPath = Path.Combine(tempDir.FullName, "foldersync-health.json");
            var store = new RuntimeHealthStore(snapshotPath, clock, new FakeAlertNotifier(), NullLogger<RuntimeHealthStore>.Instance);
            store.Initialize(["alpha"]);

            for (var i = 0; i < 15; i++)
            {
                var workItem = new SyncWorkItem
                {
                    Kind = WatcherChangeKind.Created,
                    SourcePath = $@"C:\source\file-{i}.txt",
                    DestinationPath = $@"C:\dest\file-{i}.txt"
                };

                store.RecordSyncResult("alpha", new SyncResult(true, workItem, TimeSpan.Zero));
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(snapshotPath);
            var profile = Assert.Single(snapshot!.Profiles);

            Assert.Equal(12, profile.RecentActivities.Count);
            Assert.Contains("file-14.txt", profile.RecentActivities[0].Summary);
            Assert.DoesNotContain(profile.RecentActivities, activity => activity.Summary.Contains("file-0.txt", StringComparison.Ordinal));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_DoesNotRaiseAlert_ForRepeatedSkippedUnstableFiles()
    {
        var clock = new FakeClock();
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var snapshotPath = Path.Combine(tempDir.FullName, "foldersync-health.json");
            var notifier = new FakeAlertNotifier();
            var store = new RuntimeHealthStore(snapshotPath, clock, notifier, NullLogger<RuntimeHealthStore>.Instance);
            store.Initialize(["alpha"]);

            var workItem = new SyncWorkItem
            {
                Kind = WatcherChangeKind.Created,
                SourcePath = @"C:\source\file.txt",
                DestinationPath = @"C:\dest\file.txt"
            };

            for (var i = 0; i < 3; i++)
                store.RecordSyncResult("alpha", new SyncResult(true, workItem, TimeSpan.Zero, "File not stable", IsSkipped: true));

            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(snapshotPath);
            var profile = Assert.Single(snapshot!.Profiles);
            Assert.Equal(0, profile.ConsecutiveFailureCount);
            Assert.Null(profile.AlertLevel);
            Assert.Null(profile.AlertMessage);
            Assert.Null(profile.LastAlertUtc);
            Assert.Empty(notifier.Notifications);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
