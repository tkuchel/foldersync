using System.Threading.Channels;
using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IWatcherService : IDisposable
{
    void Start(ChannelWriter<WatcherEvent> eventChannel);
    void Stop();
}

public sealed class WatcherService : IWatcherService
{
    private readonly SyncOptions _options;
    private readonly string _profileName;
    private readonly IPathMappingService _pathMapping;
    private readonly IPathSafetyService _pathSafety;
    private readonly IRuntimeHealthStore _healthStore;
    private readonly IClock _clock;
    private readonly ILogger<WatcherService> _logger;
    private FileSystemWatcher? _watcher;
    private ChannelWriter<WatcherEvent>? _channel;

    public WatcherService(
        string profileName,
        IOptions<SyncOptions> options,
        IPathMappingService pathMapping,
        IPathSafetyService pathSafety,
        IRuntimeHealthStore healthStore,
        IClock clock,
        ILogger<WatcherService> logger)
    {
        _profileName = profileName;
        _options = options.Value;
        _pathMapping = pathMapping;
        _pathSafety = pathSafety;
        _healthStore = healthStore;
        _clock = clock;
        _logger = logger;
    }

    public void Start(ChannelWriter<WatcherEvent> eventChannel)
    {
        _channel = eventChannel;
        CreateAndStartWatcher();
        _logger.LogInformation("File watcher started for {SourcePath}", _options.SourcePath);
    }

    public void Stop()
    {
        DisposeWatcher();
        _logger.LogInformation("File watcher stopped");
    }

    public void Dispose()
    {
        DisposeWatcher();
    }

    private void CreateAndStartWatcher()
    {
        DisposeWatcher();

        _watcher = new FileSystemWatcher(_options.SourcePath)
        {
            IncludeSubdirectories = _options.IncludeSubdirectories,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
            InternalBufferSize = 65536, // 64KB
            EnableRaisingEvents = true
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        EnqueueEvent(WatcherChangeKind.Created, e.FullPath);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        EnqueueEvent(WatcherChangeKind.Updated, e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        EnqueueEvent(WatcherChangeKind.Deleted, e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_pathMapping.IsExcluded(e.FullPath) && _pathMapping.IsExcluded(e.OldFullPath))
            return;

        if (_pathSafety.IsReparsePoint(e.FullPath) || _pathSafety.IsReparsePoint(e.OldFullPath))
        {
            _logger.LogWarning("Skipping reparse point rename event for {Path}", e.FullPath);
            return;
        }

        var watcherEvent = new WatcherEvent
        {
            Kind = WatcherChangeKind.Renamed,
            FullPath = e.FullPath,
            OldFullPath = e.OldFullPath,
            Timestamp = _clock.UtcNow,
            IsDirectory = Directory.Exists(e.FullPath)
        };

        TryWrite(watcherEvent);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        _logger.LogError(ex, "FileSystemWatcher error — restarting watcher and requesting reconciliation");

        // Enqueue overflow to trigger reconciliation
        TryWrite(new WatcherEvent
        {
            Kind = WatcherChangeKind.Overflow,
            FullPath = _options.SourcePath,
            Timestamp = _clock.UtcNow
        });
        _healthStore.RecordWatcherOverflow(_profileName);

        // Restart the watcher
        try
        {
            CreateAndStartWatcher();
            _logger.LogInformation("FileSystemWatcher restarted successfully");
        }
        catch (Exception restartEx)
        {
            _logger.LogCritical(restartEx, "Failed to restart FileSystemWatcher");
        }
    }

    private void EnqueueEvent(WatcherChangeKind kind, string fullPath)
    {
        if (_pathMapping.IsExcluded(fullPath))
            return;

        if (kind is WatcherChangeKind.Created or WatcherChangeKind.Updated &&
            _pathSafety.IsReparsePoint(fullPath))
        {
            _logger.LogWarning("Skipping reparse point event for {Path}", fullPath);
            return;
        }

        var watcherEvent = new WatcherEvent
        {
            Kind = kind,
            FullPath = fullPath,
            Timestamp = _clock.UtcNow,
            IsDirectory = kind != WatcherChangeKind.Deleted && Directory.Exists(fullPath)
        };

        TryWrite(watcherEvent);
    }

    private void TryWrite(WatcherEvent watcherEvent)
    {
        if (_channel is null)
            return;

        if (!_channel.TryWrite(watcherEvent))
        {
            _logger.LogWarning(
                "Event channel full — dropping {Kind} event for {Path} and requesting reconciliation",
                watcherEvent.Kind, watcherEvent.FullPath);

            // Try to enqueue an overflow event instead
            _channel.TryWrite(new WatcherEvent
            {
                Kind = WatcherChangeKind.Overflow,
                FullPath = _options.SourcePath,
                Timestamp = _clock.UtcNow
            });
            _healthStore.RecordWatcherOverflow(_profileName);
        }
    }

    private void DisposeWatcher()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
