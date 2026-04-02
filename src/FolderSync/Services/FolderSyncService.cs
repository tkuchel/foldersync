using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

/// <summary>
/// Top-level orchestrator that manages multiple sync profile pipelines.
/// Each profile runs its own isolated watcher → buffer → processor pipeline.
/// </summary>
public sealed class FolderSyncService : BackgroundService
{
    private readonly FolderSyncConfig _config;
    private readonly IClock _clock;
    private readonly IFileHasher _fileHasher;
    private readonly IProcessRunner _processRunner;
    private readonly IRuntimeHealthStore _healthStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FolderSyncService> _logger;
    private readonly List<ProfilePipeline> _pipelines = [];

    public FolderSyncService(
        IOptions<FolderSyncConfig> config,
        IClock clock,
        IFileHasher fileHasher,
        IProcessRunner processRunner,
        IRuntimeHealthStore healthStore,
        ILoggerFactory loggerFactory,
        ILogger<FolderSyncService> logger)
    {
        _config = config.Value;
        _clock = clock;
        _fileHasher = fileHasher;
        _processRunner = processRunner;
        _healthStore = healthStore;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var profiles = _config.ResolveProfiles();

        if (profiles.Count == 0)
        {
            _healthStore.Initialize([]);
            _healthStore.RecordServiceError("No sync profiles configured.");
            _logger.LogError("No sync profiles configured. Set Profiles or SourcePath/DestinationPath in appsettings.json.");
            return;
        }

        _healthStore.Initialize(profiles.Select(profile => profile.Name));

        var validation = ProfileConfigurationValidator.Validate(profiles);
        foreach (var warning in validation.Warnings)
            _logger.LogWarning(warning.Message);
        if (validation.HasErrors)
        {
            foreach (var error in validation.Errors)
                _logger.LogError(error.Message);
            _healthStore.RecordServiceError("Configuration validation failed.");
            return;
        }

        _logger.LogInformation("FolderSync starting with {Count} profile(s): {Names}",
            profiles.Count, string.Join(", ", profiles.Select(p => p.Name)));
        _healthStore.RecordServiceStarted();

        // Create a pipeline per profile
        var tasks = new List<Task>();

        foreach (var profile in profiles)
        {
            var pipeline = new ProfilePipeline(
                profile.Name,
                profile.Options,
                _clock,
                _fileHasher,
                _processRunner,
                _healthStore,
                _loggerFactory);

            _pipelines.Add(pipeline);
            tasks.Add(pipeline.StartAsync(stoppingToken));
        }

        _logger.LogInformation("FolderSync is running. Press Ctrl+C to stop.");

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FolderSync shutting down...");
        }
        finally
        {
            foreach (var pipeline in _pipelines)
                pipeline.Dispose();
            _pipelines.Clear();
            _healthStore.RecordServiceStopped();
        }
    }
}
