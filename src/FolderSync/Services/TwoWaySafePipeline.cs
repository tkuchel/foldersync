using System.Threading.Channels;
using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

/// <summary>
/// Two-way safe sync pipeline: watches both sides, applies non-destructive
/// creates and updates bidirectionally, skips deletes and conflicts.
/// </summary>
public sealed class TwoWaySafePipeline : IProfilePipeline
{
    private readonly string _profileName;
    private readonly SyncOptions _options;
    private readonly ILogger _logger;
    private readonly IRuntimeControlStore _controlStore;
    private readonly IRuntimeHealthStore _healthStore;

    private readonly WatcherService _leftWatcher;
    private readonly WatcherService _rightWatcher;
    private readonly ITwoWayPreviewService _previewService;
    private readonly ITwoWayApplyService _applyService;
    private readonly ITwoWayStateStore _stateStore;

    private Channel<WatcherEvent>? _channel;
    private Task? _processingTask;
    private Task? _controlTask;

    public string ProfileName => _profileName;

    public TwoWaySafePipeline(
        string profileName,
        SyncOptions options,
        IClock clock,
        IFileHasher fileHasher,
        IRuntimeControlStore controlStore,
        IRuntimeHealthStore healthStore,
        ILoggerFactory loggerFactory)
    {
        _profileName = profileName;
        _options = options;
        _logger = loggerFactory.CreateLogger($"FolderSync.TwoWaySafe.{profileName}");
        _controlStore = controlStore;
        _healthStore = healthStore;

        var leftOpts = Options.Create(options);
        var pathSafety = new PathSafetyService();

        // Left watcher: watches the source directory
        var leftPathMapping = new PathMappingService(leftOpts);
        _leftWatcher = new WatcherService(
            $"{profileName}-left", leftOpts, leftPathMapping, pathSafety,
            healthStore, clock, loggerFactory.CreateLogger<WatcherService>());

        // Right watcher: watches the destination directory
        // Create swapped options so the watcher monitors DestinationPath
        var rightOptions = options.Clone();
        rightOptions.SourcePath = options.DestinationPath;
        rightOptions.DestinationPath = options.SourcePath;
        var rightOpts = Options.Create(rightOptions);
        var rightPathMapping = new PathMappingService(rightOpts);
        _rightWatcher = new WatcherService(
            $"{profileName}-right", rightOpts, rightPathMapping, pathSafety,
            healthStore, clock, loggerFactory.CreateLogger<WatcherService>());

        // Preview service (uses original options with left=source, right=dest)
        _previewService = new TwoWayPreviewService(
            fileHasher, pathSafety,
            new TwoWayPreviewClassifier());

        // State store
        var stateStorePath = ResolveStateStorePath(options);
        _stateStore = new JsonTwoWayStateStore(stateStorePath);

        // Apply service needs two FileOperationService instances
        var retryService = new RetryService(leftOpts, loggerFactory.CreateLogger<RetryService>());
        var leftToRightOps = new FileOperationService(retryService, leftOpts, loggerFactory.CreateLogger<FileOperationService>());

        var rightRetryService = new RetryService(rightOpts, loggerFactory.CreateLogger<RetryService>());
        var rightToLeftOps = new FileOperationService(rightRetryService, rightOpts, loggerFactory.CreateLogger<FileOperationService>());

        _applyService = new TwoWayApplyService(
            leftToRightOps, rightToLeftOps,
            loggerFactory.CreateLogger<TwoWayApplyService>());
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        using var scope = _logger.BeginScope("Profile: {ProfileName}", _profileName);

        _logger.LogInformation("[{Profile}] Starting TwoWaySafe — Left: {Left}, Right: {Right}",
            _profileName, _options.SourcePath, _options.DestinationPath);
        _healthStore.RecordProfileState(_profileName, "Starting");

        ValidateConfiguration();

        // Shared channel for events from both watchers
        _channel = Channel.CreateBounded<WatcherEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });

        // Run initial reconciliation
        _logger.LogInformation("[{Profile}] Running startup two-way reconciliation...", _profileName);
        await RunTwoWayReconciliationAsync(stoppingToken);

        // Start both watchers feeding into the same channel
        _leftWatcher.Start(_channel.Writer);
        _rightWatcher.Start(_channel.Writer);

        // Start processing
        _processingTask = ProcessEventsAsync(stoppingToken);
        _controlTask = MonitorControlRequestsAsync(stoppingToken);

        _logger.LogInformation("[{Profile}] TwoWaySafe pipeline running", _profileName);
        _healthStore.RecordProfileState(_profileName, "Running");

        try
        {
            await Task.WhenAll(_processingTask, _controlTask);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{Profile}] TwoWaySafe pipeline shutting down...", _profileName);
        }
        finally
        {
            _healthStore.RecordProfileState(_profileName, "Stopped");
            _leftWatcher.Stop();
            _rightWatcher.Stop();
            _channel.Writer.TryComplete();
        }
    }

    private async Task ProcessEventsAsync(CancellationToken stoppingToken)
    {
        if (_channel is null) return;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait for at least one event
                await _channel.Reader.WaitToReadAsync(stoppingToken);

                // Drain the channel and debounce
                await Task.Delay(_options.DebounceWindowMilliseconds, stoppingToken);
                while (_channel.Reader.TryRead(out _)) { }

                await WaitUntilResumedAsync(stoppingToken);
                await RunTwoWayReconciliationAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunTwoWayReconciliationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[{Profile}] Running two-way sync...", _profileName);

            var detailed = await _previewService.RunDetailedAsync(_options, _stateStore, cancellationToken);
            var actionable = detailed.PreviewResult.Changes
                .Where(c => c.Kind != TwoWayChangeKind.NoChange)
                .ToList();

            if (actionable.Count == 0)
            {
                _logger.LogDebug("[{Profile}] No changes detected", _profileName);
                return;
            }

            _logger.LogInformation("[{Profile}] Detected {Count} changes, applying safe sync...",
                _profileName, actionable.Count);

            var applyResult = await _applyService.ApplyAsync(
                detailed.PreviewResult,
                _options.SourcePath,
                _options.DestinationPath,
                _stateStore,
                cancellationToken);

            // Record successful syncs as individual results for health tracking
            var totalApplied = applyResult.CopiedLeftToRight + applyResult.CopiedRightToLeft;
            if (totalApplied > 0 || applyResult.Failed > 0)
            {
                _logger.LogInformation(
                    "[{Profile}] TwoWaySafe applied: L→R={LR}, R→L={RL}, conflicts={C}, deletes skipped={D}, failed={F}",
                    _profileName, applyResult.CopiedLeftToRight, applyResult.CopiedRightToLeft,
                    applyResult.SkippedConflicts, applyResult.SkippedDeletes, applyResult.Failed);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Profile}] Two-way sync failed", _profileName);
        }
    }

    private async Task MonitorControlRequestsAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var request = _controlStore.TryDequeueReconcileRequest(_profileName);
                if (request is not null)
                {
                    await WaitUntilResumedAsync(stoppingToken);
                    _logger.LogInformation("[{Profile}] Processing control reconciliation request ({Trigger})",
                        _profileName, request.Trigger);
                    await RunTwoWayReconciliationAsync(stoppingToken);
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
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

        if (!Directory.Exists(_options.DestinationPath))
        {
            _logger.LogInformation("[{Profile}] Creating destination directory: {Path}",
                _profileName, _options.DestinationPath);
            Directory.CreateDirectory(_options.DestinationPath);
        }
    }

    private static string ResolveStateStorePath(SyncOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TwoWay.StateStorePath))
            return options.TwoWay.StateStorePath;

        var appDir = AppContext.BaseDirectory;
        return Path.Combine(appDir, "state", "twoway-state.json");
    }

    public void Dispose()
    {
        _leftWatcher.Dispose();
        _rightWatcher.Dispose();
    }
}
