using Microsoft.Extensions.Logging;

namespace FolderSync.Logging;

public static partial class LoggingExtensions
{
    [LoggerMessage(Level = LogLevel.Information, Message = "File synced: {RelativePath} ({Duration}ms)")]
    public static partial void LogFileSynced(this ILogger logger, string relativePath, long duration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "File skipped (unchanged): {RelativePath}")]
    public static partial void LogFileSkipped(this ILogger logger, string relativePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watcher overflow detected — reconciliation requested")]
    public static partial void LogWatcherOverflow(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation started")]
    public static partial void LogReconciliationStarted(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation completed in {Duration}ms")]
    public static partial void LogReconciliationCompleted(this ILogger logger, long duration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retry {Attempt}/{MaxAttempts} for {Operation}: {Error}")]
    public static partial void LogRetryAttempt(this ILogger logger, int attempt, int maxAttempts, string operation, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync error for {Path}: {Error}")]
    public static partial void LogSyncError(this ILogger logger, string path, string error);
}
