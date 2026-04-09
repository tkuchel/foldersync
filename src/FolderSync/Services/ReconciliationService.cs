using System.Threading.Channels;
using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IReconciliationService
{
    Task RunReconciliationAsync(string trigger, CancellationToken cancellationToken = default);
    Task SchedulePeriodicAsync(ChannelWriter<WatcherEvent> eventChannel, CancellationToken cancellationToken);
}

public sealed class ReconciliationService : IReconciliationService, IDisposable
{
    private readonly IRobocopyService _robocopy;
    private readonly IDestinationRetentionService _retention;
    private readonly ReconciliationOptions _options;
    private readonly string _profileName;
    private readonly IRuntimeHealthStore _healthStore;
    private readonly IClock _clock;
    private readonly ILogger<ReconciliationService> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public ReconciliationService(
        string profileName,
        IRobocopyService robocopy,
        IDestinationRetentionService retention,
        IOptions<SyncOptions> options,
        IRuntimeHealthStore healthStore,
        IClock clock,
        ILogger<ReconciliationService> logger)
    {
        _profileName = profileName;
        _robocopy = robocopy;
        _retention = retention;
        _options = options.Value.Reconciliation;
        _healthStore = healthStore;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunReconciliationAsync(string trigger, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_options.UseRobocopy)
        {
            _logger.LogDebug("Reconciliation skipped (disabled or robocopy not configured)");
            return;
        }

        await _runGate.WaitAsync(cancellationToken);
        try
        {
            var startedAt = _clock.UtcNow;
            _healthStore.RecordReconciliationStarted(_profileName, trigger);
            _logger.LogInformation("Starting reconciliation...");
            var result = await _robocopy.ReconcileAsync(cancellationToken);
            var duration = _clock.UtcNow - startedAt;
            _healthStore.RecordReconciliationCompleted(_profileName, trigger, result, duration);

            if (result.Success)
                await _retention.ApplyAsync(RetentionExecutionTrigger.Reconciliation, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation("Reconciliation completed successfully (exit code {ExitCode})", result.ExitCode);
            }
            else
            {
                _logger.LogError("Reconciliation failed (exit code {ExitCode})", result.ExitCode);
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task SchedulePeriodicAsync(ChannelWriter<WatcherEvent> eventChannel, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _options.IntervalMinutes <= 0)
        {
            _logger.LogInformation("Periodic reconciliation disabled");
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.IntervalMinutes);
        _logger.LogInformation("Periodic reconciliation scheduled every {Minutes} minutes", _options.IntervalMinutes);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);

                _logger.LogDebug("Periodic reconciliation timer fired");

                eventChannel.TryWrite(new WatcherEvent
                {
                    Kind = WatcherChangeKind.ReconcileRequested,
                    FullPath = string.Empty,
                    Timestamp = _clock.UtcNow
                });
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Periodic reconciliation stopped");
        }
    }

    public void Dispose()
    {
        _runGate.Dispose();
    }
}
