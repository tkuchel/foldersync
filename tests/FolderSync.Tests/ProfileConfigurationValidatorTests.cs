using FolderSync.Models;
using FolderSync.Services;

namespace FolderSync.Tests;

public sealed class ProfileConfigurationValidatorTests : IDisposable
{
    private readonly string _tempDir;

    public ProfileConfigurationValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-validate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Validate_ReturnsError_WhenNoProfilesConfigured()
    {
        var result = ProfileConfigurationValidator.Validate([]);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Message.Contains("No sync profiles configured", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsError_WhenSourceMissing()
    {
        var profile = new ResolvedProfile("test", new SyncOptions
        {
            SourcePath = Path.Combine(_tempDir, "missing"),
            DestinationPath = Path.Combine(_tempDir, "dest")
        });

        var result = ProfileConfigurationValidator.Validate([profile]);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Message.Contains("Source directory does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsWarning_ForOverlappingSources()
    {
        var sourceRoot = Path.Combine(_tempDir, "source");
        var nestedSource = Path.Combine(sourceRoot, "nested");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(nestedSource);

        var profiles = new[]
        {
            new ResolvedProfile("a", new SyncOptions
            {
                SourcePath = sourceRoot,
                DestinationPath = Path.Combine(_tempDir, "dest-a")
            }),
            new ResolvedProfile("b", new SyncOptions
            {
                SourcePath = nestedSource,
                DestinationPath = Path.Combine(_tempDir, "dest-b")
            })
        };

        var result = ProfileConfigurationValidator.Validate(profiles);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Warnings, w => w.Message.Contains("overlapping source paths", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsError_ForOverlappingDestinations()
    {
        var sourceA = Path.Combine(_tempDir, "source-a");
        var sourceB = Path.Combine(_tempDir, "source-b");
        var destRoot = Path.Combine(_tempDir, "dest");
        var nestedDest = Path.Combine(destRoot, "nested");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);

        var profiles = new[]
        {
            new ResolvedProfile("a", new SyncOptions
            {
                SourcePath = sourceA,
                DestinationPath = destRoot
            }),
            new ResolvedProfile("b", new SyncOptions
            {
                SourcePath = sourceB,
                DestinationPath = nestedDest
            })
        };

        var result = ProfileConfigurationValidator.Validate(profiles);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Message.Contains("overlapping destination paths", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsWarning_WhenSyncDeletionsEnabled()
    {
        var source = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(source);

        var profile = new ResolvedProfile("test", new SyncOptions
        {
            SourcePath = source,
            DestinationPath = Path.Combine(_tempDir, "dest"),
            SyncDeletions = true
        });

        var result = ProfileConfigurationValidator.Validate([profile]);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Warnings, w => w.Message.Contains("SyncDeletions is enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_StrictPromotesDeletionWarningToError()
    {
        var source = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(source);

        var profile = new ResolvedProfile("test", new SyncOptions
        {
            SourcePath = source,
            DestinationPath = Path.Combine(_tempDir, "dest"),
            SyncDeletions = true
        });

        var result = ProfileConfigurationValidator.Validate(
            [profile],
            new ProfileValidationOptions { Strict = true });

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Message.Contains("SyncDeletions is enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsError_WhenArchiveRootIsDriveRoot()
    {
        var source = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(source);

        var profile = new ResolvedProfile("test", new SyncOptions
        {
            SourcePath = source,
            DestinationPath = Path.Combine(_tempDir, "dest"),
            SyncDeletions = true,
            DeleteMode = DeleteMode.Archive,
            DeleteArchivePath = Path.GetPathRoot(_tempDir)!
        });

        var result = ProfileConfigurationValidator.Validate([profile]);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Message.Contains("DeleteArchivePath cannot be a drive or share root", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReturnsWarning_WhenRobocopyUsesMirror()
    {
        var source = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(source);

        var profile = new ResolvedProfile("test", new SyncOptions
        {
            SourcePath = source,
            DestinationPath = Path.Combine(_tempDir, "dest"),
            Reconciliation = new ReconciliationOptions
            {
                Enabled = true,
                UseRobocopy = true,
                RobocopyOptions = "/E /MIR /XJ"
            }
        });

        var result = ProfileConfigurationValidator.Validate([profile]);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Warnings, w => w.Message.Contains("/MIR", StringComparison.Ordinal));
    }
}
