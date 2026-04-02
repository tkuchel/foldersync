using FolderSync.Models;

namespace FolderSync.Tests;

public sealed class ConfigMergingTests
{
    [Fact]
    public void ResolveProfiles_BackwardCompat_SingleProfile()
    {
        var config = new FolderSyncConfig
        {
            SourcePath = @"C:\Source",
            DestinationPath = @"C:\Dest"
        };

        var profiles = config.ResolveProfiles();

        Assert.Single(profiles);
        Assert.Equal("default", profiles[0].Name);
        Assert.Equal(@"C:\Source", profiles[0].Options.SourcePath);
        Assert.Equal(@"C:\Dest", profiles[0].Options.DestinationPath);
    }

    [Fact]
    public void ResolveProfiles_BackwardCompat_InheritsDefaults()
    {
        var config = new FolderSyncConfig
        {
            SourcePath = @"C:\Source",
            DestinationPath = @"C:\Dest",
            Defaults = new SyncOptions
            {
                ConflictMode = ConflictMode.KeepNewest,
                DryRun = true
            }
        };

        var profiles = config.ResolveProfiles();

        Assert.Equal(ConflictMode.KeepNewest, profiles[0].Options.ConflictMode);
        Assert.True(profiles[0].Options.DryRun);
    }

    [Fact]
    public void ResolveProfiles_EmptyConfig_ReturnsEmpty()
    {
        var config = new FolderSyncConfig();

        var profiles = config.ResolveProfiles();

        Assert.Empty(profiles);
    }

    [Fact]
    public void ResolveProfiles_ProfilesPreferred_OverBackwardCompat()
    {
        var config = new FolderSyncConfig
        {
            SourcePath = @"C:\BackwardCompat",
            DestinationPath = @"C:\BackwardCompat2",
            Profiles =
            [
                new SyncProfileConfig
                {
                    Name = "workspace",
                    SourcePath = @"C:\Workspace",
                    DestinationPath = @"C:\Dest\Workspace"
                }
            ]
        };

        var profiles = config.ResolveProfiles();

        // Profiles take precedence — backward compat SourcePath/DestPath ignored
        Assert.Single(profiles);
        Assert.Equal("workspace", profiles[0].Name);
        Assert.Equal(@"C:\Workspace", profiles[0].Options.SourcePath);
    }

    [Fact]
    public void ResolveProfiles_MultipleProfiles()
    {
        var config = new FolderSyncConfig
        {
            Profiles =
            [
                new SyncProfileConfig { Name = "a", SourcePath = @"C:\A", DestinationPath = @"D:\A" },
                new SyncProfileConfig { Name = "b", SourcePath = @"C:\B", DestinationPath = @"D:\B" }
            ]
        };

        var profiles = config.ResolveProfiles();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("a", profiles[0].Name);
        Assert.Equal("b", profiles[1].Name);
    }

    [Fact]
    public void MergeWithDefaults_InheritsAllDefaults()
    {
        var defaults = new SyncOptions
        {
            ConflictMode = ConflictMode.KeepNewest,
            UseHashComparison = false,
            DebounceWindowMilliseconds = 3000,
            DryRun = true,
            Exclusions = new ExclusionOptions
            {
                DirectoryNames = [".git", "bin"],
                FilePatterns = ["*.tmp"]
            }
        };

        var profile = new SyncProfileConfig
        {
            Name = "test",
            SourcePath = @"C:\Source",
            DestinationPath = @"C:\Dest"
        };

        var result = profile.MergeWithDefaults(defaults);

        Assert.Equal(@"C:\Source", result.SourcePath);
        Assert.Equal(@"C:\Dest", result.DestinationPath);
        Assert.Equal(ConflictMode.KeepNewest, result.ConflictMode);
        Assert.False(result.UseHashComparison);
        Assert.Equal(3000, result.DebounceWindowMilliseconds);
        Assert.True(result.DryRun);
        Assert.Contains(".git", result.Exclusions.DirectoryNames);
        Assert.Contains("bin", result.Exclusions.DirectoryNames);
    }

