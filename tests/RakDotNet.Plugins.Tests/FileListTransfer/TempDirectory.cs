namespace RakDotNet.Plugins.Tests.FileListTransfer;

/// <summary>A fresh directory under the system's temp directory, deleted with everything in it on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rakdotnet-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory, without a trailing separator.</summary>
    public string Path { get; }

    public string Write(string relativePath, byte[] contents)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, contents);
        return full;
    }

    public string Write(string relativePath, string contents) => Write(relativePath, System.Text.Encoding.UTF8.GetBytes(contents));

    public byte[] Read(string relativePath) => File.ReadAllBytes(System.IO.Path.Combine(Path, relativePath));

    public bool Exists(string relativePath) => File.Exists(System.IO.Path.Combine(Path, relativePath));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
