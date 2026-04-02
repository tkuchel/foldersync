using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSync.Tests;

public sealed class ConflictResolverTests : IDisposable
{
    private readonly string _tempDir;

    public ConflictResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void MissingDestination_AlwaysProceeds()
    {
        foreach (var mode in Enum.GetValues<ConflictMode>())
        {
            var resolver = CreateResolver(mode);
            var result = resolver.Resolve(FileComparisonResult.MissingDestination, "src", "dst");
            Assert.True(result.ShouldProceed, $"Mode {mode} should proceed on MissingDestination");
        }
    }

    [Theory]
    [InlineData(FileComparisonResult.Same)]
    [InlineData(FileComparisonResult.DifferentMetadataOnly)]
    public void IdenticalFiles_NeverProceeds(FileComparisonResult comparison)
    {
        foreach (var mode in Enum.GetValues<ConflictMode>())
        {
            var resolver = CreateResolver(mode);
            var result = resolver.Resolve(comparison, "src", "dst");
            Assert.False(result.ShouldProceed, $"Mode {mode} should not proceed on {comparison}");
        }
    }

    [Fact]
    public void SourceWins_AlwaysProceeds_OnDifferentContent()
    {
        var resolver = CreateResolver(ConflictMode.SourceWins);
        var result = resolver.Resolve(FileComparisonResult.DifferentContent, "src", "dst");
        Assert.True(result.ShouldProceed);
    }

    [Fact]
    public void PreserveDestination_Skips_OnDifferentContent()
    {
        var resolver = CreateResolver(ConflictMode.PreserveDestination);
        var result = resolver.Resolve(FileComparisonResult.DifferentContent, "src", "dst");
        Assert.False(result.ShouldProceed);
    }

    [Fact]
    public void SkipOnConflict_Skips_OnDifferentContent()
    {
        var resolver = CreateResolver(ConflictMode.SkipOnConflict);
        var result = resolver.Resolve(FileComparisonResult.DifferentContent, "src", "dst");
        Assert.False(result.ShouldProceed);
    }

    [Fact]
    public void KeepNewest_Proceeds_WhenSourceIsNewer()
    {
        var source = CreateFile("source.txt", "content");
        var dest = CreateFile("dest.txt", "old content");

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(dest, DateTime.UtcNow.AddHours(-1));

        var resolver = CreateResolver(ConflictMode.KeepNewest);
        var result = resolver.Resolve(FileComparisonResult.DifferentContent, source, dest);
        Assert.True(result.ShouldProceed);
    }

    [Fact]
    public void KeepNewest_Skips_WhenDestinationIsNewer()
    {
        var source = CreateFile("source.txt", "content");
        var dest = CreateFile("dest.txt", "newer content");

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);

        var resolver = CreateResolver(ConflictMode.KeepNewest);
        var result = resolver.Resolve(FileComparisonResult.DifferentContent, source, dest);
        Assert.False(result.ShouldProceed);
    }

    private ConflictResolver CreateResolver(ConflictMode mode)
    {
        var options = TestOptions.Create(configure: o => o.ConflictMode = mode);
        return new ConflictResolver(options, NullLogger<ConflictResolver>.Instance);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
