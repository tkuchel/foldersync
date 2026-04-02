using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;

namespace FolderSync.Tests;

public sealed class PathMappingServiceTests : IDisposable
{
    private readonly string _sourceRoot;
    private readonly string _destRoot;
    private readonly PathMappingService _service;

    public PathMappingServiceTests()
    {
        _sourceRoot = Path.Combine(Path.GetTempPath(), $"foldersync-test-src-{Guid.NewGuid():N}");
        _destRoot = Path.Combine(Path.GetTempPath(), $"foldersync-test-dst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_destRoot);

        var options = TestOptions.Create(_sourceRoot, _destRoot);
        _service = new PathMappingService(options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceRoot)) Directory.Delete(_sourceRoot, true);
        if (Directory.Exists(_destRoot)) Directory.Delete(_destRoot, true);
    }

    [Fact]
    public void GetRelativePath_ReturnsCorrectRelativePath()
    {
        var fullPath = Path.Combine(_sourceRoot, "subdir", "file.txt");
        var relative = _service.GetRelativePath(fullPath);

        Assert.Equal(Path.Combine("subdir", "file.txt"), relative);
    }

    [Fact]
    public void MapToDestination_MapsCorrectly()
    {
        var sourcePath = Path.Combine(_sourceRoot, "subdir", "file.txt");
        var result = _service.MapToDestination(sourcePath);

        var expected = Path.Combine(_destRoot, "subdir", "file.txt");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MapToDestination_ThrowsOnPathTraversal()
    {
        var maliciousPath = Path.Combine(_sourceRoot, "..", "etc", "passwd");

        Assert.Throws<InvalidOperationException>(() => _service.MapToDestination(maliciousPath));
    }

    [Fact]
    public void MapToDestination_ThrowsOnSiblingPrefixPath()
    {
        var siblingPath = _sourceRoot + "2" + Path.DirectorySeparatorChar + "file.txt";

        Assert.Throws<InvalidOperationException>(() => _service.MapToDestination(siblingPath));
    }

    [Fact]
    public void IsExcluded_ExcludesByExtension()
    {
        var options = TestOptions.Create(_sourceRoot, _destRoot, o =>
            o.Exclusions.Extensions.Add(".tmp"));
        var service = new PathMappingService(options);

        Assert.True(service.IsExcluded(Path.Combine(_sourceRoot, "file.tmp")));
        Assert.False(service.IsExcluded(Path.Combine(_sourceRoot, "file.txt")));
    }

    [Fact]
    public void IsExcluded_ExcludesByDirectoryName()
    {
        var options = TestOptions.Create(_sourceRoot, _destRoot, o =>
            o.Exclusions.DirectoryNames.Add(".git"));
        var service = new PathMappingService(options);

        Assert.True(service.IsExcluded(Path.Combine(_sourceRoot, ".git", "config")));
        Assert.False(service.IsExcluded(Path.Combine(_sourceRoot, "src", "file.cs")));
    }

    [Fact]
    public void IsExcluded_ExcludesByFilePattern()
    {
        var options = TestOptions.Create(_sourceRoot, _destRoot, o =>
            o.Exclusions.FilePatterns.Add("~$*"));
        var service = new PathMappingService(options);

        Assert.True(service.IsExcluded(Path.Combine(_sourceRoot, "~$document.docx")));
        Assert.False(service.IsExcluded(Path.Combine(_sourceRoot, "document.docx")));
    }

    [Fact]
    public void IsExcluded_ExcludesSyncingFiles()
    {
        Assert.True(_service.IsExcluded(Path.Combine(_sourceRoot, "file.txt.__syncing")));
    }

    [Fact]
    public void GetRelativePath_RootFile()
    {
        var fullPath = Path.Combine(_sourceRoot, "file.txt");
        var relative = _service.GetRelativePath(fullPath);

        Assert.Equal("file.txt", relative);
    }
}
