using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// DirectoryDeltaTransfer, from Samples/DirectoryDeltaTransfer/DirectoryDeltaTransferTest.cpp driven without its
/// keyboard: the server sets its application directory and offers a subdirectory, and the client downloads it into its
/// own. Only files the client lacks or holds differently travel, which the client decides by sending its files' hashes,
/// so an unchanged file staying put proves both implementations hash and compare alike.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class DirectoryDeltaTransferScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Only_the_files_missing_or_different_are_downloaded(PeerKind client, PeerKind server)
    {
        using var temp = new ScenarioDirectory();
        var skin = FileListTransferScenarios.Pattern(5000, 11);
        temp.Write("server/Mods/same.txt", "unchanged");
        temp.Write("server/Mods/changed.txt", "new contents");
        temp.Write("server/Mods/new.txt", "brand new");
        temp.Write("server/Mods/Skins/red.skin", skin);
        temp.Write("server/Levels/level1.map", "not offered");
        temp.Write("client/Mods/same.txt", "unchanged");
        temp.Write("client/Mods/changed.txt", "old contents");
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (downloader, uploader) = await AttachAsync(session, temp);
        Assert.Equal(4, await uploader.AddDdtUploadsAsync("Mods", Token));
        var (serverGuid, _) = await session.ConnectAsync(Token);

        var setId = await downloader.DdtDownloadAsync("Mods", "Mods", prependAppDir: true, serverGuid, cancellationToken: Token);

        var info = await FileListTransferScenarios.WaitForAsync(downloader, i => i.Completed.Count == 1, "the download completed");
        Assert.Equal(["Mods/Skins/red.skin", "Mods/changed.txt", "Mods/new.txt"], info.Files.Select(f => f.FileName).Order(StringComparer.Ordinal));
        Assert.All(info.Files, f => Assert.Equal((setId, 3u, serverGuid), (f.SetId, f.NumberOfFilesInThisSet, f.SenderGuid)));
        Assert.Equal(new CompletedRecord(setId, 3, 5000 + 12 + 9, serverGuid), info.Completed[0]);
        Assert.Equal("new contents"u8.ToArray(), temp.Read("client/Mods/changed.txt"));
        Assert.Equal("brand new"u8.ToArray(), temp.Read("client/Mods/new.txt"));
        Assert.Equal(skin, temp.Read("client/Mods/Skins/red.skin"));
        Assert.Null(temp.Read("client/Mods/level1.map"));
        // The request is the uploader's (DirectoryDeltaTransfer.cpp:232-242).
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == (byte)MessageId.DdtDownloadRequest);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task An_uploader_with_an_incremental_reader_sends_its_files_in_chunks(PeerKind client, PeerKind server)
    {
        using var temp = new ScenarioDirectory();
        var big = FileListTransferScenarios.Pattern(25_000, 17);
        temp.Write("server/Mods/big.bin", big);
        temp.Write("server/Mods/small.txt", "small");
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (downloader, uploader) = await AttachAsync(session, temp);
        // DirectoryDeltaTransferTest.cpp: SetDownloadRequestIncrementalReadInterface(&iri, 1000000), with smaller chunks.
        await uploader.SetDdtIncrementalReadAsync(10_000, Token);
        Assert.Equal(2, await uploader.AddDdtUploadsAsync("Mods", Token));
        var (serverGuid, _) = await session.ConnectAsync(Token);

        await downloader.DdtDownloadAsync("Mods", "Downloaded", prependAppDir: true, serverGuid, cancellationToken: Token);

        var info = await FileListTransferScenarios.WaitForAsync(downloader, i => i.Completed.Count == 1, "the download completed");
        Assert.Equal(big, temp.Read("client/Downloaded/big.bin"));
        Assert.Equal("small"u8.ToArray(), temp.Read("client/Downloaded/small.txt"));
        Assert.Equal(
            [(0u, 10_000u), (10_000u, 10_000u), (20_000u, 5_000u)],
            info.Progress.Where(p => p.FileName == "Mods/big.bin").Select(p => (p.IriWriteOffset, p.BytesDownloadedForThisFile - p.IriWriteOffset)));
    }

    [Theory]
    [InlineData(PeerKind.RakDotNet, PeerKind.Native)]
    [InlineData(PeerKind.RakDotNet, PeerKind.RakDotNet)]
    public async Task A_file_named_outside_the_output_directory_is_not_written(PeerKind client, PeerKind server)
    {
        // RakNet's DDTCallback writes wherever the name points (DirectoryDeltaTransfer.cpp:44-56); RakDotNet writes only
        // inside the output directory, so only RakDotNet downloaders are tested. The server answers the request with
        // FileListTransfer alone, naming a file that climbs out.
        using var temp = new ScenarioDirectory();
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var downloader = (IFileTransferDriver)session.Client;
        var hostile = (IFileTransferDriver)session.Server;
        await downloader.AttachDirectoryDeltaTransferAsync(Token);
        await downloader.SetDdtApplicationDirectoryAsync(Path.Combine(temp.Root, "client"), Token);
        await hostile.AttachFileListTransferAsync(Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        await downloader.DdtDownloadAsync("Mods", "Out", prependAppDir: true, serverGuid, cancellationToken: Token);
        var request = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DdtDownloadRequest, "ID_DDT_DOWNLOAD_REQUEST", InteropSession.DefaultTimeout, Token);
        var setId = BinaryPrimitives.ReadUInt16BigEndian(request.Data.Span[1..]);
        await hostile.SendFilesAsync(clientGuid, setId, [new FileToSend("Mods/../../escaped.txt", "pwned"u8.ToArray())], cancellationToken: Token);

        var info = await FileListTransferScenarios.WaitForAsync(downloader, i => i.Completed.Count == 1, "the download completed");
        Assert.Equal("Mods/../../escaped.txt", Assert.Single(info.Files).FileName);
        Assert.Null(temp.Read("escaped.txt"));
        Assert.Null(temp.Read("client/escaped.txt"));
        Assert.Null(temp.Read("client/Out/escaped.txt"));
    }

    private static async Task<(IFileTransferDriver Downloader, IFileTransferDriver Uploader)> AttachAsync(InteropSession session, ScenarioDirectory temp)
    {
        var downloader = (IFileTransferDriver)session.Client;
        var uploader = (IFileTransferDriver)session.Server;
        await downloader.AttachDirectoryDeltaTransferAsync(Token);
        await uploader.AttachDirectoryDeltaTransferAsync(Token);
        await downloader.SetDdtApplicationDirectoryAsync(Path.Combine(temp.Root, "client"), Token);
        await uploader.SetDdtApplicationDirectoryAsync(Path.Combine(temp.Root, "server"), Token);
        return (downloader, uploader);
    }
}
