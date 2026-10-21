namespace RakDotNet.Plugins.Tests.FileListTransfer;

public sealed class FileOperationsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("a.txt")]
    [InlineData("sub/dir/a.txt")]
    [InlineData("sub\\dir\\a.txt")]
    [InlineData("./a.txt")]
    [InlineData("sub/../a.txt")]
    [InlineData("/a.txt")]
    public void A_name_inside_the_directory_resolves_under_it(string name)
    {
        var root = Path.Combine(_temp.Path, "out");

        Assert.True(FileOperations.TryGetContainedPath(root, name, out var full));

        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, full, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../a.txt")]
    [InlineData("sub/../../a.txt")]
    [InlineData("..\\a.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a\0b")]
    public void A_name_that_leaves_the_directory_or_names_it_is_refused(string name) =>
        Assert.False(FileOperations.TryGetContainedPath(Path.Combine(_temp.Path, "out"), name, out _));

    [Fact]
    public void A_sibling_directory_sharing_the_prefix_is_outside() =>
        Assert.False(FileOperations.TryGetContainedPath(Path.Combine(_temp.Path, "out"), "../outside/a.txt", out _));

    [Fact]
    public void Writes_a_file_creating_its_directories()
    {
        var path = Path.Combine(_temp.Path, "a", "b", "c.bin");

        Assert.True(FileOperations.WriteFileWithDirectories(path, [1, 2]));

        Assert.Equal([1, 2], File.ReadAllBytes(path));
    }

    [Fact]
    public void Add_slash_turns_a_trailing_backslash_into_a_slash()
    {
        Assert.Equal("a/", FileOperations.AddSlash("a\\"));
        Assert.Equal("a/", FileOperations.AddSlash("a"));
        Assert.Equal("a/", FileOperations.AddSlash("a/"));
        Assert.Equal("", FileOperations.AddSlash(""));
    }

    [Fact]
    public void Quote_if_spaces_quotes_only_text_with_a_space()
    {
        Assert.Equal("\"a b\"", FileOperations.QuoteIfSpaces("a b"));
        Assert.Equal("ab", FileOperations.QuoteIfSpaces("ab"));
    }

    [Fact]
    public void File_length_is_zero_for_a_missing_file()
    {
        var path = _temp.Write("f", [1, 2, 3]);

        Assert.Equal(3u, FileOperations.GetFileLength(path));
        Assert.Equal(0u, FileOperations.GetFileLength(path + "missing"));
        Assert.True(FileOperations.DirectoryExists(_temp.Path));
    }
}
