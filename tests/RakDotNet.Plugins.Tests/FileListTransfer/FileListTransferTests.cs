using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.FileListTransfer;

/// <summary>
/// FileListTransfer between two peers, from Samples/FileListTransfer: files sent whole, a file referenced and sent
/// incrementally, and the progress reports on both sides.
/// </summary>
public sealed class FileListTransferTests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly Plugins.FileListTransfer _sender = new();
    private readonly Plugins.FileListTransfer _receiver = new();
    private readonly RakPeer _senderPeer;
    private readonly RakPeer _receiverPeer;
    private readonly RecordingHandler _handler = new();
    private readonly RecordingSendProgress _sendProgress = new();
    private readonly TempDirectory _temp = new();

    public FileListTransferTests()
    {
        _senderPeer = _mesh.Add(maxIncoming: 4, _sender);
        _receiverPeer = _mesh.Add(maxIncoming: 4, _receiver);
        _sender.AddCallback(_sendProgress);
    }

    public void Dispose()
    {
        _mesh.Dispose();
        _sender.Dispose();
        _receiver.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Files_sent_whole_arrive_with_their_data_and_complete_the_set()
    {
        var (senderAddress, receiverAddress) = Connect();
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        var list = new FileList();
        list.AddFile("a.txt", "a.txt", "alpha"u8, 5, new FileListNodeContext(1, 2, 3, 4));
        list.AddFile("dir/b.bin", "dir/b.bin", [9, 8, 7], 3, default);

        _sender.Send(list, receiverAddress, setId, Priority.High, 0);
        _mesh.Settle();

        Assert.Collection(_handler.Files,
            a =>
            {
                Assert.Equal(setId, a.SetId);
                Assert.Equal(0u, a.FileIndex);
                Assert.Equal("a.txt", a.FileName);
                Assert.Equal("alpha"u8.ToArray(), a.FileData);
                Assert.Equal(5u, a.ByteLengthOfThisFile);
                Assert.Equal(5u, a.BytesDownloadedForThisFile);
                Assert.Equal(2u, a.NumberOfFilesInThisSet);
                Assert.Equal(8u, a.ByteLengthOfThisSet);
                Assert.Equal(5u, a.BytesDownloadedForThisSet);
                // The context travels whole with each file (FileListNodeContext.h:34-41).
                Assert.Equal(new FileListNodeContext(1, 2, 3, 4), a.Context);
                Assert.Equal(senderAddress, a.Sender);
            },
            b =>
            {
                Assert.Equal(1u, b.FileIndex);
                Assert.Equal("dir/b.bin", b.FileName);
                Assert.Equal([9, 8, 7], b.FileData);
                Assert.Equal(8u, b.BytesDownloadedForThisSet);
            });
        // A whole file reports one part of one before it arrives (FileListTransfer.cpp:422-433).
        Assert.Equal(2, _handler.Progress.Count);
        Assert.All(_handler.Progress, p => Assert.Equal((1u, 1u), (p.PartCount, p.PartTotal)));
        Assert.Equal("alpha"u8.ToArray(), _handler.Progress[0].IriDataChunk);
        var complete = Assert.Single(_handler.Completed);
        Assert.Equal(new DownloadComplete(setId, 2, 8, senderAddress, _senderPeer.RakNetGuid), complete);
        Assert.Equal(1, _handler.Dereferenced);
        Assert.False(_receiver.IsHandlerActive(setId));
        Assert.Equal([(receiverAddress, setId)], _sendProgress.PushesComplete);
        Assert.DoesNotContain(_mesh.Seen(_receiverPeer), e => e.Id is (byte)MessageId.FileListTransferHeader or (byte)MessageId.FileListTransferFile);
    }

    [Fact]
    public void An_empty_list_completes_the_set_with_no_files()
    {
        var (senderAddress, receiverAddress) = Connect();
        var setId = _receiver.SetupReceive(_handler, senderAddress);

        _sender.Send(new FileList(), receiverAddress, setId, Priority.High, 0);
        _mesh.Settle();

        Assert.Empty(_handler.Files);
        Assert.Equal(setId, Assert.Single(_handler.Completed).SetId);
        Assert.Equal([(receiverAddress, setId)], _sendProgress.PushesComplete);
    }

    [Fact]
    public void A_handler_that_keeps_itself_stays_active_until_its_update_ends_it()
    {
        var (senderAddress, receiverAddress) = Connect();
        _handler.KeepAfterComplete = true;
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        _sender.Send(new FileList(), receiverAddress, setId, Priority.High, 0);
        _mesh.Settle();
        Assert.True(_receiver.IsHandlerActive(setId));

        _handler.KeepUpdating = false;
        _mesh.Step();

        Assert.False(_receiver.IsHandlerActive(setId));
        Assert.Equal(1, _handler.Dereferenced);
    }

    [Fact]
    public void Set_ids_count_up_and_setup_fails_for_a_system_not_connected()
    {
        var (senderAddress, _) = Connect();

        Assert.Equal(0, _receiver.SetupReceive(_handler, senderAddress));
        Assert.Equal(1, _receiver.SetupReceive(_handler, senderAddress));
        Assert.Equal(Plugins.FileListTransfer.InvalidSetId, _receiver.SetupReceive(_handler, senderAddress.WithPort(1)));

        _receiver.CancelReceive(1);
        Assert.False(_receiver.IsHandlerActive(1));
        Assert.True(_receiver.IsHandlerActive(0));
    }

    [Fact]
    public void Files_from_a_system_other_than_the_allowed_sender_are_dropped()
    {
        var (senderAddress, receiverAddress) = Connect();
        using var otherTransfer = new Plugins.FileListTransfer();
        var other = _mesh.Add(maxIncoming: 0, otherTransfer);
        var otherConnection = _mesh.Connect(other, _receiverPeer);
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        var list = new FileList();
        list.AddFile("x", "x", [1], 1, default);

        otherTransfer.Send(list, otherConnection.Address, setId, Priority.High, 0);
        _mesh.Settle();

        Assert.Empty(_handler.Files);
        Assert.Empty(_handler.Completed);
        Assert.True(_receiver.IsHandlerActive(setId));
        Assert.NotEqual(senderAddress, receiverAddress);
    }

    [Fact]
    public void A_referenced_file_is_sent_a_chunk_at_a_time_with_an_acknowledgement_for_each()
    {
        // Samples/FileListTransfer: a file added as a reference, sent with an IncrementalReadInterface.
        var contents = SuperFastHashTests.Pattern(250_000);
        var path = _temp.Write("big.bin", contents);
        var (senderAddress, receiverAddress) = Connect();
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        var list = new FileList();
        list.AddFile(path, path, [], 250_000, default, isAReference: true, dataLength: 250_000);

        _sender.Send(list, receiverAddress, setId, Priority.High, 0, new FileIncrementalReader(), chunkSize: 100_000);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the set completed");

        // OnReferencePush (FileListTransfer.cpp:816-846): part offset / chunk of length / chunk + 1, from each chunk's own
        // length, so the shorter last chunk reports part 4 of 6.
        Assert.Equal([(0u, 3u, 0u), (1u, 3u, 100_000u), (4u, 6u, 200_000u)], _handler.Progress.Select(p => (p.PartCount, p.PartTotal, p.IriWriteOffset)));
        Assert.Equal(contents[100_000..200_000], _handler.Progress[1].IriDataChunk);
        Assert.Equal([100_000u, 200_000u, 250_000u], _handler.Progress.Select(p => p.File.BytesDownloadedForThisFile));
        var file = Assert.Single(_handler.Files);
        Assert.Equal(contents, file.FileData);
        Assert.Equal(path, file.FileName);
        Assert.Single(_handler.Completed);
        Assert.Equal(
            [(path, 250_000u, 0u, 100_000u, false, setId), (path, 250_000u, 100_000u, 100_000u, false, setId), (path, 250_000u, 200_000u, 50_000u, true, setId)],
            _sendProgress.Pushes);
        Assert.Equal([(receiverAddress, setId)], _sendProgress.PushesComplete);
        Assert.Equal(0u, _sender.GetPendingFilesToAddress(receiverAddress));
    }

    [Fact]
    public void Small_referenced_files_go_together_and_the_last_as_a_push()
    {
        var paths = new[] { _temp.Write("s1", "one"), _temp.Write("s2", "two!"), _temp.Write("s3", "three") };
        var (senderAddress, receiverAddress) = Connect();
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        var list = new FileList();
        foreach (var path in paths)
            list.AddFile(path, path, [], (uint)new FileInfo(path).Length, default, isAReference: true, dataLength: (uint)new FileInfo(path).Length);

        _sender.Send(list, receiverAddress, setId, Priority.High, 0, new FileIncrementalReader(), chunkSize: 1000);
        _mesh.Settle();

        Assert.Equal(["one", "two!", "three"], _handler.Files.Select(f => System.Text.Encoding.ASCII.GetString(f.FileData!)));
        Assert.Equal([0u, 1u, 2u], _handler.Files.Select(f => f.FileIndex));
        // FileListTransfer.cpp:986-1025: small files go together only while two remain, so the last two are pushed, the
        // second after the first is acknowledged.
        Assert.Equal([(paths[1], 4u, 0u, 4u, true, setId), (paths[2], 5u, 0u, 5u, true, setId)], _sendProgress.Pushes);
        Assert.Single(_handler.Completed);
    }

    [Fact]
    public void A_handler_may_keep_each_chunk_itself_instead_of_the_whole_file()
    {
        var contents = SuperFastHashTests.Pattern(30_000);
        var path = _temp.Write("big.bin", contents);
        var (senderAddress, receiverAddress) = Connect();
        _handler.AllocateIriDataChunkAutomatically = false;
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        var list = new FileList();
        list.AddFile(path, path, [], 30_000, default, isAReference: true, dataLength: 30_000);

        _sender.Send(list, receiverAddress, setId, Priority.High, 0, new FileIncrementalReader(), chunkSize: 10_000);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the set completed");

        Assert.Equal(contents, _handler.Progress.SelectMany(p => p.IriDataChunk!).ToArray());
        Assert.Null(Assert.Single(_handler.Files).FileData);
    }

    [Fact]
    public void Incremental_reads_may_run_on_worker_threads()
    {
        var contents = SuperFastHashTests.Pattern(50_000);
        var path = _temp.Write("big.bin", contents);
        var (senderAddress, receiverAddress) = Connect();
        _sender.StartIncrementalReadThreads(1);
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        var list = new FileList();
        list.AddFile(path, path, [], 50_000, default, isAReference: true, dataLength: 50_000);

        _sender.Send(list, receiverAddress, setId, Priority.High, 0, new FileIncrementalReader(), chunkSize: 20_000);
        RunUntilOnRealThreads(() => _handler.Files.Count == 1);

        Assert.Equal(contents, _handler.Files[0].FileData);
    }

    [Fact]
    public void A_large_file_reports_split_message_progress_before_it_arrives()
    {
        var contents = SuperFastHashTests.Pattern(40_000);
        _receiverPeer.SplitMessageProgressInterval = 4;
        var (senderAddress, receiverAddress) = Connect();
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        var list = new FileList();
        list.AddFile("large.bin", "large.bin", contents, 40_000, new FileListNodeContext(5, 6, 7, 8));

        _sender.Send(list, receiverAddress, setId, Priority.High, 0);
        _mesh.RunUntil(() => _handler.Completed.Count == 1, "the set completed");

        var partial = _handler.Progress.Where(p => p.IriDataChunk is null).ToList();
        Assert.NotEmpty(partial);
        Assert.All(partial, p =>
        {
            Assert.Equal("large.bin", p.File.FileName);
            Assert.Equal(new FileListNodeContext(5, 6, 7, 8), p.File.Context);
            Assert.True(p.PartCount < p.PartTotal);
            Assert.Equal(contents[..p.FirstDataChunk.Length], p.FirstDataChunk);
        });
        Assert.Equal(contents, Assert.Single(_handler.Files).FileData);
        Assert.DoesNotContain(_mesh.Seen(_receiverPeer), e => e.Kind == PeerEventKind.DownloadProgress);
    }

    [Fact]
    public void A_disconnect_aborts_the_send_and_drops_the_receive()
    {
        var path = _temp.Write("big.bin", SuperFastHashTests.Pattern(100_000));
        var (senderAddress, receiverAddress) = Connect();
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        var list = new FileList();
        list.AddFile(path, path, [], 100_000, default, isAReference: true, dataLength: 100_000);
        list.AddFile(path + "2", path, [], 100_000, default, isAReference: true, dataLength: 100_000);
        _sender.Send(list, receiverAddress, setId, Priority.High, 0, new FileIncrementalReader(), chunkSize: 10_000);
        Assert.Equal(2u, _sender.GetPendingFilesToAddress(receiverAddress));

        _receiverPeer.Close(_senderPeer.RakNetGuid, notify: true);
        _mesh.Settle();

        Assert.Equal([receiverAddress], _sendProgress.Aborted);
        Assert.Equal(0u, _sender.GetPendingFilesToAddress(receiverAddress));
        Assert.False(_receiver.IsHandlerActive(setId));
        Assert.Equal(1, _handler.Dereferenced);
    }

    [Fact]
    public void A_push_whose_chunk_does_not_fit_the_file_is_dropped()
    {
        var (senderAddress, _) = Connect();
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        Header(setId, fileCount: 1, totalLength: 10);

        // A chunk of 8 bytes at offset 5 of a 10-byte file: RakNet would copy past its buffer (FileListTransfer.cpp:787).
        SendRaw(ReferencePush(setId, "evil", fileLength: 10, offset: 5, chunk: new byte[8], done: true));
        _mesh.Settle();

        Assert.Empty(_handler.Files);
        Assert.Empty(_handler.Progress);
        // The acknowledgement is still sent, before the set is looked up (FileListTransfer.cpp:711-716).
        Assert.True(_receiver.IsHandlerActive(setId));
    }

    [Fact]
    public void A_whole_file_shorter_than_its_stated_length_is_dropped()
    {
        var (senderAddress, _) = Connect();
        var setId = _receiver.SetupReceive(_handler, senderAddress);
        Header(setId, fileCount: 1, totalLength: 100);

        var message = new byte[256];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)MessageId.FileListTransferFile);
        default(FileListNodeContext).Write(ref writer);
        writer.WriteUInt16(setId);
        writer.WriteCompressedString("short".AsSpan(), 512);
        writer.WriteCompressed(0u);
        writer.WriteCompressed(100u);
        writer.AlignToByte();
        writer.WriteBytes([1, 2, 3]);
        SendRaw(writer.Written.ToArray());
        _mesh.Settle();

        Assert.Empty(_handler.Files);
    }

    private (SystemAddress SenderAddress, SystemAddress ReceiverAddress) Connect()
    {
        _mesh.Connect(_receiverPeer, _senderPeer);
        Assert.True(_receiverPeer.TryGetConnection(_senderPeer.RakNetGuid, out var toSender));
        Assert.True(_senderPeer.TryGetConnection(_receiverPeer.RakNetGuid, out var toReceiver));
        return (toSender.Address, toReceiver.Address);
    }

    private void Header(ushort setId, uint fileCount, uint totalLength)
    {
        var message = new byte[16];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)MessageId.FileListTransferHeader);
        writer.WriteUInt16(setId);
        writer.WriteBool(true);
        writer.WriteCompressed(fileCount);
        writer.WriteCompressed(totalLength);
        _senderPeer.Send(writer, _receiverPeer.RakNetGuid, Reliability.ReliableOrdered, Priority.High, 0);
    }

    private void SendRaw(byte[] message) => _senderPeer.Send(message, _receiverPeer.RakNetGuid, Reliability.ReliableOrdered, Priority.High, 0);

    private static byte[] ReferencePush(ushort setId, string name, uint fileLength, uint offset, byte[] chunk, bool done)
    {
        var message = new byte[256 + chunk.Length];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)MessageId.FileListReferencePush);
        default(FileListNodeContext).Write(ref writer);
        writer.WriteUInt16(setId);
        writer.WriteCompressedString(name.AsSpan(), 512);
        writer.WriteCompressed(0u);
        writer.WriteCompressed(fileLength);
        writer.WriteCompressed(offset);
        writer.WriteCompressed((uint)chunk.Length);
        writer.WriteBool(done);
        writer.AlignToByte();
        writer.WriteBytes(chunk);
        return writer.Written.ToArray();
    }

    // Worker threads send on their own, so the mesh keeps stepping while they run.
    private void RunUntilOnRealThreads(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _mesh.Step();
            Thread.Sleep(1);
        }
        Assert.True(condition());
    }
}
