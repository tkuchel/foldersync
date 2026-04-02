using FolderSync.Infrastructure;
using FolderSync.Models;
using FolderSync.Services;
using FolderSync.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FolderSync.Tests;

public sealed class FileComparisonServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IFileHasher _hasher;
    private readonly FileComparisonService _service;

    public FileComparisonServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"foldersync-compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _hasher = Substitute.For<IFileHasher>();
        var options = TestOptions.Create(configure: o => o.UseHashComparison = true);
        _service = new FileComparisonService(_hasher, options, NullLogger<FileComparisonService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Compare_MissingDestination()
    {
        var testToken = TestContext.Current.CancellationToken;
        var source = CreateFile("source.txt", "content");
        var dest = Path.Combine(_tempDir, "nonexistent.txt");

        var result = await _service.CompareAsync(source, dest, testToken);

        Assert.Equal(FileComparisonResult.MissingDestination, result);
    }

    [Fact]
    public async Task Compare_SameContent_SameTimestamp()
    {
        var testToken = TestContext.Current.CancellationToken;
        var source = CreateFile("source.txt", "content");
        var dest = CreateFile("dest.txt", "content");

        // Set timestamps to match
        var time = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(source, time);
        File.SetLastWriteTimeUtc(dest, time);

        var result = await _service.CompareAsync(source, dest, testToken);

        Assert.Equal(FileComparisonResult.Same, result);
    }

    [Fact]
    public async Task Compare_DifferentSize()
    {
        var testToken = TestContext.Current.CancellationToken;
        var source = CreateFile("source.txt", "long content here");
        var dest = CreateFile("dest.txt", "short");

        var result = await _service.CompareAsync(source, dest, testToken);

        Assert.Equal(FileComparisonResult.DifferentContent, result);
    }

    [Fact]
    public async Task Compare_SameSize_DifferentTimestamp_SameHash()
    {
        var testToken = TestContext.Current.CancellationToken;
        var source = CreateFile("source.txt", "content");
        var dest = CreateFile("dest.txt", "content");

        // Set different timestamps
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(dest, DateTime.UtcNow.AddMinutes(-10));

        _hasher.ComputeHashAsync(source, Arg.Any<CancellationToken>()).Returns("abc123");
        _hasher.ComputeHashAsync(dest, Arg.Any<CancellationToken>()).Returns("abc123");

        var result = await _service.CompareAsync(source, dest, testToken);

        Assert.Equal(FileComparisonResult.DifferentMetadataOnly, result);
    }

    [Fact]
    public async Task Compare_SameSize_DifferentTimestamp_DifferentHash()
    {
        var testToken = TestContext.Current.CancellationToken;
        var source = CreateFile("source.txt", "content1");
        var dest = CreateFile("dest.txt", "content2");

        // Make same size with padding
        File.WriteAllText(source, "abcdefg");
        File.WriteAllText(dest, "hijklmn");

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(dest, DateTime.UtcNow.AddMinutes(-10));

        _hasher.ComputeHashAsync(source, Arg.Any<CancellationToken>()).Returns("hash1");
        _hasher.ComputeHashAsync(dest, Arg.Any<CancellationToken>()).Returns("hash2");

        var result = await _service.CompareAsync(source, dest, testToken);

        Assert.Equal(FileComparisonResult.DifferentContent, result);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
