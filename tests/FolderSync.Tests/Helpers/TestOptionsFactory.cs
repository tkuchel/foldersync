using FolderSync.Models;
using Microsoft.Extensions.Options;

namespace FolderSync.Tests.Helpers;

public static class TestOptions
{
    public static IOptions<SyncOptions> Create(
        string? sourcePath = null,
        string? destinationPath = null,
        Action<SyncOptions>? configure = null)
    {
        var options = new SyncOptions
        {
            SourcePath = sourcePath ?? Path.Combine(Path.GetTempPath(), "foldersync-test-source"),
            DestinationPath = destinationPath ?? Path.Combine(Path.GetTempPath(), "foldersync-test-dest"),
        };

        configure?.Invoke(options);

        return Options.Create(options);
    }
}
