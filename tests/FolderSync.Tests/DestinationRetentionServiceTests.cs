using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSync.Tests;

public sealed class DestinationRetentionServiceTests : IDisposable
{
    private readonly string _tempDir;

    public DestinationRetentionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ApplyAsync_KeepsNewestDirectoriesByNameAndArchivesOlderOnes()
    {
        var source = Path.Combine(_tempDir, "source");
        var destination = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(destination, "backup-2026-04-01"));
        Directory.CreateDirectory(Path.Combine(destination, "backup-2026-04-02"));
        Directory.CreateDirectory(Path.Combine(destination, "backup-2026-04-03"));

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 2,
                ItemType = RetentionItemType.Directories,
                TriggerMode = RetentionTriggerMode.ReconciliationOnly,
                SearchPattern = "backup-*",
                SortBy = RetentionSortMode.NameDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(destination, "backup-2026-04-01")));
        Assert.True(Directory.Exists(Path.Combine(destination, "backup-2026-04-02")));
        Assert.True(Directory.Exists(Path.Combine(destination, "backup-2026-04-03")));
        Assert.True(Directory.Exists(Path.Combine(destination, ".deleted")));
    }

    [Fact]
    public async Task ApplyAsync_KeepsNewestDirectoriesWithinConfiguredRelativePath()
    {
        var source = Path.Combine(_tempDir, "source-scoped-dirs");
        var destination = Path.Combine(_tempDir, "dest-scoped-dirs");
        var retentionRoot = Path.Combine(destination, "Nightly");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(retentionRoot);
        Directory.CreateDirectory(Path.Combine(retentionRoot, "backup-2026-04-01"));
        Directory.CreateDirectory(Path.Combine(retentionRoot, "backup-2026-04-02"));
        Directory.CreateDirectory(Path.Combine(retentionRoot, "backup-2026-04-03"));
        Directory.CreateDirectory(Path.Combine(destination, "backup-outside-scope"));

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 2,
                ItemType = RetentionItemType.Directories,
                RelativePath = "Nightly",
                TriggerMode = RetentionTriggerMode.ReconciliationOnly,
                SearchPattern = "backup-*",
                SortBy = RetentionSortMode.NameDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(retentionRoot, "backup-2026-04-01")));
        Assert.True(Directory.Exists(Path.Combine(retentionRoot, "backup-2026-04-02")));
        Assert.True(Directory.Exists(Path.Combine(retentionRoot, "backup-2026-04-03")));
        Assert.True(Directory.Exists(Path.Combine(destination, "backup-outside-scope")));
    }

    [Fact]
    public async Task ApplyAsync_KeepsNewestFilesByNameAndArchivesOlderOnes()
    {
        var source = Path.Combine(_tempDir, "source-files");
        var destination = Path.Combine(_tempDir, "dest-files");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "backup-2026-04-01.zip"), "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "backup-2026-04-02.zip"), "mid", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "backup-2026-04-03.zip"), "new", TestContext.Current.CancellationToken);

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 2,
                ItemType = RetentionItemType.Files,
                TriggerMode = RetentionTriggerMode.ReconciliationOnly,
                SearchPattern = "backup-*.zip",
                SortBy = RetentionSortMode.NameDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(destination, "backup-2026-04-01.zip")));
        Assert.True(File.Exists(Path.Combine(destination, "backup-2026-04-02.zip")));
        Assert.True(File.Exists(Path.Combine(destination, "backup-2026-04-03.zip")));
        Assert.True(Directory.Exists(Path.Combine(destination, ".deleted")));
    }

    [Fact]
    public async Task ApplyAsync_KeepsNewestFilesWithinConfiguredRelativePath()
    {
        var source = Path.Combine(_tempDir, "source-scoped-files");
        var destination = Path.Combine(_tempDir, "dest-scoped-files");
        var retentionRoot = Path.Combine(destination, "ZipBackups");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(retentionRoot);
        await File.WriteAllTextAsync(Path.Combine(retentionRoot, "backup-2026-04-01.zip"), "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(retentionRoot, "backup-2026-04-02.zip"), "mid", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(retentionRoot, "backup-2026-04-03.zip"), "new", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "backup-outside-scope.zip"), "keep", TestContext.Current.CancellationToken);

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 2,
                ItemType = RetentionItemType.Files,
                RelativePath = "ZipBackups",
                TriggerMode = RetentionTriggerMode.ReconciliationOnly,
                SearchPattern = "backup-*.zip",
                SortBy = RetentionSortMode.NameDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(retentionRoot, "backup-2026-04-01.zip")));
        Assert.True(File.Exists(Path.Combine(retentionRoot, "backup-2026-04-02.zip")));
        Assert.True(File.Exists(Path.Combine(retentionRoot, "backup-2026-04-03.zip")));
        Assert.True(File.Exists(Path.Combine(destination, "backup-outside-scope.zip")));
    }

    [Fact]
    public async Task ApplyAsync_KeepsNewestFilesRecursivelyWithinConfiguredRelativePath()
    {
        var source = Path.Combine(_tempDir, "source-recursive-files");
        var destination = Path.Combine(_tempDir, "dest-recursive-files");
        var retentionRoot = Path.Combine(destination, "ZipBackups");
        var dayOne = Path.Combine(retentionRoot, "2026-04-01");
        var dayTwo = Path.Combine(retentionRoot, "2026-04-02");
        var dayThree = Path.Combine(retentionRoot, "2026-04-03");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dayOne);
        Directory.CreateDirectory(dayTwo);
        Directory.CreateDirectory(dayThree);
        await File.WriteAllTextAsync(Path.Combine(dayOne, "backup.zip"), "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dayTwo, "backup.zip"), "mid", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dayThree, "backup.zip"), "new", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "backup-outside-scope.zip"), "keep", TestContext.Current.CancellationToken);

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 2,
                ItemType = RetentionItemType.Files,
                RelativePath = "ZipBackups",
                Recursive = true,
                TriggerMode = RetentionTriggerMode.ReconciliationOnly,
                SearchPattern = "*.zip",
                SortBy = RetentionSortMode.LastWriteTimeUtcDescending
            };
        });

        File.SetLastWriteTimeUtc(Path.Combine(dayOne, "backup.zip"), new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(dayTwo, "backup.zip"), new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(dayThree, "backup.zip"), new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc));

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(dayOne, "backup.zip")));
        Assert.True(File.Exists(Path.Combine(dayTwo, "backup.zip")));
        Assert.True(File.Exists(Path.Combine(dayThree, "backup.zip")));
        Assert.True(File.Exists(Path.Combine(destination, "backup-outside-scope.zip")));
    }

    [Fact]
    public async Task ApplyAsync_KeepsNewestDirectoriesRecursivelyWithinConfiguredRelativePath()
    {
        var source = Path.Combine(_tempDir, "source-recursive-dirs");
        var destination = Path.Combine(_tempDir, "dest-recursive-dirs");
        var retentionRoot = Path.Combine(destination, "Nightly");
        var weekOne = Path.Combine(retentionRoot, "Week1");
        var weekTwo = Path.Combine(retentionRoot, "Week2");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(weekOne, "backup-2026-04-01"));
        Directory.CreateDirectory(Path.Combine(weekOne, "backup-2026-04-02"));
        Directory.CreateDirectory(Path.Combine(weekTwo, "backup-2026-04-03"));
        Directory.CreateDirectory(Path.Combine(destination, "backup-outside-scope"));

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 2,
                ItemType = RetentionItemType.Directories,
                RelativePath = "Nightly",
                Recursive = true,
                TriggerMode = RetentionTriggerMode.ReconciliationOnly,
                SearchPattern = "backup-*",
                SortBy = RetentionSortMode.NameDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(weekOne, "backup-2026-04-01")));
        Assert.True(Directory.Exists(Path.Combine(weekOne, "backup-2026-04-02")));
        Assert.True(Directory.Exists(Path.Combine(weekTwo, "backup-2026-04-03")));
        Assert.True(Directory.Exists(Path.Combine(destination, "backup-outside-scope")));
    }

    [Fact]
    public async Task ApplyAsync_DoesNotRun_WhenTriggerDoesNotMatchConfiguredMode()
    {
        var source = Path.Combine(_tempDir, "source-trigger-mismatch");
        var destination = Path.Combine(_tempDir, "dest-trigger-mismatch");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(destination, "backup-2026-04-01"));
        Directory.CreateDirectory(Path.Combine(destination, "backup-2026-04-02"));
        Directory.CreateDirectory(Path.Combine(destination, "backup-2026-04-03"));

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 2,
                ItemType = RetentionItemType.Directories,
                TriggerMode = RetentionTriggerMode.SyncOnly,
                SearchPattern = "backup-*",
                SortBy = RetentionSortMode.NameDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(destination, "backup-2026-04-01")));
        Assert.True(Directory.Exists(Path.Combine(destination, "backup-2026-04-02")));
        Assert.True(Directory.Exists(Path.Combine(destination, "backup-2026-04-03")));
    }

    [Fact]
    public async Task ApplyAsync_Runs_WhenTriggerMatchesSyncMode()
    {
        var source = Path.Combine(_tempDir, "source-trigger-sync");
        var destination = Path.Combine(_tempDir, "dest-trigger-sync");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "backup-2026-04-01.zip"), "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "backup-2026-04-02.zip"), "mid", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "backup-2026-04-03.zip"), "new", TestContext.Current.CancellationToken);

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 2,
                ItemType = RetentionItemType.Files,
                TriggerMode = RetentionTriggerMode.SyncOnly,
                SearchPattern = "backup-*.zip",
                SortBy = RetentionSortMode.NameDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Sync, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(destination, "backup-2026-04-01.zip")));
        Assert.True(File.Exists(Path.Combine(destination, "backup-2026-04-02.zip")));
        Assert.True(File.Exists(Path.Combine(destination, "backup-2026-04-03.zip")));
    }

    [Fact]
    public async Task ApplyAsync_DoesNotPrune_ItemsYoungerThanMinimumAge()
    {
        var source = Path.Combine(_tempDir, "source-min-age");
        var destination = Path.Combine(_tempDir, "dest-min-age");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);

        var oldPath = Path.Combine(destination, "backup-2026-04-01.zip");
        var midPath = Path.Combine(destination, "backup-2026-04-02.zip");
        var youngPath = Path.Combine(destination, "backup-2026-04-03.zip");
        await File.WriteAllTextAsync(oldPath, "old", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(midPath, "mid", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(youngPath, "young", TestContext.Current.CancellationToken);

        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddHours(-72));
        File.SetLastWriteTimeUtc(midPath, DateTime.UtcNow.AddHours(-36));
        File.SetLastWriteTimeUtc(youngPath, DateTime.UtcNow.AddHours(-2));

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 1,
                ItemType = RetentionItemType.Files,
                TriggerMode = RetentionTriggerMode.ReconciliationOnly,
                MinAgeHours = 24,
                SearchPattern = "backup-*.zip",
                SortBy = RetentionSortMode.LastWriteTimeUtcDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(oldPath));
        Assert.False(File.Exists(midPath));
        Assert.True(File.Exists(youngPath));
    }

    [Fact]
    public async Task ApplyAsync_LeavesOverflowItems_WhenTheyDoNotMeetMinimumAge()
    {
        var source = Path.Combine(_tempDir, "source-min-age-overflow");
        var destination = Path.Combine(_tempDir, "dest-min-age-overflow");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);

        var newestPath = Path.Combine(destination, "backup-2026-04-03.zip");
        var oldEnoughPath = Path.Combine(destination, "backup-2026-04-02.zip");
        var tooYoungPath = Path.Combine(destination, "backup-2026-04-01.zip");
        await File.WriteAllTextAsync(newestPath, "newest", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(oldEnoughPath, "old-enough", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tooYoungPath, "too-young", TestContext.Current.CancellationToken);

        File.SetLastWriteTimeUtc(newestPath, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(oldEnoughPath, DateTime.UtcNow.AddHours(-48));
        File.SetLastWriteTimeUtc(tooYoungPath, DateTime.UtcNow.AddHours(-4));

        var options = TestOptions.Create(source, destination, o =>
        {
            o.DeleteMode = DeleteMode.Archive;
            o.Retention = new DestinationRetentionOptions
            {
                Enabled = true,
                KeepNewestCount = 1,
                ItemType = RetentionItemType.Files,
                TriggerMode = RetentionTriggerMode.ReconciliationOnly,
                MinAgeHours = 24,
                SearchPattern = "backup-*.zip",
                SortBy = RetentionSortMode.LastWriteTimeUtcDescending
            };
        });

        var fileOperations = new FileOperationService(
            new RetryService(options, NullLogger<RetryService>.Instance),
            options,
            NullLogger<FileOperationService>.Instance);
        var service = new DestinationRetentionService(
            "alpha",
            options,
            fileOperations,
            NullLogger<DestinationRetentionService>.Instance);

        await service.ApplyAsync(RetentionExecutionTrigger.Reconciliation, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(newestPath));
        Assert.False(File.Exists(oldEnoughPath));
        Assert.True(File.Exists(tooYoungPath));
    }
}
