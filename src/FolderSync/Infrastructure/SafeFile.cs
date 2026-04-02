namespace FolderSync.Infrastructure;

public static class SafeFile
{
    public const string SyncingExtension = ".__syncing";

    public static async Task SafeCopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var tempPath = destinationPath + SyncingExtension;

        try
        {
            EnsureDirectoryExists(destinationPath);

            await using (var sourceStream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await sourceStream.CopyToAsync(destStream, cancellationToken);
            }

            // Preserve timestamps
            var sourceInfo = new FileInfo(sourcePath);
            File.SetLastWriteTimeUtc(tempPath, sourceInfo.LastWriteTimeUtc);
            File.SetCreationTimeUtc(tempPath, sourceInfo.CreationTimeUtc);

            // Atomic-ish replace
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            // Clean up temp file on failure
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best effort cleanup */ }
        }
    }

    public static void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public static string GenerateArchivePath(string filePath, string archiveRoot, string relativePath)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var archiveFileName = $"{fileName} (deleted {timestamp}){ext}";
        var relativeDir = Path.GetDirectoryName(relativePath) ?? string.Empty;
        return Path.Combine(archiveRoot, relativeDir, archiveFileName);
    }

    public static bool IsSyncingFile(string path)
    {
        return path.EndsWith(SyncingExtension, StringComparison.OrdinalIgnoreCase);
    }
}
