using FolderSync.Services;
using FolderSync.Tests.Helpers;

namespace FolderSync.Tests;

public sealed class RuntimeControlStoreTests
{
    [Fact]
    public void Store_Persists_Pause_State_And_Reason()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 2, 10, 30, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var store = new RuntimeControlStore(path, clock);

            store.SetPaused(true, "Maintenance window");
            var paused = store.Read();

            Assert.True(paused.IsPaused);
            Assert.Equal("Maintenance window", paused.Reason);
            Assert.Equal(clock.UtcNow, paused.ChangedAtUtc);

            clock.Advance(TimeSpan.FromMinutes(5));
            store.SetPaused(false);
            var resumed = store.Read();

            Assert.False(resumed.IsPaused);
            Assert.Null(resumed.Reason);
            Assert.Equal(clock.UtcNow, resumed.ChangedAtUtc);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Persists_Profile_Specific_Pause_State()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 2, 11, 0, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var store = new RuntimeControlStore(path, clock);

            store.SetProfilePaused("alpha", true, "Index rebuild");
            var paused = store.Read();
            var profile = Assert.Single(paused.Profiles);

            Assert.Equal("alpha", profile.Name);
            Assert.True(profile.IsPaused);
            Assert.Equal("Index rebuild", profile.Reason);
            Assert.Equal(clock.UtcNow, profile.ChangedAtUtc);
            Assert.False(paused.IsPaused);

            clock.Advance(TimeSpan.FromMinutes(2));
            store.SetProfilePaused("alpha", false);
            var resumed = store.Read();

            Assert.Empty(resumed.Profiles);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Serializes_Global_And_Profile_Updates_Through_Shared_File()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 2, 11, 30, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var firstStore = new RuntimeControlStore(path, clock);
            var secondStore = new RuntimeControlStore(path, clock);

            firstStore.SetProfilePaused("alpha", true, "Index rebuild");
            clock.Advance(TimeSpan.FromMinutes(1));
            secondStore.SetPaused(true, "Maintenance window");

            var snapshot = firstStore.Read();

            Assert.True(snapshot.IsPaused);
            Assert.Equal("Maintenance window", snapshot.Reason);
            var profile = Assert.Single(snapshot.Profiles);
            Assert.Equal("alpha", profile.Name);
            Assert.True(profile.IsPaused);
            Assert.Equal("Index rebuild", profile.Reason);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Queues_And_Dequeues_Profile_Reconcile_Requests()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var store = new RuntimeControlStore(path, clock);

            store.EnqueueReconcileRequest("alpha", "Dashboard");
            store.EnqueueReconcileRequests(["beta", "gamma"], "Tray");

            var first = store.TryDequeueReconcileRequest("alpha");
            var second = store.TryDequeueReconcileRequest("beta");
            var third = store.TryDequeueReconcileRequest("gamma");
            var none = store.TryDequeueReconcileRequest("alpha");

            Assert.NotNull(first);
            Assert.Equal("alpha", first!.ProfileName);
            Assert.Equal("Dashboard", first.Trigger);
            Assert.NotNull(second);
            Assert.Equal("beta", second!.ProfileName);
            Assert.Equal("Tray", second.Trigger);
            Assert.NotNull(third);
            Assert.Equal("gamma", third!.ProfileName);
            Assert.Null(none);
            Assert.Empty(store.Read().ReconcileRequests);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
