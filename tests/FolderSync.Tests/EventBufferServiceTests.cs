using System.Threading.Channels;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FolderSync.Tests;

public sealed class EventBufferServiceTests
{
    private readonly FakeClock _clock = new();
    private readonly IPathMappingService _pathMapping;
    private readonly EventBufferService _service;

    public EventBufferServiceTests()
    {
        _pathMapping = Substitute.For<IPathMappingService>();
        _pathMapping.MapToDestination(Arg.Any<string>()).Returns(ci => ci.Arg<string>().Replace("source", "dest"));
        _pathMapping.MapOldToDestination(Arg.Any<string>()).Returns(ci => ci.Arg<string>().Replace("source", "dest"));

        var options = TestOptions.Create(configure: o => o.DebounceWindowMilliseconds = 100);
        _service = new EventBufferService(_pathMapping, _clock, options, NullLogger<EventBufferService>.Instance);
    }

    [Fact]
    public async Task OverflowEvents_PassThroughImmediately()
    {
        var testToken = TestContext.Current.CancellationToken;
        var input = Channel.CreateUnbounded<WatcherEvent>();
        var output = Channel.CreateUnbounded<SyncWorkItem>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var overflowEvent = new WatcherEvent
        {
            Kind = WatcherChangeKind.Overflow,
            FullPath = "C:\\source",
            Timestamp = _clock.UtcNow
        };

        await input.Writer.WriteAsync(overflowEvent, testToken);
        input.Writer.Complete();

        var runTask = _service.RunAsync(input.Reader, output.Writer, cts.Token);

        // Wait a bit for processing
        await Task.Delay(200, testToken);
        cts.Cancel();

        try { await runTask; } catch (OperationCanceledException) { }

        output.Writer.Complete();
        var items = new List<SyncWorkItem>();
        await foreach (var item in output.Reader.ReadAllAsync(testToken))
            items.Add(item);

        Assert.Contains(items, i => i.Kind == WatcherChangeKind.Overflow);
    }

    [Fact]
    public async Task ReconcileEvents_PassThroughImmediately()
    {
        var testToken = TestContext.Current.CancellationToken;
        var input = Channel.CreateUnbounded<WatcherEvent>();
        var output = Channel.CreateUnbounded<SyncWorkItem>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var reconcileEvent = new WatcherEvent
        {
            Kind = WatcherChangeKind.ReconcileRequested,
            FullPath = string.Empty,
            Timestamp = _clock.UtcNow
        };

        await input.Writer.WriteAsync(reconcileEvent, testToken);
        input.Writer.Complete();

        var runTask = _service.RunAsync(input.Reader, output.Writer, cts.Token);

        await Task.Delay(200, testToken);
        cts.Cancel();

        try { await runTask; } catch (OperationCanceledException) { }

        output.Writer.Complete();
        var items = new List<SyncWorkItem>();
        await foreach (var item in output.Reader.ReadAllAsync(testToken))
            items.Add(item);

        Assert.Contains(items, i => i.Kind == WatcherChangeKind.ReconcileRequested);
    }

    [Fact]
    public async Task SingleEvent_FlushesAfterDebounceWindow()
    {
        var testToken = TestContext.Current.CancellationToken;
        var input = Channel.CreateUnbounded<WatcherEvent>();
        var output = Channel.CreateUnbounded<SyncWorkItem>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var evt = new WatcherEvent
        {
            Kind = WatcherChangeKind.Created,
            FullPath = "C:\\source\\file.txt",
            Timestamp = _clock.UtcNow
        };

        await input.Writer.WriteAsync(evt, testToken);
        input.Writer.Complete();

        // Advance clock past debounce window
        _clock.Advance(TimeSpan.FromMilliseconds(200));

        var runTask = _service.RunAsync(input.Reader, output.Writer, cts.Token);

        await Task.Delay(500, testToken);
        cts.Cancel();

        try { await runTask; } catch (OperationCanceledException) { }

        output.Writer.Complete();
        var items = new List<SyncWorkItem>();
        await foreach (var item in output.Reader.ReadAllAsync(testToken))
            items.Add(item);

        Assert.Single(items);
        Assert.Equal(WatcherChangeKind.Created, items[0].Kind);
    }
}
