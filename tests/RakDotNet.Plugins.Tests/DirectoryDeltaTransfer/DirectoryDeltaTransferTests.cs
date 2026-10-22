using RakDotNet.Plugins.Tests.FileListTransfer;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.DirectoryDeltaTransfer;

/// <summary>
/// DirectoryDeltaTransfer between an uploader and a downloader, from Samples/DirectoryDeltaTransfer: set the application
/// directory, offer a subdirectory, download it into another, with and without an incremental reader.
/// </summary>
public sealed class DirectoryDeltaTransferTests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly TempDirectory _temp = new();
    private readonly Plugins.FileListTransfer _uploaderTransfer = new();
    private readonly Plugins.FileListTransfer _downloaderTransfer = new();
    private readonly Plugins.DirectoryDeltaTransfer _uploader = new();
    private readonly Plugins.DirectoryDeltaTransfer _downloader = new();
    private readonly RakPeer _uploaderPeer;
    private readonly RakPeer _downloaderPeer;
    private readonly RecordingHandler _handler = new();

    public DirectoryDeltaTransferTests()
    {
        _uploaderPeer = _mesh.Add(maxIncoming: 4, _uploader, _uploaderTransfer);
        _downloaderPeer = _mesh.Add(maxIncoming: 4, _downloader, _downloaderTransfer);
        _uploader.FileListTransfer = _uploaderTransfer;
        _downloader.FileListTransfer = _downloaderTransfer;
        _uploader.ApplicationDirectory = Path.Combine(_temp.Path, "server");
        _downloader.ApplicationDirectory = Path.Combine(_temp.Path, "client");
    }

    public void Dispose()
    {
        _mesh.Dispose();
        _uploaderTransfer.Dispose();
        _downloaderTransfer.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Downloads_every_file_of_the_subdirectory_into_the_output_directory()
    {
        _temp.Write("server/Mods/readme.txt", "hello");
        _temp.Write("server/Mods/Skins/red.skin", SuperFastHashTests.Pattern(5000));
        _temp.Write("server/Levels/level1.map", "not offered");
        _uploader.AddUploadsFromSubdirectory("Mods");
        Assert.Equal(2, _uploader.NumberOfFilesForUpload);
        var host = Connect();

        var setId = _downloader.DownloadFromSubdirectory("Mods", "Downloaded", true, host, _handler, Priority.High, 0, null);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the download completed");

        Assert.Equal(0, setId);
        Assert.Equal("hello"u8.ToArray(), _temp.Read("client/Downloaded/readme.txt"));
        Assert.Equal(SuperFastHashTests.Pattern(5000), _temp.Read("client/Downloaded/Skins/red.skin"));
        Assert.False(_temp.Exists("client/Downloaded/level1.map"));
        Assert.Equal(["Mods/readme.txt", "Mods/Skins/red.skin"], _handler.Files.Select(f => f.FileName).Order());
        Assert.DoesNotContain(_mesh.Seen(_uploaderPeer), e => e.Id == (byte)MessageId.DdtDownloadRequest);
    }

    [Fact]
    public void Only_files_missing_or_different_are_sent()
    {
        _temp.Write("server/Mods/same.txt", "unchanged");
        _temp.Write("server/Mods/changed.txt", "new contents");
        _temp.Write("server/Mods/missing.txt", "missing");
        _temp.Write("client/Mods/same.txt", "unchanged");
        _temp.Write("client/Mods/changed.txt", "old contents");
        _uploader.AddUploadsFromSubdirectory("Mods");
        var host = Connect();

        _downloader.DownloadFromSubdirectory("Mods", "Mods", true, host, _handler, Priority.High, 0, null);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the download completed");

        Assert.Equal(["Mods/changed.txt", "Mods/missing.txt"], _handler.Files.Select(f => f.FileName).Order());
        Assert.Equal("new contents"u8.ToArray(), _temp.Read("client/Mods/changed.txt"));
        Assert.Equal("missing"u8.ToArray(), _temp.Read("client/Mods/missing.txt"));
    }

    [Fact]
    public void Nothing_to_send_completes_an_empty_set()
    {
        _temp.Write("server/Mods/same.txt", "unchanged");
        _temp.Write("client/Mods/same.txt", "unchanged");
        _uploader.AddUploadsFromSubdirectory("Mods");
        var host = Connect();

        _downloader.DownloadFromSubdirectory("Mods", "Mods", true, host, _handler, Priority.High, 0, null);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the download completed");

        Assert.Empty(_handler.Files);
        Assert.Equal(0u, _handler.Completed[0].NumberOfFilesInThisSet);
    }

    [Fact]
    public void An_uploader_with_an_incremental_reader_sends_in_chunks()
    {
        // DirectoryDeltaTransferTest.cpp: SetDownloadRequestIncrementalReadInterface(&iri, 1000000), here with smaller chunks.
        var big = SuperFastHashTests.Pattern(25_000);
        _temp.Write("server/Mods/big.bin", big);
        _temp.Write("server/Mods/small.txt", "small");
        _uploader.SetDownloadRequestIncrementalReader(new FileIncrementalReader(), 10_000);
        _uploader.AddUploadsFromSubdirectory("Mods");
        var host = Connect();

        _downloader.DownloadFromSubdirectory("Mods", "Mods", true, host, _handler, Priority.High, 0, null);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the download completed");

        Assert.Equal(big, _temp.Read("client/Mods/big.bin"));
        Assert.Equal("small"u8.ToArray(), _temp.Read("client/Mods/small.txt"));
        Assert.Contains(_handler.Progress, p => p.File.FileName == "Mods/big.bin" && p.IriWriteOffset == 10_000);
    }

    [Fact]
    public void A_download_of_a_more_specific_subdirectory_gets_only_its_files()
    {
        _temp.Write("server/Mods/top.txt", "top");
        _temp.Write("server/Mods/Skins/red.skin", "red");
        _uploader.AddUploadsFromSubdirectory("Mods");
        var host = Connect();

        _downloader.DownloadFromSubdirectory("Mods/Skins", "Skins", true, host, _handler, Priority.High, 0, null);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the download completed");

        Assert.Equal(["Mods/Skins/red.skin"], _handler.Files.Select(f => f.FileName));
        Assert.Equal("red"u8.ToArray(), _temp.Read("client/Skins/red.skin"));
        Assert.False(_temp.Exists("client/Skins/top.txt"));
    }

    [Theory]
    [InlineData("Mods/../../escaped.txt")]
    [InlineData("Mods/../../../escaped.txt")]
    [InlineData("Mods/..\\..\\escaped.txt")]
    public void A_file_named_outside_the_output_directory_is_not_written(string name)
    {
        // A hostile uploader answers the request through its FileListTransfer with a name that climbs out.
        using var hostileTransfer = new Plugins.FileListTransfer();
        var hostile = new HostileUploader(hostileTransfer, name);
        var hostilePeer = _mesh.Add(maxIncoming: 4, hostile, hostileTransfer);
        _mesh.Connect(_downloaderPeer, hostilePeer);
        Assert.True(_downloaderPeer.TryGetConnection(hostilePeer.RakNetGuid, out var connection));

        _downloader.DownloadFromSubdirectory("Mods", "Out", true, connection.Address, _handler, Priority.High, 0, null);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the download completed");

        Assert.False(File.Exists(Path.Combine(_temp.Path, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "client", "escaped.txt")));
        // The handler still gets the file, as RakNet's DDTCallback passes it on (DirectoryDeltaTransfer.cpp:44-56).
        Assert.Equal(name, Assert.Single(_handler.Files).FileName);
    }

    [Fact]
    public void Clearing_uploads_offers_nothing()
    {
        _temp.Write("server/Mods/a.txt", "a");
        _uploader.AddUploadsFromSubdirectory("Mods");

        _uploader.ClearUploads();

        Assert.Equal(0, _uploader.NumberOfFilesForUpload);
    }

    [Fact]
    public void The_application_directory_gets_a_trailing_slash()
    {
        _uploader.ApplicationDirectory = "/srv/game";
        Assert.Equal("/srv/game" + Path.DirectorySeparatorChar, _uploader.ApplicationDirectory);
        _uploader.ApplicationDirectory = "C:\\Games\\";
        Assert.Equal("C:\\Games\\", _uploader.ApplicationDirectory);
        _uploader.ApplicationDirectory = "";
        Assert.Equal("", _uploader.ApplicationDirectory);
    }

    private SystemAddress Connect()
    {
        _mesh.Connect(_downloaderPeer, _uploaderPeer);
        Assert.True(_downloaderPeer.TryGetConnection(_uploaderPeer.RakNetGuid, out var connection));
        return connection.Address;
    }

    /// <summary>Answers ID_DDT_DOWNLOAD_REQUEST with one file under the given name.</summary>
    private sealed class HostileUploader(Plugins.FileListTransfer transfer, string name) : IRakPlugin
    {
        public PluginResult OnEvent(PeerEvent e)
        {
            if (e.Data.IsEmpty || e.Data[0] != (byte)MessageId.DdtDownloadRequest)
                return PluginResult.Continue;
            var reader = e.GetReader();
            reader.IgnoreBits(8);
            Assert.True(reader.TryReadUInt16(out var setId));
            var list = new FileList();
            list.AddFile(name, name, "pwned"u8, 5, default);
            transfer.Send(list, e.Address, setId, Priority.High, 0);
            return PluginResult.Consume;
        }
    }
}
