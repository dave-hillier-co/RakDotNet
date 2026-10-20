using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.FileListTransfer;

public sealed class FileListTests : IDisposable
{
    // SuperFastHash("abcde") big-endian, as native FileList stores it (tools/file-vectors, hash_abcde_bytes).
    private static readonly byte[] AbcdeHash = [0x51, 0xed, 0x07, 0x2e];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Serializes_three_files_as_native_does()
    {
        // tools/file-vectors, filelist_three: data with a hash prefix, a hash only, and a name only.
        var list = new FileList();
        list.AddFile("dir/a.txt", "full/dir/a.txt", [.. AbcdeHash, .. "abcde"u8], 5, new FileListNodeContext(7, 1, 0x12345678, 3));
        list.AddFile("b.bin", "b.bin", AbcdeHash, 70000, default);
        list.AddFile("dir/sub/c", "dir/sub/c", [], 0, new FileListNodeContext(255, 0xffffffff, 0, 0));
        using var stream = new BitStream();

        list.Serialize(stream);

        Assert.Equal(660, stream.BitsWritten);
        Assert.Equal(
            "00000001dc00000002123456780000001c393338cf5d4ed5c00000012a3da0e5cc2c4c6c8ca00000002c3c3c0000000407bc7a12b0000000228f683970000222e0ff7ffffffff80000000e1c999cdacf9ca150",
            Convert.ToHexStringLower(stream.Written));
    }

    [Fact]
    public void Serializes_an_empty_list_as_native_does()
    {
        using var stream = new BitStream();

        new FileList().Serialize(stream);

        // tools/file-vectors, filelist_empty.
        Assert.Equal(8, stream.BitsWritten);
        Assert.Equal("f0", Convert.ToHexStringLower(stream.Written));
    }

