using FolderSync.Infrastructure;

namespace FolderSync.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_TimesOutAndCancelsProcess()
    {
        var runner = new ProcessRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                "powershell",
                "-NoProfile -Command \"Start-Sleep -Seconds 10\"",
                timeout: TimeSpan.FromMilliseconds(200)));
    }
}
