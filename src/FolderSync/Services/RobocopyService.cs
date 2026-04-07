using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public sealed record RobocopyResult(
    bool Success,
    int ExitCode,
    string Output,
    string ErrorOutput,
    string ExitDescription,
    RobocopySummarySnapshot? Summary);

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
            return new RobocopyResult(true, 0, "Dry run — skipped", string.Empty, DescribeExitCode(0), null);
        }

        var arguments = BuildArguments();

        _logger.LogInformation("Starting robocopy reconciliation: robocopy {Arguments}", arguments);

        var result = await _processRunner.RunAsync(
            "robocopy",
            arguments,
            cancellationToken,
            timeout: TimeSpan.FromMinutes(30));

        var success = IsSuccessExitCode(result.ExitCode);
        var description = DescribeExitCode(result.ExitCode);
        var summary = TryParseSummary(result.StandardOutput);

        if (success)
        {
            _logger.LogInformation(
                "Robocopy reconciliation completed (exit code {ExitCode}): {Description}",
                result.ExitCode, description);
        }
        else
        {
            _logger.LogError(
                "Robocopy reconciliation failed (exit code {ExitCode}): {Description}\n{Error}",
                result.ExitCode, description, result.StandardError);
        }

        return new RobocopyResult(success, result.ExitCode, result.StandardOutput, result.StandardError, description, summary);
    }

    private string BuildArguments()
    {
        var source = Quote(_options.SourcePath);
        var dest = Quote(_options.DestinationPath);

        var optionTokens = TokenizeOptions(_options.Reconciliation.RobocopyOptions);

        if (!ContainsOption(optionTokens, "/XJ"))
            optionTokens.Add("/XJ");

        // Add exclusions
        if (_options.Exclusions.DirectoryNames.Count > 0)
        {
            optionTokens.Add("/XD");
            optionTokens.AddRange(_options.Exclusions.DirectoryNames.Select(Quote));
        }

        // Exclude syncing temp files and configured file patterns (deduplicated)
        var fileExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"*{SafeFile.SyncingExtension}"
        };
        foreach (var pattern in _options.Exclusions.FilePatterns)
            fileExclusions.Add(pattern);
        optionTokens.Add("/XF");
        optionTokens.AddRange(fileExclusions.Select(Quote));

        if (!_options.IncludeSubdirectories)
        {
            optionTokens.RemoveAll(token => string.Equals(token, "/E", StringComparison.OrdinalIgnoreCase));
            if (!ContainsOption(optionTokens, "/LEV:1"))
                optionTokens.Add("/LEV:1");
        }

        return $"{source} {dest} {string.Join(" ", optionTokens)}".Trim();
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

    internal static RobocopySummarySnapshot? TryParseSummary(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var dirs = ParseSummaryLine(lines, "Dirs");
        var files = ParseSummaryLine(lines, "Files");

        if (dirs is null && files is null)
            return null;

        return new RobocopySummarySnapshot
        {
            DirectoriesTotal = dirs?.Total,
            DirectoriesCopied = dirs?.Copied,
            DirectoriesSkipped = dirs?.Skipped,
            DirectoriesExtras = dirs?.Extras,
            FilesTotal = files?.Total,
            FilesCopied = files?.Copied,
            FilesSkipped = files?.Skipped,
            FilesExtras = files?.Extras,
            FilesFailed = files?.Failed
        };
    }

    private static SummaryCounts? ParseSummaryLine(IEnumerable<string> lines, string label)
    {
        var line = lines.LastOrDefault(candidate => candidate.StartsWith(label, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return null;

        var numbers = line
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(2)
            .Take(6)
            .Select(token => int.TryParse(token.Replace(",", string.Empty), out var value) ? value : (int?)null)
            .ToArray();

        if (numbers.Length < 6 || numbers.Any(value => value is null))
            return null;

        return new SummaryCounts(
            numbers[0]!.Value,
            numbers[1]!.Value,
            numbers[2]!.Value,
            numbers[3]!.Value,
            numbers[4]!.Value,
            numbers[5]!.Value);
    }

    private sealed record SummaryCounts(int Total, int Copied, int Skipped, int Mismatch, int Failed, int Extras);

    private static bool ContainsOption(IEnumerable<string> options, string expectedOption)
    {
        return options
            .Any(token => string.Equals(token, expectedOption, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> TokenizeOptions(string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
            return [];

        return options
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static string Quote(string value) => $"\"{value}\"";
}
