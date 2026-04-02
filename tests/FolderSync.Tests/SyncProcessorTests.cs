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

        pathSafety.IsReparsePoint(@"C:\source\link").Returns(true);

        var processor = new SyncProcessor(
            stabilityChecker,
            comparisonService,
            conflictResolver,
            fileOperations,
            pathMapping,
            pathSafety,
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
    }
}
