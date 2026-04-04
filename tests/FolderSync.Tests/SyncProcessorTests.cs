using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FolderSync.Tests;

public sealed class SyncProcessorTests
{
    [Fact]
    public async Task ProcessAsync_SkipsReparsePointBeforeCopy()
    {
        var stabilityChecker = Substitute.For<IStabilityChecker>();
        var comparisonService = Substitute.For<IFileComparisonService>();
        var conflictResolver = Substitute.For<IConflictResolver>();
        var fileOperations = Substitute.For<IFileOperationService>();
        var pathMapping = Substitute.For<IPathMappingService>();
        var pathSafety = Substitute.For<IPathSafetyService>();
        var retention = Substitute.For<IDestinationRetentionService>();

        pathSafety.IsReparsePoint(@"C:\source\link").Returns(true);

        var processor = new SyncProcessor(
            stabilityChecker,
            comparisonService,
            conflictResolver,
            fileOperations,
            pathMapping,
            pathSafety,
            retention,
            NullLogger<SyncProcessor>.Instance);

        var workItem = new SyncWorkItem
        {
            Kind = WatcherChangeKind.Created,
            SourcePath = @"C:\source\link",
            DestinationPath = @"C:\dest\link",
            IsDirectory = false
        };

        var result = await processor.ProcessAsync(workItem, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Skipped reparse point", result.ErrorMessage);
        await fileOperations.DidNotReceive().CopyFileAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await stabilityChecker.DidNotReceive().WaitForFileReadyAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await retention.DidNotReceive().ApplyAsync(
            Arg.Any<RetentionExecutionTrigger>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_AppliesRetention_AfterSuccessfulCopy()
    {
        var stabilityChecker = Substitute.For<IStabilityChecker>();
        var comparisonService = Substitute.For<IFileComparisonService>();
        var conflictResolver = Substitute.For<IConflictResolver>();
        var fileOperations = Substitute.For<IFileOperationService>();
        var pathMapping = Substitute.For<IPathMappingService>();
        var pathSafety = Substitute.For<IPathSafetyService>();
        var retention = Substitute.For<IDestinationRetentionService>();

        stabilityChecker.WaitForFileReadyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        comparisonService.CompareAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(FileComparisonResult.DifferentContent);
        conflictResolver.Resolve(Arg.Any<FileComparisonResult>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new ConflictResolutionResult(true, "Copy"));

        var processor = new SyncProcessor(
            stabilityChecker,
            comparisonService,
            conflictResolver,
            fileOperations,
            pathMapping,
            pathSafety,
            retention,
            NullLogger<SyncProcessor>.Instance);

        var workItem = new SyncWorkItem
        {
            Kind = WatcherChangeKind.Created,
            SourcePath = @"C:\source\backup-2026-04-04.zip",
            DestinationPath = @"C:\dest\backup-2026-04-04.zip",
            IsDirectory = false
        };

        var result = await processor.ProcessAsync(workItem, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        await retention.Received(1).ApplyAsync(
            RetentionExecutionTrigger.Sync,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_DoesNotApplyRetention_WhenResultIsSkipped()
    {
        var stabilityChecker = Substitute.For<IStabilityChecker>();
        var comparisonService = Substitute.For<IFileComparisonService>();
        var conflictResolver = Substitute.For<IConflictResolver>();
        var fileOperations = Substitute.For<IFileOperationService>();
        var pathMapping = Substitute.For<IPathMappingService>();
        var pathSafety = Substitute.For<IPathSafetyService>();
        var retention = Substitute.For<IDestinationRetentionService>();

        stabilityChecker.WaitForFileReadyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        comparisonService.CompareAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(FileComparisonResult.Same);

        var processor = new SyncProcessor(
            stabilityChecker,
            comparisonService,
            conflictResolver,
            fileOperations,
            pathMapping,
            pathSafety,
            retention,
            NullLogger<SyncProcessor>.Instance);

        var workItem = new SyncWorkItem
        {
            Kind = WatcherChangeKind.Updated,
            SourcePath = @"C:\source\backup-2026-04-04.zip",
            DestinationPath = @"C:\dest\backup-2026-04-04.zip",
            IsDirectory = false
        };

        var result = await processor.ProcessAsync(workItem, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.IsSkipped);
        await retention.DidNotReceive().ApplyAsync(
            Arg.Any<RetentionExecutionTrigger>(),
            Arg.Any<CancellationToken>());
    }
}
