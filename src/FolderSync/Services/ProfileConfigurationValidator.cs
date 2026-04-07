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

public sealed class ProfileValidationOptions
{
    public bool Strict { get; init; }
}

public static class ProfileConfigurationValidator
{
    public static ProfileValidationResult Validate(
        IReadOnlyList<ResolvedProfile> profiles,
        ProfileValidationOptions? options = null)
    {
        options ??= new ProfileValidationOptions();
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

            ValidateProfile(profile, result, options);
        }

        ValidateCrossProfileRelationships(profiles, result, options);
        return result;
    }

    private static void ValidateProfile(
        ResolvedProfile profile,
        ProfileValidationResult result,
        ProfileValidationOptions validationOptions)
    {
        var syncOptions = profile.Options;

        if (string.IsNullOrWhiteSpace(syncOptions.SourcePath))
        {
            result.AddError($"[{profile.Name}] SourcePath must be configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(syncOptions.DestinationPath))
        {
            result.AddError($"[{profile.Name}] DestinationPath must be configured");
            return;
        }

        var normalizedSource = NormalizePath(syncOptions.SourcePath);
        var normalizedDest = NormalizePath(syncOptions.DestinationPath);

        if (!Directory.Exists(syncOptions.SourcePath))
            result.AddError($"[{profile.Name}] Source directory does not exist: {syncOptions.SourcePath}");

        if (string.Equals(normalizedSource, normalizedDest, StringComparison.OrdinalIgnoreCase))
            result.AddError($"[{profile.Name}] Source and destination paths must be different");

        if (IsUnderRoot(normalizedDest, normalizedSource))
            result.AddError($"[{profile.Name}] Destination path cannot be inside the source path");

        if (IsUnderRoot(normalizedSource, normalizedDest))
            result.AddError($"[{profile.Name}] Source path cannot be inside the destination path");

        ValidateSyncMode(profile, result);

        ValidateDeletionSettings(profile, validationOptions, result, normalizedDest);
        ValidateReconciliationSettings(profile, validationOptions, result);
        ValidateRetentionSettings(profile, result);
    }

    private static void ValidateSyncMode(
        ResolvedProfile profile,
        ProfileValidationResult result)
    {
        var syncOptions = profile.Options;

        switch (syncOptions.SyncMode)
        {
            case SyncMode.OneWay:
                return;

            case SyncMode.TwoWayPreview:
                ValidateTwoWayCommon(profile, result);
                return;

            case SyncMode.TwoWaySafe:
                ValidateTwoWayCommon(profile, result);
                if (!Directory.Exists(syncOptions.DestinationPath))
                    result.AddError($"[{profile.Name}] TwoWaySafe requires destination directory to exist: {syncOptions.DestinationPath}");
                if (syncOptions.SyncDeletions)
                    result.AddWarning($"[{profile.Name}] SyncDeletions is ignored in TwoWaySafe mode (deletes are never propagated)");
                return;

            default:
                result.AddError(
                    $"[{profile.Name}] SyncMode '{syncOptions.SyncMode}' is not supported yet.");
                return;
        }
    }

    private static void ValidateTwoWayCommon(
        ResolvedProfile profile,
        ProfileValidationResult result)
    {
        var syncOptions = profile.Options;
        if (!syncOptions.UseHashComparison && !syncOptions.TwoWay.RequireHashComparison)
            result.AddWarning($"[{profile.Name}] Hash comparison is strongly recommended for two-way sync modes");
    }

    private static void ValidateCrossProfileRelationships(
        IReadOnlyList<ResolvedProfile> profiles,
        ProfileValidationResult result,
        ProfileValidationOptions options)
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
                    AddIssue(
                        result,
                        options.Strict,
                        $"Profiles '{a.Name}' and '{b.Name}' have overlapping source paths: {a.Path}, {b.Path}");
                }
            }
        }

        var destinations = profiles
            .Select(p => (p.Name, Path: NormalizePath(p.Options.DestinationPath)))
            .ToList();

        for (var i = 0; i < destinations.Count; i++)
        {
            for (var j = i + 1; j < destinations.Count; j++)
            {
                var a = destinations[i];
                var b = destinations[j];

                if (string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddError($"Profiles '{a.Name}' and '{b.Name}' share the same destination path: {a.Path}");
                    continue;
                }

                if (IsUnderRoot(a.Path, b.Path) || IsUnderRoot(b.Path, a.Path))
                {
                    result.AddError(
                        $"Profiles '{a.Name}' and '{b.Name}' have overlapping destination paths: {a.Path}, {b.Path}");
                }
            }
        }
    }

    private static void ValidateDeletionSettings(
        ResolvedProfile profile,
        ProfileValidationOptions validationOptions,
        ProfileValidationResult result,
        string normalizedDestinationPath)
    {
        var syncOptions = profile.Options;

        if (!syncOptions.SyncDeletions)
            return;

        AddIssue(
            result,
            validationOptions.Strict,
            $"[{profile.Name}] SyncDeletions is enabled. Delete events will be mirrored to the destination.");

        if (syncOptions.DeleteMode == DeleteMode.Delete)
        {
            AddIssue(
                result,
                validationOptions.Strict,
                $"[{profile.Name}] DeleteMode is set to Delete. Files removed from the source will be permanently deleted from the destination.");
            return;
        }

        var archiveRoot = string.IsNullOrWhiteSpace(syncOptions.DeleteArchivePath)
            ? Path.Combine(syncOptions.DestinationPath, ".deleted")
            : syncOptions.DeleteArchivePath;

        var normalizedArchiveRoot = NormalizePath(archiveRoot);
        var archiveRootPath = Path.GetPathRoot(normalizedArchiveRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(archiveRootPath) &&
            string.Equals(
                normalizedArchiveRoot,
                archiveRootPath,
                StringComparison.OrdinalIgnoreCase))
        {
            result.AddError($"[{profile.Name}] DeleteArchivePath cannot be a drive or share root: {archiveRoot}");
        }

        if (!string.Equals(normalizedArchiveRoot, normalizedDestinationPath, StringComparison.OrdinalIgnoreCase) &&
            !IsUnderRoot(normalizedArchiveRoot, normalizedDestinationPath))
        {
            AddIssue(
                result,
                validationOptions.Strict,
                $"[{profile.Name}] Archive path is outside the destination root: {archiveRoot}");
        }
    }

    private static void ValidateReconciliationSettings(
        ResolvedProfile profile,
        ProfileValidationOptions validationOptions,
        ProfileValidationResult result)
    {
        var syncOptions = profile.Options;

        if (!syncOptions.Reconciliation.Enabled || !syncOptions.Reconciliation.UseRobocopy)
            return;

        var options = syncOptions.Reconciliation.RobocopyOptions;
        if (string.IsNullOrWhiteSpace(options))
        {
            AddIssue(
                result,
                validationOptions.Strict,
                $"[{profile.Name}] Reconciliation is enabled without explicit RobocopyOptions.");
            return;
        }

        var tokens = options
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!tokens.Contains("/XJ"))
        {
            AddIssue(
                result,
                validationOptions.Strict,
                $"[{profile.Name}] RobocopyOptions does not include /XJ. Junctions or reparse points could be traversed.");
        }

        if (tokens.Contains("/MIR"))
        {
            AddIssue(
                result,
                validationOptions.Strict,
                $"[{profile.Name}] RobocopyOptions includes /MIR, which can delete destination files during reconciliation.");
        }

        if (tokens.Contains("/PURGE"))
        {
            AddIssue(
                result,
                validationOptions.Strict,
                $"[{profile.Name}] RobocopyOptions includes /PURGE, which can delete destination files during reconciliation.");
        }

        if (tokens.Contains("/B") || tokens.Contains("/ZB"))
        {
            result.AddWarning(
                $"[{profile.Name}] RobocopyOptions includes backup-mode copying (/B or /ZB). This may access files with elevated privileges.");
        }
    }

    private static void ValidateRetentionSettings(ResolvedProfile profile, ProfileValidationResult result)
    {
        var retention = profile.Options.Retention;
        if (!retention.Enabled)
            return;

        if (retention.KeepNewestCount <= 0)
            result.AddError($"[{profile.Name}] Retention requires KeepNewestCount to be greater than 0.");

        if (retention.MinAgeHours < 0)
            result.AddError($"[{profile.Name}] Retention MinAgeHours cannot be negative.");

        if (string.IsNullOrWhiteSpace(retention.SearchPattern))
            result.AddError($"[{profile.Name}] Retention requires a non-empty SearchPattern.");

        if (!string.IsNullOrWhiteSpace(retention.RelativePath))
        {
            var destinationRoot = NormalizePath(profile.Options.DestinationPath);
            var retentionRoot = NormalizePath(Path.Combine(destinationRoot, retention.RelativePath));

            if (!string.Equals(retentionRoot, destinationRoot, StringComparison.OrdinalIgnoreCase) &&
                !IsUnderRoot(retentionRoot, destinationRoot))
            {
                result.AddError($"[{profile.Name}] Retention RelativePath must stay within the destination root.");
            }
        }
    }

    private static void AddIssue(ProfileValidationResult result, bool strict, string message)
    {
        if (strict)
            result.AddError(message);
        else
            result.AddWarning(message);
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
