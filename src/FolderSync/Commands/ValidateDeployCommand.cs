using System.CommandLine;
using System.IO.Compression;
using System.Runtime.Versioning;
using FolderSync.Infrastructure;

namespace FolderSync.Commands;

public static class ValidateDeployCommand
{
    public static Command Create()
    {
        var targetDirOption = new Option<string>("--target-dir")
        {
            Description = "Installed FolderSync directory to validate",
            DefaultValueFactory = _ => @"C:\FolderSync"
        };

        var configurationOption = new Option<string>("--configuration")
        {
            Description = "Build configuration to publish",
            DefaultValueFactory = _ => "Release"
        };

        var skipTestsOption = new Option<bool>("--skip-tests")
        {
            Description = "Skip running the test suite before publish validation"
        };

        var skipTrayOption = new Option<bool>("--skip-tray")
        {
            Description = "Skip tray publish and tray artifact validation"
        };

        var command = new Command("validate-deploy", "Validate local deployment inputs, publish output, and packaged artifacts without touching the installed service");
        command.Options.Add(targetDirOption);
        command.Options.Add(configurationOption);
        command.Options.Add(skipTestsOption);
        command.Options.Add(skipTrayOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!CommandPlatformGuard.EnsureWindows("validate-deploy"))
                return;

            await ExecuteAsync(
                parseResult.GetValue(targetDirOption)!,
                parseResult.GetValue(configurationOption)!,
                parseResult.GetValue(skipTestsOption),
                parseResult.GetValue(skipTrayOption),
                cancellationToken);
        });

        return command;
    }

    [SupportedOSPlatform("windows")]
    private static async Task ExecuteAsync(
        string targetDir,
        string configuration,
        bool skipTests,
        bool skipTray,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Could not locate FolderSync.slnx. Run this command from the repository or a child directory.");

        var projectPath = Path.Combine(repoRoot, "src", "FolderSync", "FolderSync.csproj");
        var trayProjectPath = Path.Combine(repoRoot, "src", "FolderSync.Tray", "FolderSync.Tray.csproj");
        var validateArtifactsScript = Path.Combine(repoRoot, "scripts", "Validate-ReleaseArtifacts.ps1");
        var smokeTestScript = Path.Combine(repoRoot, "scripts", "Smoke-Test-ReleaseArtifacts.ps1");
        var configPath = Path.Combine(Path.GetFullPath(targetDir), "appsettings.json");

        if (!File.Exists(projectPath))
            throw new InvalidOperationException($"Project file not found: {projectPath}");

        if (!skipTray && !File.Exists(trayProjectPath))
            throw new InvalidOperationException($"Tray project file not found: {trayProjectPath}");

        if (!Directory.Exists(targetDir))
            throw new InvalidOperationException($"Target directory not found: {targetDir}");

        if (!File.Exists(configPath))
            throw new InvalidOperationException($"Target config not found: {configPath}");

        var validation = ValidateConfigCommand.ValidateConfiguration(configPath, profileName: null, strict: false);
        foreach (var warning in validation.Warnings)
            Console.WriteLine($"Warning: {warning}");
        if (validation.Errors.Count > 0)
        {
            foreach (var error in validation.Errors)
                Console.Error.WriteLine($"Error: {error}");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"Configuration valid for {validation.ProfileCount} profile(s).");

        var processRunner = new ProcessRunner();
        if (!skipTests)
        {
            Console.WriteLine("Running test suite...");
            await RunProcessAsync(processRunner, repoRoot, "dotnet", $"test FolderSync.slnx --nologo", cancellationToken);
        }

        var servicePublishDir = CreateTempDirectory("foldersync-validate-service-");
        var trayPublishDir = CreateTempDirectory("foldersync-validate-tray-");
        var serviceZip = servicePublishDir + ".zip";
        var trayZip = trayPublishDir + ".zip";

        try
        {
            Console.WriteLine($"Publishing FolderSync ({configuration})...");
            await RunProcessAsync(processRunner, repoRoot, "dotnet", $"publish \"{projectPath}\" -c {configuration} -o \"{servicePublishDir}\"", cancellationToken);
            AssertPathExists(Path.Combine(servicePublishDir, "foldersync.exe"), "Published service executable");

            if (!skipTray)
            {
                Console.WriteLine($"Publishing FolderSync.Tray ({configuration})...");
                await RunProcessAsync(processRunner, repoRoot, "dotnet", $"publish \"{trayProjectPath}\" -c {configuration} -o \"{trayPublishDir}\"", cancellationToken);
                AssertPathExists(Path.Combine(trayPublishDir, "foldersync-tray.exe"), "Published tray executable");
            }

            RecreateZip(servicePublishDir, serviceZip);
            if (!skipTray)
                RecreateZip(trayPublishDir, trayZip);

            Console.WriteLine("Validating packaged artifacts...");
            var validationArgs = $"-ExecutionPolicy Bypass -File \"{validateArtifactsScript}\" -ServiceZip \"{serviceZip}\" -TrayZip \"{(skipTray ? serviceZip : trayZip)}\"";
            if (!skipTray)
            {
                await RunProcessAsync(processRunner, repoRoot, "powershell", validationArgs, cancellationToken);
                Console.WriteLine("Smoke-testing packaged binaries...");
                var smokeArgs = $"-ExecutionPolicy Bypass -File \"{smokeTestScript}\" -ServiceZip \"{serviceZip}\" -TrayZip \"{trayZip}\"";
                await RunProcessAsync(processRunner, repoRoot, "powershell", smokeArgs, cancellationToken);
            }
            else
            {
                Console.WriteLine("Tray validation skipped.");
            }

            Console.WriteLine("Local deployment validation completed successfully without touching the installed service.");
        }
        finally
        {
            TryDeletePath(servicePublishDir);
            TryDeletePath(trayPublishDir);
            TryDeletePath(serviceZip);
            TryDeletePath(trayZip);
        }
    }

    internal static string? FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FolderSync.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }

    private static async Task RunProcessAsync(
        IProcessRunner processRunner,
        string workingDirectory,
        string executable,
        string arguments,
        CancellationToken cancellationToken)
    {
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = workingDirectory;
        try
        {
            var result = await processRunner.RunAsync(executable, arguments, cancellationToken);
            if (result.ExitCode != 0)
            {
                var errorOutput = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                throw new InvalidOperationException($"{executable} {arguments} failed with exit code {result.ExitCode}:{Environment.NewLine}{errorOutput}");
            }

            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                Console.WriteLine(result.StandardOutput.Trim());
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RecreateZip(string sourceDirectory, string zipPath)
    {
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        ZipFile.CreateFromDirectory(sourceDirectory, zipPath);
    }

    private static void AssertPathExists(string path, string description)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"{description} not found: {path}");
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
