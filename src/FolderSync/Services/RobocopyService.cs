using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public sealed record RobocopyResult(bool Success, int ExitCode, string Output, string ErrorOutput);

public interface IRobocopyService
{
    Task<RobocopyResult> ReconcileAsync(CancellationToken cancellationToken = default);
}

public sealed class RobocopyService : IRobocopyService
{
    private readonly IProcessRunner _processRunner;
    private readonly SyncOptions _options;
    private readonly ILogger<RobocopyService> _logger;

    public RobocopyService(
        IProcessRunner processRunner,
        IOptions<SyncOptions> options,
        ILogger<RobocopyService> logger)
    {
        _processRunner = processRunner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RobocopyResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (_options.DryRun)
        {
            _logger.LogInformation("[DRY RUN] Would run robocopy reconciliation");
            return new RobocopyResult(true, 0, "Dry run — skipped", string.Empty);
        }

        var arguments = BuildArguments();

        _logger.LogInformation("Starting robocopy reconciliation: robocopy {Arguments}", arguments);

        var result = await _processRunner.RunAsync(
            "robocopy",
            arguments,
            cancellationToken,
            timeout: TimeSpan.FromMinutes(30));

        var success = IsSuccessExitCode(result.ExitCode);

        if (success)
        {
            _logger.LogInformation(
                "Robocopy reconciliation completed (exit code {ExitCode}): {Description}",
                result.ExitCode, DescribeExitCode(result.ExitCode));
        }
        else
        {
            _logger.LogError(
                "Robocopy reconciliation failed (exit code {ExitCode}): {Description}\n{Error}",
                result.ExitCode, DescribeExitCode(result.ExitCode), result.StandardError);
        }

        return new RobocopyResult(success, result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private string BuildArguments()
    {
        var source = Quote(_options.SourcePath);
        var dest = Quote(_options.DestinationPath);

        var args = $"{source} {dest}";
        var customOptions = _options.Reconciliation.RobocopyOptions;

        if (!string.IsNullOrWhiteSpace(customOptions))
        {
            args += $" {customOptions}";
        }

        if (!ContainsOption(customOptions, "/XJ"))
        {
            args += " /XJ";
        }

        // Add exclusions
        if (_options.Exclusions.DirectoryNames.Count > 0)
        {
            args += " /XD " + string.Join(" ", _options.Exclusions.DirectoryNames.Select(Quote));
        }

        // Exclude syncing temp files and configured file patterns (deduplicated)
        var fileExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"*{SafeFile.SyncingExtension}"
        };
        foreach (var pattern in _options.Exclusions.FilePatterns)
            fileExclusions.Add(pattern);
        args += " /XF " + string.Join(" ", fileExclusions.Select(Quote));

        if (!_options.IncludeSubdirectories)
        {
            // Remove /E if present in custom options and add /LEV:1
            args = args.Replace(" /E ", " ");
            args += " /LEV:1";
        }

        return args;
    }

    /// <summary>
    /// Robocopy exit codes are bitmapped:
    /// Bit 0 (1): Files were copied
    /// Bit 1 (2): Extra files/dirs detected
    /// Bit 2 (4): Mismatched files/dirs detected
    /// Bit 3 (8): Failed files/dirs
    /// Bit 4 (16): Fatal error
    /// Exit codes 0-7 are success/informational. 8+ indicate failures.
    /// </summary>
    private static bool IsSuccessExitCode(int exitCode) => exitCode < 8;

    private static string DescribeExitCode(int exitCode) => exitCode switch
    {
        0 => "No changes",
        1 => "Files copied successfully",
        2 => "Extra files or directories detected",
        3 => "Files copied + extras detected",
        4 => "Mismatched files or directories detected",
        5 => "Files copied + mismatches detected",
        6 => "Extras + mismatches detected",
        7 => "Files copied + extras + mismatches",
        8 => "Some files could not be copied (copy errors, max retries exceeded)",
        16 => "Fatal error — no files were copied",
        _ => $"Exit code {exitCode} (bits: {Convert.ToString(exitCode, 2)})"
    };

    private static bool ContainsOption(string? options, string expectedOption)
    {
        if (string.IsNullOrWhiteSpace(options))
            return false;

        return options
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, expectedOption, StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string value) => $"\"{value}\"";
}
