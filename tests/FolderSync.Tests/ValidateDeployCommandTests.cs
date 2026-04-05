using FolderSync.Commands;

namespace FolderSync.Tests;

public sealed class ValidateDeployCommandTests
{
    [Fact]
    public void FindRepositoryRoot_FindsSolutionFromNestedDirectory()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "FolderSync.slnx"), "placeholder");
            var nested = Path.Combine(root.FullName, "src", "FolderSync", "bin");
            Directory.CreateDirectory(nested);

            var result = ValidateDeployCommand.FindRepositoryRoot(nested);

            Assert.Equal(root.FullName, result);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindRepositoryRoot_ReturnsNull_WhenSolutionCannotBeFound()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var result = ValidateDeployCommand.FindRepositoryRoot(root.FullName);

            Assert.Null(result);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
