using FolderSync.Infrastructure;

namespace FolderSync.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_TimesOutAndCancelsProcess()
    {
        var testToken = TestContext.Current.CancellationToken;
        var runner = new ProcessRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                "powershell",
                "-NoProfile -Command \"Start-Sleep -Seconds 10\"",
                testToken,
                timeout: TimeSpan.FromMilliseconds(200)));
    }
}
