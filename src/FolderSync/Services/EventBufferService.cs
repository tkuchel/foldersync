using System.Collections.Concurrent;
using System.Threading.Channels;
using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IEventBufferService
{
    Task RunAsync(
        ChannelReader<WatcherEvent> input,
        ChannelWriter<SyncWorkItem> output,
        CancellationToken cancellationToken);
}

public sealed class EventBufferService : IEventBufferService
{
    private readonly IPathMappingService _pathMapping;
    private readonly IClock _clock;
    private readonly int _debounceMs;
    private readonly ILogger<EventBufferService> _logger;

    public EventBufferService(
        IPathMappingService pathMapping,
        IClock clock,
        IOptions<SyncOptions> options,
        ILogger<EventBufferService> logger)
    {
        _pathMapping = pathMapping;
        _clock = clock;
        _debounceMs = options.Value.DebounceWindowMilliseconds;
        _logger = logger;
    }

    public async Task RunAsync(
        ChannelReader<WatcherEvent> input,
        ChannelWriter<SyncWorkItem> output,
        CancellationToken cancellationToken)
    {
        var buffer = new ConcurrentDictionary<string, BufferedEvent>(StringComparer.OrdinalIgnoreCase);

        // Run two tasks: one reads events into buffer, one flushes mature events
        var readerTask = ReadEventsAsync(input, buffer, output, cancellationToken);
        var flusherTask = FlushLoopAsync(buffer, output, cancellationToken);

        await Task.WhenAll(readerTask, flusherTask);

        // Final flush on shutdown
        FlushAll(buffer, output);
    }

    private async Task ReadEventsAsync(
        ChannelReader<WatcherEvent> input,
        ConcurrentDictionary<string, BufferedEvent> buffer,
        ChannelWriter<SyncWorkItem> output,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in input.ReadAllAsync(cancellationToken))
            {
                // Overflow and reconcile requests pass through immediately
                if (evt.Kind is WatcherChangeKind.Overflow or WatcherChangeKind.ReconcileRequested)
                {
                    var workItem = CreateWorkItem(evt);
                    if (workItem is not null)
                        await output.WriteAsync(workItem, cancellationToken);
                    continue;
                }

                var key = NormalizeKey(evt);
                buffer.AddOrUpdate(
                    key,
                    _ => new BufferedEvent(evt, _clock.UtcNow),
                    (_, existing) => CoalesceEvents(existing, evt));

                _logger.LogDebug("Buffered {Kind} event for {Path}", evt.Kind, evt.FullPath);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task FlushLoopAsync(
        ConcurrentDictionary<string, BufferedEvent> buffer,
        ChannelWriter<SyncWorkItem> output,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_debounceMs / 2, cancellationToken);

                var now = _clock.UtcNow;
                var keysToFlush = buffer
                    .Where(kvp => (now - kvp.Value.LastUpdated).TotalMilliseconds >= _debounceMs)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToFlush)
                {
                    if (buffer.TryRemove(key, out var buffered))
                    {
                        if (buffered.Discarded)
                            continue;

                        var workItem = CreateWorkItem(buffered.Event);
                        if (workItem is not null)
                        {
                            _logger.LogDebug("Flushing coalesced {Kind} event for {Path}",
                                buffered.Event.Kind, buffered.Event.FullPath);
                            await output.WriteAsync(workItem, cancellationToken);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void FlushAll(
        ConcurrentDictionary<string, BufferedEvent> buffer,
        ChannelWriter<SyncWorkItem> output)
    {
        foreach (var kvp in buffer)
        {
            if (buffer.TryRemove(kvp.Key, out var buffered) && !buffered.Discarded)
            {
                var workItem = CreateWorkItem(buffered.Event);
                if (workItem is not null)
                    output.TryWrite(workItem);
            }
        }
    }

    private BufferedEvent CoalesceEvents(BufferedEvent existing, WatcherEvent incoming)
    {
        var coalesced = (existing.Event.Kind, incoming.Kind) switch
        {
            // Created then Updated -> still Created (with latest data)
            (WatcherChangeKind.Created, WatcherChangeKind.Updated) =>
                new BufferedEvent(existing.Event, _clock.UtcNow),

            // Created then Deleted -> discard both
            (WatcherChangeKind.Created, WatcherChangeKind.Deleted) =>
                new BufferedEvent(incoming, _clock.UtcNow) { Discarded = true },

            // Updated then Updated -> keep latest
            (WatcherChangeKind.Updated, WatcherChangeKind.Updated) =>
                new BufferedEvent(incoming, _clock.UtcNow),

            // Updated then Deleted -> Deleted wins
            (WatcherChangeKind.Updated, WatcherChangeKind.Deleted) =>
                new BufferedEvent(incoming, _clock.UtcNow),

            // Deleted then Created -> treat as Updated
            (WatcherChangeKind.Deleted, WatcherChangeKind.Created) =>
                new BufferedEvent(incoming with { Kind = WatcherChangeKind.Updated }, _clock.UtcNow),

            // Default: latest event wins
            _ => new BufferedEvent(incoming, _clock.UtcNow)
        };

        return coalesced;
    }

    private SyncWorkItem? CreateWorkItem(WatcherEvent evt)
    {
        try
        {
            if (evt.Kind is WatcherChangeKind.Overflow or WatcherChangeKind.ReconcileRequested)
            {
                return new SyncWorkItem
                {
                    Kind = evt.Kind,
                    SourcePath = evt.FullPath,
                    DestinationPath = string.Empty,
                    EnqueuedAtUtc = _clock.UtcNow,
                    IsDirectory = false
                };
            }

            var destinationPath = _pathMapping.MapToDestination(evt.FullPath);
            string? oldDestinationPath = evt.OldFullPath is not null
                ? _pathMapping.MapOldToDestination(evt.OldFullPath)
                : null;

            return new SyncWorkItem
            {
                Kind = evt.Kind,
                SourcePath = evt.FullPath,
                OldSourcePath = evt.OldFullPath,
                DestinationPath = destinationPath,
                OldDestinationPath = oldDestinationPath,
                EnqueuedAtUtc = _clock.UtcNow,
                IsDirectory = evt.IsDirectory
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create work item for {Kind} event at {Path}", evt.Kind, evt.FullPath);
            return null;
        }
    }

    private static string NormalizeKey(WatcherEvent evt)
    {
        // For renames, use the new path as the key
        return evt.FullPath;
    }

    private sealed record BufferedEvent(WatcherEvent Event, DateTimeOffset LastUpdated)
    {
        public bool Discarded { get; init; }
    }
}
