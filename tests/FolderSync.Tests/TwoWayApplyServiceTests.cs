using FolderSync.Models;
using FolderSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FolderSync.Tests;

public sealed class TwoWayApplyServiceTests
{
    private readonly IFileOperationService _leftToRightOps;
    private readonly IFileOperationService _rightToLeftOps;
    private readonly ITwoWayStateStore _stateStore;
    private readonly TwoWayApplyService _service;

    private const string LeftRoot = @"C:\Left";
    private const string RightRoot = @"C:\Right";

    public TwoWayApplyServiceTests()
    {
        _leftToRightOps = Substitute.For<IFileOperationService>();
        _rightToLeftOps = Substitute.For<IFileOperationService>();
        _stateStore = Substitute.For<ITwoWayStateStore>();

        _service = new TwoWayApplyService(
            _leftToRightOps, _rightToLeftOps,
            NullLogger<TwoWayApplyService>.Instance);
    }

    private static TwoWayPreviewResult CreatePreview(params TwoWayPreviewChange[] changes)
    {
        return new TwoWayPreviewResult { Changes = [.. changes] };
    }

    private static TwoWayPreviewChange Change(string path, TwoWayChangeKind kind) =>
        new() { RelativePath = path, Kind = kind, Summary = $"{kind} for {path}" };

    [Fact]
    public async Task LeftOnly_CopiesLeftToRight()
    {
        var preview = CreatePreview(Change("file.txt", TwoWayChangeKind.LeftOnly));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.CopiedLeftToRight);
        Assert.Equal(0, result.CopiedRightToLeft);
        await _leftToRightOps.Received(1).CopyFileAsync(
            Path.Combine(LeftRoot, "file.txt"),
            Path.Combine(RightRoot, "file.txt"),
            Arg.Any<CancellationToken>());
        _stateStore.Received(1).UpdateEntry("file.txt", Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task RightOnly_CopiesRightToLeft()
    {
        var preview = CreatePreview(Change("file.txt", TwoWayChangeKind.RightOnly));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.CopiedLeftToRight);
        Assert.Equal(1, result.CopiedRightToLeft);
        await _rightToLeftOps.Received(1).CopyFileAsync(
            Path.Combine(RightRoot, "file.txt"),
            Path.Combine(LeftRoot, "file.txt"),
            Arg.Any<CancellationToken>());
        _stateStore.Received(1).UpdateEntry("file.txt", Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task LeftChanged_CopiesLeftToRight()
    {
        var preview = CreatePreview(Change("doc.md", TwoWayChangeKind.LeftChanged));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.CopiedLeftToRight);
        await _leftToRightOps.Received(1).CopyFileAsync(
            Path.Combine(LeftRoot, "doc.md"),
            Path.Combine(RightRoot, "doc.md"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RightChanged_CopiesRightToLeft()
    {
        var preview = CreatePreview(Change("doc.md", TwoWayChangeKind.RightChanged));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.CopiedRightToLeft);
        await _rightToLeftOps.Received(1).CopyFileAsync(
            Path.Combine(RightRoot, "doc.md"),
            Path.Combine(LeftRoot, "doc.md"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TwoWayChangeKind.BothChanged)]
    [InlineData(TwoWayChangeKind.Conflict)]
    public async Task Conflict_IsSkipped(TwoWayChangeKind kind)
    {
        var preview = CreatePreview(Change("conflict.txt", kind));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SkippedConflicts);
        Assert.Equal(0, result.CopiedLeftToRight);
        Assert.Equal(0, result.CopiedRightToLeft);
        await _leftToRightOps.DidNotReceive().CopyFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _rightToLeftOps.DidNotReceive().CopyFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TwoWayChangeKind.DeleteOnLeft)]
    [InlineData(TwoWayChangeKind.DeleteOnRight)]
    public async Task Delete_IsSkipped(TwoWayChangeKind kind)
    {
        var preview = CreatePreview(Change("deleted.txt", kind));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SkippedDeletes);
        Assert.Equal(0, result.CopiedLeftToRight);
        Assert.Equal(0, result.CopiedRightToLeft);
    }

    [Fact]
    public async Task NoChange_IsIgnored()
    {
        var preview = CreatePreview(Change("stable.txt", TwoWayChangeKind.NoChange));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.CopiedLeftToRight);
        Assert.Equal(0, result.CopiedRightToLeft);
        Assert.Equal(0, result.SkippedConflicts);
        Assert.Equal(0, result.SkippedDeletes);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task FailedCopy_IncrementsFailedCount()
    {
        _leftToRightOps.CopyFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));
        var preview = CreatePreview(Change("big.bin", TwoWayChangeKind.LeftOnly));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.CopiedLeftToRight);
        Assert.Single(result.Errors);
        Assert.Contains("Disk full", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixedChanges_CountedCorrectly()
    {
        var preview = CreatePreview(
            Change("new-left.txt", TwoWayChangeKind.LeftOnly),
            Change("new-right.txt", TwoWayChangeKind.RightOnly),
            Change("conflict.txt", TwoWayChangeKind.Conflict),
            Change("deleted.txt", TwoWayChangeKind.DeleteOnLeft),
            Change("same.txt", TwoWayChangeKind.NoChange));

        var result = await _service.ApplyAsync(preview, LeftRoot, RightRoot, _stateStore, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.CopiedLeftToRight);
        Assert.Equal(1, result.CopiedRightToLeft);
        Assert.Equal(1, result.SkippedConflicts);
        Assert.Equal(1, result.SkippedDeletes);
        Assert.Equal(0, result.Failed);
    }
}
