using System.Threading.Channels;
using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IReconciliationService
{
    Task RunReconciliationAsync(CancellationToken cancellationToken = default);
    Task SchedulePeriodicAsync(ChannelWriter<WatcherEvent> eventChannel, CancellationToken cancellationToken);
}

public sealed class ReconciliationService : IReconciliationService
{
    private readonly IRobocopyService _robocopy;
    private readonly ReconciliationOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(
        IRobocopyService robocopy,
        IOptions<SyncOptions> options,
        IClock clock,
        ILogger<ReconciliationService> logger)
    {
        _robocopy = robocopy;
        _options = options.Value.Reconciliation;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunReconciliationAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_options.UseRobocopy)
        {
            _logger.LogDebug("Reconciliation skipped (disabled or robocopy not configured)");
            return;
        }

        _logger.LogInformation("Starting reconciliation...");
        var result = await _robocopy.ReconcileAsync(cancellationToken);

        if (result.Success)
        {
            _logger.LogInformation("Reconciliation completed successfully (exit code {ExitCode})", result.ExitCode);
        }
        else
        {
            _logger.LogError("Reconciliation failed (exit code {ExitCode})", result.ExitCode);
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
}
