using FolderSync.Models;

namespace FolderSync.Services;

public sealed record ProfileValidationIssue(string Message, bool IsWarning);

public sealed class ProfileValidationResult
{
    public List<ProfileValidationIssue> Errors { get; } = [];
    public List<ProfileValidationIssue> Warnings { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    public void AddError(string message) => Errors.Add(new ProfileValidationIssue(message, false));

    public void AddWarning(string message) => Warnings.Add(new ProfileValidationIssue(message, true));
}

public static class ProfileConfigurationValidator
{
    public static ProfileValidationResult Validate(IReadOnlyList<ResolvedProfile> profiles)
    {
        var result = new ProfileValidationResult();

        if (profiles.Count == 0)
        {
            result.AddError("No sync profiles configured. Set Profiles or SourcePath/DestinationPath in appsettings.json.");
            return result;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (!names.Add(profile.Name))
                result.AddError($"Duplicate profile name: '{profile.Name}'");

            ValidateProfile(profile, result);
        }

        ValidateCrossProfileRelationships(profiles, result);
        return result;
    }

    private static void ValidateProfile(ResolvedProfile profile, ProfileValidationResult result)
    {
        var options = profile.Options;

        if (string.IsNullOrWhiteSpace(options.SourcePath))
        {
            result.AddError($"[{profile.Name}] SourcePath must be configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.DestinationPath))
        {
            result.AddError($"[{profile.Name}] DestinationPath must be configured");
            return;
        }

        var normalizedSource = NormalizePath(options.SourcePath);
        var normalizedDest = NormalizePath(options.DestinationPath);

        if (!Directory.Exists(options.SourcePath))
            result.AddError($"[{profile.Name}] Source directory does not exist: {options.SourcePath}");

        if (string.Equals(normalizedSource, normalizedDest, StringComparison.OrdinalIgnoreCase))
            result.AddError($"[{profile.Name}] Source and destination paths must be different");

        if (IsUnderRoot(normalizedDest, normalizedSource))
            result.AddError($"[{profile.Name}] Destination path cannot be inside the source path");

        if (IsUnderRoot(normalizedSource, normalizedDest))
            result.AddError($"[{profile.Name}] Source path cannot be inside the destination path");
    }

    private static void ValidateCrossProfileRelationships(
        IReadOnlyList<ResolvedProfile> profiles,
        ProfileValidationResult result)
    {
        var sources = profiles
            .Select(p => (p.Name, Path: NormalizePath(p.Options.SourcePath)))
            .ToList();

        for (var i = 0; i < sources.Count; i++)
        {
            for (var j = i + 1; j < sources.Count; j++)
            {
                var a = sources[i];
                var b = sources[j];

                if (string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddError($"Profiles '{a.Name}' and '{b.Name}' share the same source path: {a.Path}");
                    continue;
                }

                if (IsUnderRoot(a.Path, b.Path) || IsUnderRoot(b.Path, a.Path))
                {
                    result.AddWarning(
                        $"Profiles '{a.Name}' and '{b.Name}' have overlapping source paths: {a.Path}, {b.Path}");
                }
            }
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
