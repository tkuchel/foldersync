using System.Threading.Channels;
using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

/// <summary>
/// Encapsulates the full watcher → buffer → processor pipeline for a single sync profile.
/// Each profile gets its own isolated set of services, channels, and background tasks.
/// </summary>
public sealed class ProfilePipeline : IDisposable
{
    private readonly string _profileName;
    private readonly SyncOptions _options;
    private readonly ILogger _logger;
    private readonly IRuntimeControlStore _controlStore;
    private readonly IRuntimeHealthStore _healthStore;

    // Per-profile services
    private readonly WatcherService _watcher;
    private readonly EventBufferService _eventBuffer;
    private readonly SyncProcessor _syncProcessor;
    private readonly ReconciliationService _reconciliation;

    // Channels
    private Channel<WatcherEvent>? _watcherChannel;
    private Channel<SyncWorkItem>? _workItemChannel;

    // Background tasks
    private Task? _bufferTask;
    private Task? _reconcileTask;
    private Task? _processingTask;

    public string ProfileName => _profileName;

    public ProfilePipeline(
        string profileName,
        SyncOptions options,
        IClock clock,
        IFileHasher fileHasher,
        IProcessRunner processRunner,
        IRuntimeControlStore controlStore,
        IRuntimeHealthStore healthStore,
        ILoggerFactory loggerFactory)
    {
        _profileName = profileName;
        _options = options;

        _logger = loggerFactory.CreateLogger($"FolderSync.Profile.{profileName}");
        _controlStore = controlStore;
        _healthStore = healthStore;

        // Create per-profile service instances using Options.Create for isolation
        var opts = Options.Create(options);

        var pathMapping = new PathMappingService(opts);
        var pathSafety = new PathSafetyService();
        var stabilityChecker = new StabilityChecker(opts, loggerFactory.CreateLogger<StabilityChecker>());
        var fileComparison = new FileComparisonService(fileHasher, opts, loggerFactory.CreateLogger<FileComparisonService>());
        var conflictResolver = new ConflictResolver(opts, loggerFactory.CreateLogger<ConflictResolver>());
        var retryService = new RetryService(opts, loggerFactory.CreateLogger<RetryService>());
        var fileOperations = new FileOperationService(retryService, opts, loggerFactory.CreateLogger<FileOperationService>());
        var robocopyService = new RobocopyService(processRunner, opts, loggerFactory.CreateLogger<RobocopyService>());

        _watcher = new WatcherService(profileName, opts, pathMapping, pathSafety, healthStore, clock, loggerFactory.CreateLogger<WatcherService>());
        _eventBuffer = new EventBufferService(pathMapping, clock, opts, loggerFactory.CreateLogger<EventBufferService>());
        _syncProcessor = new SyncProcessor(stabilityChecker, fileComparison, conflictResolver, fileOperations, pathMapping, pathSafety, loggerFactory.CreateLogger<SyncProcessor>());
        _reconciliation = new ReconciliationService(profileName, robocopyService, opts, healthStore, clock, loggerFactory.CreateLogger<ReconciliationService>());
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        using var scope = _logger.BeginScope("Profile: {ProfileName}", _profileName);

        _logger.LogInformation("[{Profile}] Starting — Source: {Source}, Destination: {Dest}",
            _profileName, _options.SourcePath, _options.DestinationPath);
        _healthStore.RecordProfileState(_profileName, "Starting");

        ValidateConfiguration();

        // Create channels
        _watcherChannel = Channel.CreateBounded<WatcherEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });

        _workItemChannel = Channel.CreateBounded<SyncWorkItem>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        // Startup reconciliation
        if (_options.Reconciliation.RunOnStartup)
        {
            await WaitUntilResumedAsync(stoppingToken);
            _logger.LogInformation("[{Profile}] Running startup reconciliation...", _profileName);
            await _reconciliation.RunReconciliationAsync("Startup", stoppingToken);
        }

        // Start watcher
        _watcher.Start(_watcherChannel.Writer);

        // Start background tasks
        _bufferTask = _eventBuffer.RunAsync(_watcherChannel.Reader, _workItemChannel.Writer, stoppingToken);
        _reconcileTask = _reconciliation.SchedulePeriodicAsync(_watcherChannel.Writer, stoppingToken);
        _processingTask = ProcessWorkItemsAsync(stoppingToken);

        _logger.LogInformation("[{Profile}] Pipeline running", _profileName);
        _healthStore.RecordProfileState(_profileName, "Running");

        // Wait for all tasks to complete (they run until cancellation)
        try
        {
            await Task.WhenAll(_bufferTask, _reconcileTask, _processingTask);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{Profile}] Pipeline shutting down...", _profileName);
        }
        finally
        {
            _healthStore.RecordProfileState(_profileName, "Stopped");
            _watcher.Stop();
            _watcherChannel.Writer.TryComplete();
            _workItemChannel.Writer.TryComplete();
        }
    }

    private async Task ProcessWorkItemsAsync(CancellationToken stoppingToken)
    {
        if (_workItemChannel is null) return;

        try
        {
            await foreach (var workItem in _workItemChannel.Reader.ReadAllAsync(stoppingToken))
            {
                if (workItem.Kind is WatcherChangeKind.Overflow or WatcherChangeKind.ReconcileRequested)
                {
                    await WaitUntilResumedAsync(stoppingToken);
                    _logger.LogInformation("[{Profile}] Processing reconciliation request ({Kind})",
                        _profileName, workItem.Kind);
                    await _reconciliation.RunReconciliationAsync(workItem.Kind.ToString(), stoppingToken);
                    continue;
                }

                await WaitUntilResumedAsync(stoppingToken);
                var result = await _syncProcessor.ProcessAsync(workItem, stoppingToken);
                _healthStore.RecordSyncResult(_profileName, result);

                if (!result.Success)
                {
                    _logger.LogWarning("[{Profile}] Failed to process {Kind} for {Path}: {Error}",
                        _profileName, workItem.Kind, workItem.SourcePath, result.ErrorMessage);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WaitUntilResumedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var control = _controlStore.Read();
            var effectivePause = control.GetEffectivePause(_profileName);
            _healthStore.RecordPauseState(control.IsPaused, control.Reason, control.ChangedAtUtc);
            _healthStore.RecordProfilePauseState(_profileName, effectivePause is not null, effectivePause?.Reason, effectivePause?.ChangedAtUtc);

            if (effectivePause is null)
            {
                _healthStore.RecordProfileState(_profileName, "Running");
                return;
            }

            _healthStore.RecordProfileState(_profileName, "Paused");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.SourcePath))
            throw new InvalidOperationException($"[{_profileName}] SourcePath must be configured");

        if (string.IsNullOrWhiteSpace(_options.DestinationPath))
            throw new InvalidOperationException($"[{_profileName}] DestinationPath must be configured");

        if (!Directory.Exists(_options.SourcePath))
            throw new InvalidOperationException($"[{_profileName}] Source directory does not exist: {_options.SourcePath}");

        var normalizedSource = Path.GetFullPath(_options.SourcePath);
        var normalizedDest = Path.GetFullPath(_options.DestinationPath);

        if (string.Equals(normalizedSource, normalizedDest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[{_profileName}] Source and destination paths must be different");

        if (normalizedDest.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[{_profileName}] Destination path cannot be inside the source path");

        if (normalizedSource.StartsWith(normalizedDest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[{_profileName}] Source path cannot be inside the destination path");

        // Ensure destination exists
        if (!Directory.Exists(_options.DestinationPath))
        {
            _logger.LogInformation("[{Profile}] Creating destination directory: {Path}",
                _profileName, _options.DestinationPath);
            Directory.CreateDirectory(_options.DestinationPath);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
