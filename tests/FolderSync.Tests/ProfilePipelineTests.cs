using FolderSync.Commands;
using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging;

namespace FolderSync.Tests;

public sealed class ProfilePipelineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;

    public ProfilePipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-pipeline-{Guid.NewGuid():N}");
        _sourceRoot = Path.Combine(_tempDir, "source");
        _destinationRoot = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_destinationRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task StartAsync_Consumes_Control_Reconcile_Request_And_Records_Runtime_Health()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero));

        var controlPath = Path.Combine(_tempDir, "foldersync-control.json");
        var healthPath = Path.Combine(_tempDir, "foldersync-health.json");
        var controlStore = new RuntimeControlStore(controlPath, clock);
        var healthStore = new RuntimeHealthStore(healthPath, clock, new FakeAlertNotifier(), LoggerFactory.Create(builder => { }).CreateLogger<RuntimeHealthStore>());
        healthStore.Initialize(["alpha"]);
        healthStore.RecordServiceStarted();

        var processRunner = new FakeProcessRunner();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var pipeline = new ProfilePipeline(
            "alpha",
            new SyncOptions
            {
                SourcePath = _sourceRoot,
                DestinationPath = _destinationRoot,
                Reconciliation = new ReconciliationOptions
                {
                    Enabled = true,
                    RunOnStartup = false,
                    UseRobocopy = true,
                    IntervalMinutes = 0,
                    RobocopyOptions = "/E /FFT /XJ"
                }
            },
            clock,
            new Sha256FileHasher(),
            processRunner,
            controlStore,
            healthStore,
            loggerFactory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipelineTask = pipeline.StartAsync(cts.Token);

        controlStore.EnqueueReconcileRequest("alpha", "Dashboard");

        await processRunner.WaitForInvocationAsync();

        await WaitUntilAsync(
            () => controlStore.Read().ReconcileRequests.Count == 0 && processRunner.InvocationCount >= 1,
            cts.Token);

        var snapshot = await WaitForSnapshotAsync(
            healthPath,
            runtimeSnapshot => runtimeSnapshot.Profiles.Single().Reconciliation.RunCount >= 1,
            cts.Token);

        var profile = Assert.Single(snapshot.Profiles);
        Assert.True(profile.Reconciliation.RunCount >= 1);
        Assert.True(processRunner.InvocationCount >= 1);
        Assert.Empty(controlStore.Read().ReconcileRequests);

        cts.Cancel();
        await pipelineTask;
    }

    private static async Task<RuntimeHealthSnapshot> WaitForSnapshotAsync(
        string healthPath,
        Func<RuntimeHealthSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = StatusCommand.TryReadRuntimeHealthSnapshot(healthPath);
            if (snapshot is not null && predicate(snapshot))
                return snapshot;

            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
            await Task.Delay(50, cancellationToken);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource _invoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executable,
            string arguments,
            CancellationToken cancellationToken = default,
            TimeSpan? timeout = null)
        {
            InvocationCount++;
            _invoked.TrySetResult();

            const string output = """
-------------------------------------------------------------------------------
   ROBOCOPY     ::     Robust File Copy for Windows
-------------------------------------------------------------------------------

   Dirs :         1         0         1         0         0         0
  Files :         1         0         1         0         0         0
""";

            return Task.FromResult(new ProcessResult(0, output, string.Empty));
        }

        public Task WaitForInvocationAsync() => _invoked.Task;
    }
}
