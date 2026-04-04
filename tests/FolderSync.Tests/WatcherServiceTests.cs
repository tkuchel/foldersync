using System.Reflection;
using System.Threading.Channels;
using FolderSync.Commands;
using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSync.Tests;

public sealed class WatcherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;

    public WatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-watcher-{Guid.NewGuid():N}");
        _sourceRoot = Path.Combine(_tempDir, "source");
        _destinationRoot = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_destinationRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task TryWrite_WhenChannelIsFull_EventuallyQueuesOverflowReconciliation()
    {
        var clock = new FakeClock();
        var healthPath = Path.Combine(_tempDir, "foldersync-health.json");
        var healthStore = new RuntimeHealthStore(healthPath, clock, new FakeAlertNotifier(), NullLogger<RuntimeHealthStore>.Instance);
        healthStore.Initialize(["alpha"]);
        var options = TestOptions.Create(_sourceRoot, _destinationRoot);
        var pathMapping = new PathMappingService(options);
        var pathSafety = new PathSafetyService();
        using var watcher = new WatcherService(
            "alpha",
            options,
            pathMapping,
            pathSafety,
            healthStore,
            clock,
            NullLogger<WatcherService>.Instance);

        var channel = Channel.CreateBounded<WatcherEvent>(1);
        watcher.Start(channel.Writer);

        var initialEvent = new WatcherEvent
        {
            Kind = WatcherChangeKind.Created,
            FullPath = Path.Combine(_sourceRoot, "initial.txt"),
            Timestamp = clock.UtcNow
        };
        Assert.True(channel.Writer.TryWrite(initialEvent));

        InvokeTryWrite(watcher, new WatcherEvent
        {
            Kind = WatcherChangeKind.Updated,
            FullPath = Path.Combine(_sourceRoot, "later.txt"),
            Timestamp = clock.UtcNow
        });

        var firstDequeued = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WatcherChangeKind.Created, firstDequeued.Kind);

        var overflowEvent = await WaitForOverflowAsync(channel.Reader, TestContext.Current.CancellationToken);
        Assert.Equal(WatcherChangeKind.Overflow, overflowEvent.Kind);
        Assert.Equal(_sourceRoot, overflowEvent.FullPath);

        var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(healthPath);
        var profile = Assert.Single(snapshot!.Profiles);
        Assert.Equal(1, profile.WatcherOverflowCount);

        watcher.Stop();
    }

    private static void InvokeTryWrite(WatcherService watcher, WatcherEvent watcherEvent)
    {
        var method = typeof(WatcherService).GetMethod("TryWrite", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(watcher, [watcherEvent]);
    }

    private static async Task<WatcherEvent> WaitForOverflowAsync(ChannelReader<WatcherEvent> reader, CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var watcherEvent))
            {
                if (watcherEvent.Kind == WatcherChangeKind.Overflow)
                    return watcherEvent;
            }
        }

        throw new InvalidOperationException("Expected overflow event was not written to the watcher channel.");
    }
}