    [Fact]
    public void Deserializes_the_native_bytes_into_the_files()
    {
        var bytes = Convert.FromHexString("00000001dc00000002123456780000001c393338cf5d4ed5c00000012a3da0e5cc2c4c6c8ca00000002c3c3c0000000407bc7a12b0000000228f683970000222e0ff7ffffffff80000000e1c999cdacf9ca150");
        var reader = new BitStreamReader(bytes, 660);
        var list = new FileList();

        Assert.True(list.TryDeserialize(ref reader));

        Assert.Collection(list.Files,
            a =>
            {
                Assert.Equal("dir/a.txt", a.Filename);
                Assert.Equal("dir/a.txt", a.FullPathToFile);
                Assert.Equal([.. AbcdeHash, .. "abcde"u8], a.Data!);
                Assert.Equal(9u, a.DataLengthBytes);
                Assert.Equal(5u, a.FileLengthBytes);
                // The third extra value is not sent (FileList.cpp:394-396).
                Assert.Equal(new FileListNodeContext(7, 1, 0x12345678, 0), a.Context);
            },
            b =>
            {
                Assert.Equal("b.bin", b.Filename);
                Assert.Equal(AbcdeHash, b.Data);
                Assert.Equal(70000u, b.FileLengthBytes);
            },
            c =>
            {
                Assert.Equal("dir/sub/c", c.Filename);
                Assert.Null(c.Data);
                Assert.Equal(0u, c.DataLengthBytes);
                Assert.Equal(0u, c.FileLengthBytes);
                Assert.Equal(new FileListNodeContext(255, 0xffffffff, 0, 0), c.Context);
            });
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void A_truncated_list_fails_and_is_left_empty()
    {
        var bytes = Convert.FromHexString("00000001dc00000002123456780000001c393338cf5d4ed5c00000012a3da0e5cc2c4c6c8ca00000002c3c3c0000000407bc7a12b0000000228f683970000222e0ff7ffffffff80000000e1c999cdacf9ca150");
        var reader = new BitStreamReader(bytes, 600);
        var list = new FileList();

        Assert.False(list.TryDeserialize(ref reader));

        Assert.Empty(list.Files);
    }

    [Fact]
    public void A_list_of_more_than_10000_files_is_refused()
    {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new BitStreamWriter(buffer);
        writer.WriteCompressed(10001u);
        var reader = new BitStreamReader(writer.Written, writer.BitsWritten);

        Assert.False(new FileList().TryDeserialize(ref reader));
    }

    [Fact]
    public void A_data_length_beyond_the_message_is_refused_without_allocating_it()
    {
        Span<byte> buffer = stackalloc byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteCompressed(1u);
        writer.WriteCompressed((byte)0);
        writer.WriteCompressed(0u);
        writer.WriteCompressed(0u);
        writer.WriteCompressedString("x".AsSpan(), FileList.MaxFilenameLength);
        writer.WriteBool(true);
        writer.WriteCompressed(1_999_999_999u);
        var reader = new BitStreamReader(writer.Written, writer.BitsWritten);

        Assert.False(new FileList().TryDeserialize(ref reader));
    }

    [Fact]
    public void Adding_the_same_file_again_keeps_one_and_different_contents_replace_it()
    {
        var list = new FileList();
        list.AddFile("a", "a", "one"u8, 3, default);
        list.AddFile("b", "b", "two"u8, 3, default);

        list.AddFile("a", "a", "one"u8, 3, default);
        Assert.Equal(["a", "b"], list.Files.Select(f => f.Filename));

        // FileList.cpp:188-191: the old entry is removed and the new one appended.
        list.AddFile("a", "a", "uno"u8, 3, default);
        Assert.Equal(["b", "a"], list.Files.Select(f => f.Filename));
        Assert.Equal("uno"u8.ToArray(), list.Files[1].Data);
    }

    [Fact]
    public void A_name_longer_than_512_bytes_is_not_added()
    {
        var list = new FileList();

        list.AddFile(new string('x', 513), "x", "a"u8, 1, default);

        Assert.Empty(list.Files);
    }

    [Fact]
    public void Adds_files_from_a_directory_breadth_first_with_names_relative_to_the_application_directory()
    {
        _temp.Write("app/levels/one.txt", "first level");
        _temp.Write("app/levels/deeper/two.txt", "second");
        _temp.Write("app/other.txt", "not under levels");
        var list = new FileList();
        var progress = new RecordingProgress();
        list.AddCallback(progress);

        list.AddFilesFromDirectory(Path.Combine(_temp.Path, "app"), "levels", writeHash: true, writeData: true, recursive: true, default);

        var one = Assert.Single(list.Files, f => f.Filename == "levels/one.txt");
        Assert.Equal(11u, one.FileLengthBytes);
        Assert.Equal(15u, one.DataLengthBytes);
        Assert.Equal(SuperFastHash.Hash("first level"u8), BinaryPrimitives.ReadUInt32BigEndian(one.Data));
        Assert.Equal("first level"u8.ToArray(), one.Data![4..]);
        Assert.Equal(Path.Combine(_temp.Path, "app") + "/levels/one.txt", one.FullPathToFile);
        Assert.Contains(list.Files, f => f.Filename == "levels/deeper/two.txt");
        Assert.Equal(2, list.Files.Count);
        // The top directory's files come before the subdirectory's.
        Assert.Equal("levels/deeper/two.txt", list.Files[^1].Filename);
        Assert.Equal(Path.Combine(_temp.Path, "app") + "/levels/", progress.Started);
        Assert.Equal(2, progress.Directories.Count);
        Assert.Equal(2, progress.Files.Count);
    }

    [Fact]
    public void Hash_only_and_name_only_lists_hold_what_was_asked_for()
    {
        _temp.Write("data/file.bin", "abcde");

        var hashes = new FileList();
        hashes.AddFilesFromDirectory(_temp.Path, "data", writeHash: true, writeData: false, recursive: true, default);
        var names = new FileList();
        names.AddFilesFromDirectory(_temp.Path, "data", writeHash: false, writeData: false, recursive: true, default);
        var contents = new FileList();
        contents.AddFilesFromDirectory(_temp.Path, "data", writeHash: false, writeData: true, recursive: true, default);

        Assert.Equal(AbcdeHash, Assert.Single(hashes.Files).Data);
        Assert.Equal(5u, hashes.Files[0].FileLengthBytes);
        Assert.Null(Assert.Single(names.Files).Data);
        Assert.Equal(5u, names.Files[0].FileLengthBytes);
        Assert.Equal("abcde"u8.ToArray(), Assert.Single(contents.Files).Data);
    }

    [Fact]
    public void A_directory_that_cannot_be_opened_adds_nothing()
    {
        var list = new FileList();

        list.AddFilesFromDirectory(_temp.Path, "missing", writeHash: true, writeData: false, recursive: true, default);

        Assert.Empty(list.Files);
    }

    [Fact]
    public void The_delta_holds_the_files_the_other_list_lacks_or_holds_differently()
    {
        // DirectoryDeltaTransfer's use: the uploader's names start with its subdirectory, the downloader's with its output one.
        var uploads = new FileList();
        uploads.AddFile("Mods/same.txt", "/srv/Mods/same.txt", AbcdeHash, 5, default);
        uploads.AddFile("Mods/changed.txt", "/srv/Mods/changed.txt", AbcdeHash, 5, default);
        uploads.AddFile("Mods/Skins/new.txt", "/srv/Mods/Skins/new.txt", AbcdeHash, 5, default);
        uploads.AddFile("Levels/elsewhere.txt", "/srv/Levels/elsewhere.txt", AbcdeHash, 5, default);
        var local = new FileList();
        local.AddFile("Out/SAME.txt", "x", AbcdeHash, 5, default);
        local.AddFile("Out/changed.txt", "x", [1, 2, 3, 4], 5, default);
        var delta = new FileList();

        uploads.GetDeltaToCurrent(local, delta, "Mods", "Out/");

        Assert.Equal(["Mods/changed.txt", "Mods/Skins/new.txt"], delta.Files.Select(f => f.Filename));
        Assert.All(delta.Files, f => Assert.Null(f.Data));
        Assert.Equal("/srv/Mods/Skins/new.txt", delta.Files[1].FullPathToFile);
        Assert.Equal(5u, delta.Files[1].FileLengthBytes);
    }

    [Fact]
    public void Lists_files_missing_or_changed_on_disk()
    {
        _temp.Write("same.txt", "abcde");
        _temp.Write("changed.txt", "abcdX");
        _temp.Write("longer.txt", "abcdef");
        var expected = new FileList();
        expected.AddFile("same.txt", "same.txt", AbcdeHash, 5, default);
        expected.AddFile("changed.txt", "changed.txt", AbcdeHash, 5, default);
        expected.AddFile("longer.txt", "longer.txt", AbcdeHash, 5, default);
        expected.AddFile("missing.txt", "missing.txt", AbcdeHash, 5, default);
        var differences = new FileList();

        expected.ListMissingOrChangedFiles(_temp.Path, differences, alwaysWriteHash: false, neverWriteHash: false);

        Assert.Equal(["changed.txt", "longer.txt", "missing.txt"], differences.Files.Select(f => f.Filename));
        var changed = differences.Files[0];
        Assert.Equal(SuperFastHash.Hash("abcdX"u8), BinaryPrimitives.ReadUInt32BigEndian(changed.Data));
        Assert.Null(differences.Files[1].Data);
        Assert.Equal(6u, differences.Files[1].FileLengthBytes);
        Assert.Equal(0u, differences.Files[2].FileLengthBytes);
    }

    [Fact]
    public void Populates_data_from_disk_and_drops_unknown_files()
    {
        _temp.Write("a.txt", "abcde");
        var list = new FileList();
        list.AddFile("a.txt", "a.txt", [], 0, default);
        list.AddFile("gone.txt", "gone.txt", [], 0, default);

        list.PopulateDataFromDisk(_temp.Path, writeFileData: true, writeFileHash: false, removeUnknownFiles: true);

        var a = Assert.Single(list.Files);
        Assert.Equal("abcde"u8.ToArray(), a.Data);
        Assert.Equal(5u, a.DataLengthBytes);
        Assert.Equal(5u, a.FileLengthBytes);
    }

    [Fact]
    public void Populating_the_hash_alone_stores_four_bytes()
    {
        _temp.Write("a.txt", "abcde");
        var list = new FileList();
        list.AddFile("a.txt", "a.txt", [], 0, default);

        list.PopulateDataFromDisk(_temp.Path, writeFileData: false, writeFileHash: true, removeUnknownFiles: false);

        Assert.Equal(AbcdeHash, list.Files[0].Data);
        Assert.Equal(4u, list.Files[0].DataLengthBytes);
    }

    [Fact]
    public void Flagging_files_as_references_sets_their_data_length_to_the_file_length()
    {
        var list = new FileList();
        list.AddFile("a", "a", [], 1234, default);

        list.FlagFilesAsReferences();

        Assert.True(list.Files[0].IsAReference);
        Assert.Equal(1234u, list.Files[0].DataLengthBytes);
    }

    [Fact]
    public void Writes_data_to_disk_creating_directories()
    {
        var list = new FileList();
        list.AddFile("x/y/z.bin", "x/y/z.bin", [1, 2, 3], 3, default);

        list.WriteDataToDisk(Path.Combine(_temp.Path, "out"));

        Assert.Equal([1, 2, 3], _temp.Read("out/x/y/z.bin"));
    }

    [Theory]
    [InlineData("../escaped.bin")]
    [InlineData("a/../../escaped.bin")]
    [InlineData("a\\..\\..\\escaped.bin")]
    public void A_name_with_dot_dot_cancels_the_write(string name)
    {
        var list = new FileList();
        list.AddFile("first.bin", "first.bin", [1], 1, default);
        list.AddFile(name, name, [2], 1, default);
        list.AddFile("last.bin", "last.bin", [3], 1, default);

        list.WriteDataToDisk(Path.Combine(_temp.Path, "out"));

        Assert.True(_temp.Exists("out/first.bin"));
        Assert.False(_temp.Exists("escaped.bin"));
        Assert.False(_temp.Exists("out/last.bin"));
    }

    [Fact]
    public void A_rooted_name_is_written_inside_the_directory()
    {
        var list = new FileList();
        var rooted = Path.Combine(_temp.Path, "rooted.bin");
        list.AddFile(rooted, rooted, [9], 1, default);

        list.WriteDataToDisk(Path.Combine(_temp.Path, "out"));

        Assert.False(File.Exists(rooted));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "out") + "/" + rooted));
    }

    [Fact]
    public void Deletes_the_files_under_the_directory()
    {
        _temp.Write("d/a.txt", "a");
        _temp.Write("d/b.txt", "b");
        var list = new FileList();
        list.AddFile("a.txt", "a.txt", [], 1, default);

        list.DeleteFiles(Path.Combine(_temp.Path, "d"));

        Assert.False(_temp.Exists("d/a.txt"));
        Assert.True(_temp.Exists("d/b.txt"));
    }

    [Fact]
    public void A_name_with_dot_dot_cancels_the_deletion()
    {
        _temp.Write("keep.txt", "k");
        var list = new FileList();
        list.AddFile("../keep.txt", "x", [], 1, default);

        list.DeleteFiles(Path.Combine(_temp.Path, "d"));

        Assert.True(_temp.Exists("keep.txt"));
    }

    [Fact]
    public void Fix_ending_slash_appends_one_unless_present()
    {
        var slash = OperatingSystem.IsWindows() ? "\\" : "/";
        Assert.Equal("a" + slash, FileList.FixEndingSlash("a"));
        Assert.Equal("a/", FileList.FixEndingSlash("a/"));
        Assert.Equal("a\\", FileList.FixEndingSlash("a\\"));
        Assert.Equal("", FileList.FixEndingSlash(""));
    }

    private sealed class RecordingProgress : IFileListProgress
    {
        public string? Started { get; private set; }

        public List<string> Directories { get; } = [];

        public List<string> Files { get; } = [];

        public void OnAddFilesFromDirectoryStarted(FileList fileList, string directory) => Started = directory;

        public void OnDirectory(FileList fileList, string directory, uint directoriesRemaining) => Directories.Add(directory);

        public void OnFile(FileList fileList, string directory, string fileName, uint fileSize) => Files.Add(fileName);
    }
}
