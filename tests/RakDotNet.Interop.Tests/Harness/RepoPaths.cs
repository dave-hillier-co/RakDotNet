namespace RakDotNet.Interop.Tests.Harness;

public static class RepoPaths
{
    /// <summary>Walks up from the test output folder to the directory holding RakDotNet.slnx.</summary>
    public static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RakDotNet.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No RakDotNet.slnx found above {AppContext.BaseDirectory}.");
    }

    public static string VectorsDirectory => Path.Combine(FindRepoRoot(), "tests", "vectors");
}
