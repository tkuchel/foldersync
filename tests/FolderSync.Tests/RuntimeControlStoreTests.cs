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

    [Fact]
    public void Store_Coalesces_Repeated_Reconcile_Requests_For_The_Same_Profile()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 5, 9, 0, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var store = new RuntimeControlStore(path, clock);

            store.EnqueueReconcileRequest("alpha", "Dashboard");
            clock.Advance(TimeSpan.FromSeconds(10));
            store.EnqueueReconcileRequest("alpha", "Tray");

            var snapshot = store.Read();
            var request = Assert.Single(snapshot.ReconcileRequests);
            Assert.Equal("alpha", request.ProfileName);
            Assert.Equal("Dashboard", request.Trigger);
            Assert.Equal(new DateTimeOffset(2026, 4, 5, 9, 0, 0, TimeSpan.Zero), request.RequestedAtUtc);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Coalesces_Repeated_Reconcile_Requests_Across_Bulk_Enqueue_Calls()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 5, 10, 0, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var store = new RuntimeControlStore(path, clock);

            store.EnqueueReconcileRequests(["alpha", "beta"], "Dashboard");
            clock.Advance(TimeSpan.FromSeconds(10));
            store.EnqueueReconcileRequests(["beta", "gamma", "alpha"], "Tray");

            var snapshot = store.Read();

            Assert.Equal(["alpha", "beta", "gamma"], snapshot.ReconcileRequests
                .OrderBy(request => request.RequestedAtUtc)
                .ThenBy(request => request.ProfileName, StringComparer.OrdinalIgnoreCase)
                .Select(request => request.ProfileName)
                .ToArray());

            Assert.Equal("Dashboard", snapshot.ReconcileRequests.Single(request => request.ProfileName == "alpha").Trigger);
            Assert.Equal("Dashboard", snapshot.ReconcileRequests.Single(request => request.ProfileName == "beta").Trigger);
            Assert.Equal("Tray", snapshot.ReconcileRequests.Single(request => request.ProfileName == "gamma").Trigger);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Preserves_Queued_Reconciles_Across_CrossProcess_Pause_And_Resume_Updates()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 4, 1, 0, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var firstStore = new RuntimeControlStore(path, clock);
            var secondStore = new RuntimeControlStore(path, clock);
            var thirdStore = new RuntimeControlStore(path, clock);

            firstStore.SetProfilePaused("alpha", true, "Index rebuild");
            clock.Advance(TimeSpan.FromMinutes(1));
            secondStore.EnqueueReconcileRequest("alpha", "Dashboard");
            secondStore.EnqueueReconcileRequest("beta", "Tray");
            clock.Advance(TimeSpan.FromMinutes(1));
            thirdStore.SetPaused(true, "Maintenance window");
            clock.Advance(TimeSpan.FromMinutes(1));
            firstStore.SetProfilePaused("alpha", false);
            clock.Advance(TimeSpan.FromMinutes(1));
            secondStore.SetPaused(false);

            var snapshot = thirdStore.Read();

            Assert.False(snapshot.IsPaused);
            Assert.Null(snapshot.Reason);
            Assert.Empty(snapshot.Profiles);
            Assert.Equal(2, snapshot.ReconcileRequests.Count);
            Assert.Equal(["alpha", "beta"], snapshot.ReconcileRequests
                .OrderBy(request => request.RequestedAtUtc)
                .Select(request => request.ProfileName)
                .ToArray());

            var alphaRequest = firstStore.TryDequeueReconcileRequest("alpha");
            var betaRequest = secondStore.TryDequeueReconcileRequest("beta");

            Assert.NotNull(alphaRequest);
            Assert.Equal("Dashboard", alphaRequest!.Trigger);
            Assert.NotNull(betaRequest);
            Assert.Equal("Tray", betaRequest!.Trigger);
            Assert.Empty(thirdStore.Read().ReconcileRequests);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Prunes_Stale_Reconcile_Requests_On_Read()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 6, 8, 0, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var store = new RuntimeControlStore(path, clock);

            store.EnqueueReconcileRequest("alpha", "Dashboard");
            clock.Advance(TimeSpan.FromHours(25));
            store.EnqueueReconcileRequest("beta", "Tray");

            var snapshot = store.Read();

            var request = Assert.Single(snapshot.ReconcileRequests);
            Assert.Equal("beta", request.ProfileName);
            Assert.Equal("Tray", request.Trigger);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_Can_Disable_Stale_Reconcile_Request_Pruning()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 6, 8, 0, 0, TimeSpan.Zero));
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = Path.Combine(tempDir.FullName, "foldersync-control.json");
            var store = new RuntimeControlStore(path, clock, staleReconcileRequestThreshold: TimeSpan.Zero);

            store.EnqueueReconcileRequest("alpha", "Dashboard");
            clock.Advance(TimeSpan.FromHours(25));

            var snapshot = store.Read();

            var request = Assert.Single(snapshot.ReconcileRequests);
            Assert.Equal("alpha", request.ProfileName);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
