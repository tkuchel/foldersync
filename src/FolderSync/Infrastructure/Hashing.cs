using System.Security.Cryptography;

namespace FolderSync.Infrastructure;

public interface IFileHasher
{
    Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed class Sha256FileHasher : IFileHasher
{
    private const int BufferSize = 81920;

    public async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hashBytes);
    }
}
