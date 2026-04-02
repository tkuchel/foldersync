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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FolderSyncService> _logger;
    private readonly List<ProfilePipeline> _pipelines = [];

    public FolderSyncService(
        IOptions<FolderSyncConfig> config,
        IClock clock,
        IFileHasher fileHasher,
        IProcessRunner processRunner,
        ILoggerFactory loggerFactory,
        ILogger<FolderSyncService> logger)
    {
        _config = config.Value;
        _clock = clock;
        _fileHasher = fileHasher;
        _processRunner = processRunner;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var profiles = _config.ResolveProfiles();

        if (profiles.Count == 0)
        {
            _logger.LogError("No sync profiles configured. Set Profiles or SourcePath/DestinationPath in appsettings.json.");
            return;
        }

        ValidateProfiles(profiles);

        _logger.LogInformation("FolderSync starting with {Count} profile(s): {Names}",
            profiles.Count, string.Join(", ", profiles.Select(p => p.Name)));

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
        }
    }

    private void ValidateProfiles(List<ResolvedProfile> profiles)
    {
        // Check for duplicate names
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (!names.Add(profile.Name))
                throw new InvalidOperationException($"Duplicate profile name: '{profile.Name}'");
        }

        // Check for overlapping source paths
        var sourcePaths = profiles
            .Select(p => (p.Name, Path: Path.GetFullPath(p.Options.SourcePath)))
            .ToList();

        for (int i = 0; i < sourcePaths.Count; i++)
        {
            for (int j = i + 1; j < sourcePaths.Count; j++)
            {
                var a = sourcePaths[i];
                var b = sourcePaths[j];

                if (string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Profiles '{a.Name}' and '{b.Name}' share the same source path: {a.Path}");
                }

                if (a.Path.StartsWith(b.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    b.Path.StartsWith(a.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Profiles '{ProfileA}' and '{ProfileB}' have overlapping source paths: {PathA}, {PathB}",
                        a.Name, b.Name, a.Path, b.Path);
                }
            }
        }
    }
}
