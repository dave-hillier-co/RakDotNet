using System.Text;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Plugins;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// FileListTransfer between the systems, from Samples/FileListTransfer/main.cpp: the client sets up a receive from the
/// server, which sends a list of files whole, then files referenced on disk and read in chunks by an
/// IncrementalReadInterface, then one large enough to report split message progress. The files' bytes, names, contexts
/// and the progress each handler reports must agree, which also proves both sides hash files alike (SuperFastHash).
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class FileListTransferScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Files_sent_whole_arrive_with_their_bytes_and_contexts(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (receiver, sender) = await AttachAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        var readme = Encoding.ASCII.GetBytes("Hello, World!");
        var binary = Pattern(3000, 31);
        var big = Pattern(20000, 7);
        var setId = await receiver.SetupFileReceiveAsync(serverGuid, cancellationToken: Token);

        await sender.SendFilesAsync(clientGuid, setId,
        [
            new FileToSend("readme.txt", readme),
            new FileToSend("dir/b.bin", binary, Context: new FileListNodeContext(1, 2, 3, 0xdeadbeef)),
            new FileToSend("big.bin", big),
        ], cancellationToken: Token);

        var info = await WaitForAsync(receiver, i => i.Completed.Count == 1, "the set completed");
        Assert.Collection(info.Files,
            f => AssertFile(f, setId, 0, "readme.txt", readme, 13, default, serverGuid),
            f => AssertFile(f, setId, 1, "dir/b.bin", binary, 3013, new FileListNodeContext(1, 2, 3, 0xdeadbeef), serverGuid),
            f => AssertFile(f, setId, 2, "big.bin", big, 23013, default, serverGuid));
        Assert.All(info.Files, f => Assert.Equal((3u, 23013u), (f.NumberOfFilesInThisSet, f.ByteLengthOfThisSet)));
        // A whole file reports one part of one, with the file as the chunk (FileListTransfer.cpp:422-433).
        Assert.Equal(
            [(0u, 1u, 1u, 13u, SuperFastHash.Hash(readme)), (1u, 1u, 1u, 3000u, SuperFastHash.Hash(binary)), (2u, 1u, 1u, 20000u, SuperFastHash.Hash(big))],
            info.Progress.Select(p => (p.FileIndex, p.PartCount, p.PartTotal, p.DataChunkLength, p.IriHash ?? 0)));
        Assert.Equal([new CompletedRecord(setId, 3, 23013, serverGuid)], info.Completed);
        Assert.Equal([setId], info.Dereferenced);

        var sent = await WaitForAsync(sender, i => i.PushesComplete.Count == 1, "the sender's pushes completed");
        Assert.Equal([setId], sent.PushesComplete);
        Assert.Empty(sent.Pushes);
        AssertNothingReachedTheApplication(session);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Referenced_files_are_read_and_sent_a_chunk_at_a_time(PeerKind client, PeerKind server)
    {
        using var temp = new ScenarioDirectory();
        var small1 = Encoding.ASCII.GetBytes("one");
        var big = Pattern(250_000, 13);
        var small2 = Encoding.ASCII.GetBytes("two!");
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (receiver, sender) = await AttachAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        var setId = await receiver.SetupFileReceiveAsync(serverGuid, cancellationToken: Token);

        // Samples/FileListTransfer: files added as references, sent with an IncrementalReadInterface.
        await sender.SendFilesAsync(clientGuid, setId,
        [
            new FileToSend("s1.txt", Path: temp.Write("s1.txt", small1), Reference: true),
            new FileToSend("big.bin", Path: temp.Write("big.bin", big), Reference: true),
            new FileToSend("s2.txt", Path: temp.Write("s2.txt", small2), Reference: true),
        ], incremental: true, chunkSize: 100_000, cancellationToken: Token);

        var info = await WaitForAsync(receiver, i => i.Completed.Count == 1, "the set completed");
        // s1 goes whole with the first chunk of big.bin (FileListTransfer.cpp:986-1025); the rest are pushed.
        Assert.Collection(info.Files,
            f => AssertFile(f, setId, 0, "s1.txt", small1, 3, default, serverGuid),
            f => AssertFile(f, setId, 1, "big.bin", big, 250_003, default, serverGuid),
            f => AssertFile(f, setId, 2, "s2.txt", small2, 250_007, default, serverGuid));
        // OnReferencePush (FileListTransfer.cpp:816-846): part offset / chunk of length / chunk + 1, from each chunk's
        // own length.
        Assert.Equal(
            [
                ("s1.txt", 1u, 1u, 0u, 3u, SuperFastHash.Hash(small1)),
                ("big.bin", 0u, 3u, 0u, 100_000u, SuperFastHash.Hash(big.AsSpan(0, 100_000))),
                ("big.bin", 1u, 3u, 100_000u, 200_000u, SuperFastHash.Hash(big.AsSpan(100_000, 100_000))),
                ("big.bin", 4u, 6u, 200_000u, 250_000u, SuperFastHash.Hash(big.AsSpan(200_000))),
                ("s2.txt", 0u, 1u, 0u, 4u, SuperFastHash.Hash(small2)),
            ],
            info.Progress.Select(p => (p.FileName, p.PartCount, p.PartTotal, p.IriWriteOffset, p.BytesDownloadedForThisFile, p.IriHash ?? 0)));
        Assert.Equal([new CompletedRecord(setId, 3, 250_007, serverGuid)], info.Completed);

        var sent = await WaitForAsync(sender, i => i.PushesComplete.Count == 1, "the sender's pushes completed");
        Assert.Equal(
            [
                new PushRecord("big.bin", 250_000, 0, 100_000, false, setId),
                new PushRecord("big.bin", 250_000, 100_000, 100_000, false, setId),
                new PushRecord("big.bin", 250_000, 200_000, 50_000, true, setId),
                new PushRecord("s2.txt", 4, 0, 4, true, setId),
            ],
            sent.Pushes);
        Assert.Equal(0u, (await sender.GetFileTransferInfoAsync(target: clientGuid, cancellationToken: Token)).PendingFiles);
        AssertNothingReachedTheApplication(session);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_large_file_reports_split_message_progress_before_it_arrives(PeerKind client, PeerKind server)
    {
        var large = Pattern(60_000, 3);
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (receiver, sender) = await AttachAsync(session);
        // Samples/FileListTransfer: SetSplitMessageProgressInterval(9).
        await session.Client.SetSplitMessageProgressIntervalAsync(9, Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        var setId = await receiver.SetupFileReceiveAsync(serverGuid, cancellationToken: Token);

        await sender.SendFilesAsync(clientGuid, setId, [new FileToSend("large.bin", large, Context: new FileListNodeContext(5, 6, 7, 8))], cancellationToken: Token);

        var info = await WaitForAsync(receiver, i => i.Completed.Count == 1, "the set completed");
        AssertFile(Assert.Single(info.Files), setId, 0, "large.bin", large, 60_000, new FileListNodeContext(5, 6, 7, 8), serverGuid);
        // ID_DOWNLOAD_PROGRESS of ID_FILE_LIST_TRANSFER_FILE (FileListTransfer.cpp:464-488): the header is read from the
        // first part, and no chunk is given.
        var partial = info.Progress.Where(p => !p.HasIriDataChunk).ToList();
        Assert.NotEmpty(partial);
        Assert.All(partial, p =>
        {
            Assert.Equal(("large.bin", 0u, setId), (p.FileName, p.FileIndex, p.SetId));
            Assert.True(p.PartCount < p.PartTotal, $"part {p.PartCount} of {p.PartTotal}");
            Assert.True(p.PartCount % 9 == 0, $"part {p.PartCount} is not a multiple of the interval");
        });
        Assert.Equal((1u, 1u), (info.Progress[^1].PartCount, info.Progress[^1].PartTotal));
        Assert.DoesNotContain(session.ClientInbox.Unconsumed, p => p.MessageId == (byte)Protocol.Wire.MessageId.DownloadProgress);
    }

    private static async Task<(IFileTransferDriver Receiver, IFileTransferDriver Sender)> AttachAsync(InteropSession session)
    {
        var receiver = (IFileTransferDriver)session.Client;
        var sender = (IFileTransferDriver)session.Server;
        await receiver.AttachFileListTransferAsync(Token);
        await sender.AttachFileListTransferAsync(Token);
        return (receiver, sender);
    }

    internal static void AssertFile(FileRecord file, ushort setId, uint index, string name, byte[] contents, uint bytesDownloadedForThisSet,
                                    FileListNodeContext context, ulong senderGuid)
    {
        Assert.Equal((setId, index, name), (file.SetId, file.FileIndex, file.FileName));
        Assert.Equal((uint)contents.Length, file.ByteLengthOfThisFile);
        Assert.Equal((uint)contents.Length, file.BytesDownloadedForThisFile);
        Assert.Equal(bytesDownloadedForThisSet, file.BytesDownloadedForThisSet);
        Assert.Equal(context, file.Context);
        Assert.Equal(senderGuid, file.SenderGuid);
        Assert.True(file.HasData);
        Assert.Equal(SuperFastHash.Hash(contents), file.Hash);
        if (contents.Length <= 1024)
            Assert.Equal(contents, file.Data);
    }

    internal static async Task<FileTransferInfo> WaitForAsync(IFileTransferDriver driver, Func<FileTransferInfo, bool> condition, string expectation)
    {
        var deadline = DateTime.UtcNow + InteropSession.DefaultTimeout;
        while (true)
        {
            var info = await driver.GetFileTransferInfoAsync(cancellationToken: Token);
            if (condition(info))
                return info;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {expectation}; last saw {info.Files.Count} files, {info.Progress.Count} progress reports, {info.Completed.Count} completions, {info.Pushes.Count} pushes.");
            await Task.Delay(20, Token);
        }
    }

    // The plugin consumes every file message (FileListTransfer.cpp:492-526).
    private static void AssertNothingReachedTheApplication(InteropSession session) =>
        Assert.DoesNotContain(session.ClientInbox.Unconsumed.Concat(session.ServerInbox.Unconsumed), p => p.MessageId is
            (byte)Protocol.Wire.MessageId.FileListTransferHeader or (byte)Protocol.Wire.MessageId.FileListTransferFile
            or (byte)Protocol.Wire.MessageId.FileListReferencePush or (byte)Protocol.Wire.MessageId.FileListReferencePushAck);

    internal static byte[] Pattern(int length, int multiplier)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = unchecked((byte)(i * multiplier + 7));
        return bytes;
    }
}

/// <summary>A fresh directory under the system's temp directory, deleted with everything in it on dispose.</summary>
internal sealed class ScenarioDirectory : IDisposable
{
    public ScenarioDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), $"rakdotnet-interop-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string relativePath, byte[] contents)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, contents);
        return full;
    }

    public string Write(string relativePath, string contents) => Write(relativePath, Encoding.UTF8.GetBytes(contents));

    public byte[]? Read(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
