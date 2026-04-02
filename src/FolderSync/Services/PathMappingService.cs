using FolderSync.Infrastructure;
using FolderSync.Models;
using Microsoft.Extensions.Options;

namespace FolderSync.Services;

public interface IPathMappingService
{
    string GetRelativePath(string fullSourcePath);
    string MapToDestination(string fullSourcePath);
    string MapOldToDestination(string oldFullSourcePath);
    bool IsExcluded(string fullPath);
}

public sealed class PathMappingService : IPathMappingService
{
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;
    private readonly ExclusionOptions _exclusions;

    public PathMappingService(IOptions<SyncOptions> options)
    {
        _sourceRoot = NormalizePath(options.Value.SourcePath);
        _destinationRoot = NormalizePath(options.Value.DestinationPath);
        _exclusions = options.Value.Exclusions;
    }

    public string GetRelativePath(string fullSourcePath)
    {
        var normalized = NormalizePath(fullSourcePath);
        ValidateUnderRoot(normalized, _sourceRoot);
        return Path.GetRelativePath(_sourceRoot, normalized);
    }

    public string MapToDestination(string fullSourcePath)
    {
        var relative = GetRelativePath(fullSourcePath);
        var destination = Path.GetFullPath(Path.Combine(_destinationRoot, relative));
        ValidateUnderRoot(destination, _destinationRoot);
        return destination;
    }

    public string MapOldToDestination(string oldFullSourcePath)
    {
        return MapToDestination(oldFullSourcePath);
    }

    public bool IsExcluded(string fullPath)
    {
        if (SafeFile.IsSyncingFile(fullPath))
            return true;

        var fileName = Path.GetFileName(fullPath);
        var extension = Path.GetExtension(fullPath);

        // Check extensions
        foreach (var ext in _exclusions.Extensions)
        {
            if (extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check file patterns (simple glob matching)
        foreach (var pattern in _exclusions.FilePatterns)
        {
            if (MatchesPattern(fileName, pattern))
                return true;
        }

        // Check directory names in path segments
        var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var dir in _exclusions.DirectoryNames)
        {
            foreach (var segment in segments)
            {
                if (segment.Equals(dir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static void ValidateUnderRoot(string path, string root)
    {
        var fullPath = NormalizePath(path);
        var fullRoot = NormalizePath(root);

        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            return;

        var rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{path}' is not under root '{root}'. Possible path traversal.");
        }
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        // Support simple glob patterns: *.ext, ~$*, prefix*
        if (pattern.StartsWith('*') && pattern.Length > 1)
        {
            var suffix = pattern[1..];
            return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.EndsWith('*') && pattern.Length > 1)
        {
            var prefix = pattern[..^1];
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
