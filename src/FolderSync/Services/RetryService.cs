using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IRetryService
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string operationName,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        string operationName,
        CancellationToken cancellationToken = default);
}

public sealed class RetryService : IRetryService
{
    private readonly RetryOptions _options;
    private readonly ILogger<RetryService> _logger;

    public RetryService(IOptions<SyncOptions> options, ILogger<RetryService> logger)
    {
        _options = options.Value.Retry;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                attempt++;
                return await action(cancellationToken);
            }
            catch (Exception ex) when (attempt < _options.MaxAttempts && IsRetryable(ex))
            {
                var delay = CalculateDelay(attempt);
                _logger.LogWarning(
                    ex,
                    "Retry {Attempt}/{MaxAttempts} for {Operation}, waiting {Delay}ms",
                    attempt, _options.MaxAttempts, operationName, delay);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<object?>(async ct =>
        {
            await action(ct);
            return null;
        }, operationName, cancellationToken);
    }

    private int CalculateDelay(int attempt)
    {
        var delay = (int)(_options.InitialDelayMilliseconds * Math.Pow(_options.BackoffMultiplier, attempt - 1));
        return Math.Min(delay, _options.MaxDelayMilliseconds);
    }

    private static bool IsRetryable(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or TimeoutException;
    }
}
