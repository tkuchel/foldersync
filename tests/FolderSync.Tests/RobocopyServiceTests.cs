using FolderSync.Infrastructure;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FolderSync.Tests;

public sealed class RobocopyServiceTests
{
    private readonly IProcessRunner _processRunner;
    private readonly RobocopyService _service;

    public RobocopyServiceTests()
    {
        _processRunner = Substitute.For<IProcessRunner>();

        var options = TestOptions.Create(
            "C:\\Source",
            "C:\\Dest",
            o =>
            {
                o.Exclusions.DirectoryNames.Add(".git");
                o.Exclusions.FilePatterns.Add("*.tmp");
            });

        _service = new RobocopyService(_processRunner, options, NullLogger<RobocopyService>.Instance);
    }

    [Theory]
    [InlineData(0, true)]  // No changes
    [InlineData(1, true)]  // Files copied
    [InlineData(2, true)]  // Extras detected
    [InlineData(3, true)]  // Files copied + extras
    [InlineData(4, true)]  // Mismatches
    [InlineData(7, true)]  // All informational bits
    [InlineData(8, false)] // Copy errors
    [InlineData(16, false)] // Fatal error
    public async Task ExitCode_InterpretedCorrectly(int exitCode, bool expectedSuccess)
    {
        var testToken = TestContext.Current.CancellationToken;
        _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<TimeSpan?>())
            .Returns(new ProcessResult(exitCode, "", ""));

        var result = await _service.ReconcileAsync(testToken);

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    public async Task Arguments_IncludeExclusions()
    {
        var testToken = TestContext.Current.CancellationToken;
        string? capturedArgs = null;
        _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Do<string>(args => capturedArgs = args),
            Arg.Any<CancellationToken>(),
            Arg.Any<TimeSpan?>())
            .Returns(new ProcessResult(0, "", ""));

        await _service.ReconcileAsync(testToken);

        Assert.NotNull(capturedArgs);
        Assert.Contains("/XD", capturedArgs);
        Assert.Contains("\".git\"", capturedArgs);
        Assert.Contains("/XF", capturedArgs);
        Assert.Contains("\"*.tmp\"", capturedArgs);
        Assert.Contains("\"*.__syncing\"", capturedArgs);
        Assert.Contains("/XJ", capturedArgs);
    }

    [Fact]
    public async Task Arguments_DoNotDuplicateXjOption()
    {
        var testToken = TestContext.Current.CancellationToken;
        string? capturedArgs = null;

        var options = TestOptions.Create(
            "C:\\Source",
            "C:\\Dest",
            o => o.Reconciliation.RobocopyOptions = "/E /XJ /R:1");
        var service = new RobocopyService(_processRunner, options, NullLogger<RobocopyService>.Instance);

        _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Do<string>(args => capturedArgs = args),
            Arg.Any<CancellationToken>(),
            Arg.Any<TimeSpan?>())
            .Returns(new ProcessResult(0, "", ""));

        await service.ReconcileAsync(testToken);

        Assert.NotNull(capturedArgs);
        var xjCount = capturedArgs
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(token => string.Equals(token, "/XJ", StringComparison.OrdinalIgnoreCase));
        Assert.Single(Enumerable.Repeat("/XJ", xjCount));
    }

    [Fact]
    public async Task DryRun_SkipsExecution()
    {
        var testToken = TestContext.Current.CancellationToken;
        var options = TestOptions.Create("C:\\Source", "C:\\Dest", o => o.DryRun = true);
        var service = new RobocopyService(_processRunner, options, NullLogger<RobocopyService>.Instance);

        var result = await service.ReconcileAsync(testToken);

        Assert.True(result.Success);
        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Theory]
    [InlineData("/E /R:1")]
    [InlineData("/R:1 /E")]
    [InlineData("/R:1 /E /W:1")]
    public async Task Arguments_RemoveRecursiveOption_WhenIncludeSubdirectoriesDisabled(string customOptions)
    {
        var testToken = TestContext.Current.CancellationToken;
        string? capturedArgs = null;

        var options = TestOptions.Create(
            "C:\\Source",
            "C:\\Dest",
            o =>
            {
                o.IncludeSubdirectories = false;
                o.Reconciliation.RobocopyOptions = customOptions;
            });
        var service = new RobocopyService(_processRunner, options, NullLogger<RobocopyService>.Instance);

        _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Do<string>(args => capturedArgs = args),
            Arg.Any<CancellationToken>(),
            Arg.Any<TimeSpan?>())
            .Returns(new ProcessResult(0, "", ""));

        await service.ReconcileAsync(testToken);

        Assert.NotNull(capturedArgs);
        Assert.DoesNotContain(" /E ", $" {capturedArgs} ", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/LEV:1", capturedArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseSummary_ExtractsFileAndDirectoryCounts()
    {
        var output = "               Total    Copied   Skipped  Mismatch    FAILED    Extras\n" +
                     "    Dirs :        18         0        18         0         0         1\n" +
                     "   Files :        62         5        57         0         0         0\n";

        var summary = RobocopyService.TryParseSummary(output);

        Assert.NotNull(summary);
        Assert.Equal(18, summary!.DirectoriesTotal);
        Assert.Equal(1, summary.DirectoriesExtras);
        Assert.Equal(62, summary.FilesTotal);
        Assert.Equal(5, summary.FilesCopied);
        Assert.Equal(57, summary.FilesSkipped);
        Assert.Equal(0, summary.FilesFailed);
    }
}
