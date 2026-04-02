namespace FolderSync.Infrastructure;

public interface IPathSafetyService
{
    bool IsReparsePoint(string path);
}

public sealed class PathSafetyService : IPathSafetyService
{
    public bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return false;

            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
