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
}