    [Fact]
    public void MergeWithDefaults_ProfileOverridesScalars()
    {
        var defaults = new SyncOptions
        {
            ConflictMode = ConflictMode.SourceWins,
            UseHashComparison = true,
            DryRun = false
        };

        var profile = new SyncProfileConfig
        {
            Name = "test",
            SourcePath = @"C:\Source",
            DestinationPath = @"C:\Dest",
            ConflictMode = ConflictMode.SkipOnConflict,
            DryRun = true
        };

        var result = profile.MergeWithDefaults(defaults);

        Assert.Equal(ConflictMode.SkipOnConflict, result.ConflictMode);
        Assert.True(result.UseHashComparison); // Not overridden
        Assert.True(result.DryRun);
    }

    [Fact]
    public void MergeWithDefaults_ExclusionsAreMerged()
    {
        var defaults = new SyncOptions
        {
            Exclusions = new ExclusionOptions
            {
                DirectoryNames = [".git", "bin"],
                FilePatterns = ["*.tmp"],
                Extensions = [".bak"]
            }
        };

        var profile = new SyncProfileConfig
        {
            Name = "test",
            SourcePath = @"C:\Source",
            DestinationPath = @"C:\Dest",
            Exclusions = new ExclusionOptions
            {
                DirectoryNames = ["node_modules", "bin"], // "bin" is a duplicate
                FilePatterns = ["~$*"],
                Extensions = [".log"]
            }
        };

        var result = profile.MergeWithDefaults(defaults);

        // Merged and deduplicated
        Assert.Contains(".git", result.Exclusions.DirectoryNames);
        Assert.Contains("bin", result.Exclusions.DirectoryNames);
        Assert.Contains("node_modules", result.Exclusions.DirectoryNames);
        Assert.Equal(3, result.Exclusions.DirectoryNames.Count); // "bin" deduplicated

        Assert.Contains("*.tmp", result.Exclusions.FilePatterns);
        Assert.Contains("~$*", result.Exclusions.FilePatterns);

        Assert.Contains(".bak", result.Exclusions.Extensions);
        Assert.Contains(".log", result.Exclusions.Extensions);
    }

    [Fact]
    public void MergeWithDefaults_SubObjectOverride_ReplacesEntireSubObject()
    {
        var defaults = new SyncOptions
        {
            Retry = new RetryOptions
            {
                MaxAttempts = 5,
                InitialDelayMilliseconds = 2000
            }
        };

        var profile = new SyncProfileConfig
        {
            Name = "test",
            SourcePath = @"C:\Source",
            DestinationPath = @"C:\Dest",
            Retry = new RetryOptions
            {
                MaxAttempts = 3,
                InitialDelayMilliseconds = 1000
            }
        };

        var result = profile.MergeWithDefaults(defaults);

        Assert.Equal(3, result.Retry.MaxAttempts);
        Assert.Equal(1000, result.Retry.InitialDelayMilliseconds);
    }

    [Fact]
    public void MergeWithDefaults_DoesNotMutateDefaults()
    {
        var defaults = new SyncOptions
        {
            ConflictMode = ConflictMode.SourceWins,
            Exclusions = new ExclusionOptions
            {
                DirectoryNames = [".git"]
            }
        };

        var profile = new SyncProfileConfig
        {
            Name = "test",
            SourcePath = @"C:\Source",
            DestinationPath = @"C:\Dest",
            ConflictMode = ConflictMode.KeepNewest,
            Exclusions = new ExclusionOptions
            {
                DirectoryNames = ["node_modules"]
            }
        };

        profile.MergeWithDefaults(defaults);

        // Defaults must be unchanged
        Assert.Equal(ConflictMode.SourceWins, defaults.ConflictMode);
        Assert.Single(defaults.Exclusions.DirectoryNames);
        Assert.Equal(".git", defaults.Exclusions.DirectoryNames[0]);
    }

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        var original = new SyncOptions
        {
            SourcePath = @"C:\Source",
            ConflictMode = ConflictMode.KeepNewest,
            Exclusions = new ExclusionOptions
            {
                DirectoryNames = [".git", "bin"]
            }
        };

        var clone = original.Clone();

        // Modify clone
        clone.SourcePath = @"C:\Other";
        clone.ConflictMode = ConflictMode.SkipOnConflict;
        clone.Exclusions.DirectoryNames.Add("obj");

        // Original must be unaffected
        Assert.Equal(@"C:\Source", original.SourcePath);
        Assert.Equal(ConflictMode.KeepNewest, original.ConflictMode);
        Assert.Equal(2, original.Exclusions.DirectoryNames.Count);
    }
}
