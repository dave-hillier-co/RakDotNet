using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's PacketizedTCP: a <see cref="TcpInterface"/> that sends each message behind its length and returns whole
/// messages, so TCP can carry RakNet messages such as FileListTransfer's.
/// </summary>
/// <remarks>
/// <para>
/// On the wire a message is its length as a 32-bit integer, big-endian (PTCPHeader, swapped on little-endian hosts by
/// BitStream::DoEndianSwap, PacketizedTCP.cpp:44-62), then its bytes.
/// </para>
/// <para>
/// RakNet's bookkeeping is kept as it is. A connection's incoming stream is buffered only once a polling call has
/// noticed the connection, and each polling call notices one new, completed, failed and lost connection
/// (PacketizedTCP.cpp:101-130), so data from a connection not yet noticed is dropped, packets pushed back included
/// (PacketizedTCP.cpp:147-158). A receive that leaves a message incomplete but takes the buffered byte count past a
/// multiple of 65536 queues ID_DOWNLOAD_PROGRESS (PacketizedTCP.cpp:211-249). The notification queues of this class
/// survive <see cref="TcpInterface.Stop"/>, as RakNet's do.
/// </para>
/// </remarks>
public class PacketizedTcp : TcpInterface
{
    private const int HeaderLength = sizeof(uint);
    private const int ProgressChunk = 65536;

    private readonly Lock _gate = new();
    private readonly Queue<TcpPacket> _waitingPackets = new();
    private readonly Dictionary<SystemAddress, ByteQueue> _connections = [];
    private readonly Queue<SystemAddress> _newIncomingConnections = new();
    private readonly Queue<SystemAddress> _lostConnections = new();
    private readonly Queue<SystemAddress> _failedConnectionAttempts = new();
    private readonly Queue<SystemAddress> _completedConnectionAttempts = new();

    public PacketizedTcp(TimeProvider? timeProvider = null)
        : base(timeProvider)
    {
    }

    /// <summary>Send: the length then the bytes (PacketizedTCP.cpp:44-62). An empty message is a zero length.</summary>
    public override void Send(ReadOnlySpan<byte> data, SystemAddress address, bool broadcast)
    {
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        base.SendList([header, data.ToArray()], address, broadcast);
    }

    /// <summary>SendList: one message of the parts joined; false as <see cref="TcpInterface.SendList"/>, or when the parts hold no bytes (PacketizedTCP.cpp:63-100).</summary>
    public override bool SendList(ReadOnlySpan<ReadOnlyMemory<byte>> parts, SystemAddress address, bool broadcast)
    {
        if (!WasStarted)
            return false;
        if (address == SystemAddress.Unassigned && !broadcast)
            return false;
        uint total = 0;
        foreach (var part in parts)
            total += (uint)part.Length;
        if (total == 0)
            return false;

        var framed = new ReadOnlyMemory<byte>[parts.Length + 1];
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, total);
        framed[0] = header;
        parts.CopyTo(framed.AsSpan(1));
        return base.SendList(framed, address, broadcast);
    }

    /// <summary>
    /// Receive: notices connections, runs the plugins' updates, then returns the next whole message, or a progress
    /// report, that no plugin consumed (PacketizedTCP.cpp:131-263).
    /// </summary>
    public override TcpPacket? Receive()
    {
        PushNotificationsToQueues();
        foreach (var plugin in AttachedPlugins)
            plugin.OnUpdate();

        lock (_gate)
        {
            if (ReturnOutgoingPacket() is { } waiting)
                return waiting;

            while (ReceiveInt() is { } incoming)
            {
                if (!_connections.TryGetValue(incoming.Address, out var queue))
                    continue;
                if (incoming.FromNetwork)
                    Buffer(queue, incoming);
                else
                    _waitingPackets.Enqueue(incoming);
            }
            return ReturnOutgoingPacket();
        }
    }

    /// <summary>CloseConnection: forgets the connection's buffered stream, then closes it (PacketizedTCP.cpp:291-295).</summary>
    public override void CloseConnection(SystemAddress address)
    {
        lock (_gate)
            RemoveFromConnectionList(address);
        base.CloseConnection(address);
    }

    public override SystemAddress HasCompletedConnectionAttempt()
    {
        PushNotificationsToQueues();
        lock (_gate)
            return _completedConnectionAttempts.TryDequeue(out var address) ? address : SystemAddress.Unassigned;
    }

    public override SystemAddress HasFailedConnectionAttempt()
    {
        PushNotificationsToQueues();
        lock (_gate)
            return _failedConnectionAttempts.TryDequeue(out var address) ? address : SystemAddress.Unassigned;
    }

    public override SystemAddress HasNewIncomingConnection()
    {
        PushNotificationsToQueues();
        lock (_gate)
            return _newIncomingConnections.TryDequeue(out var address) ? address : SystemAddress.Unassigned;
    }

    public override SystemAddress HasLostConnection()
    {
        PushNotificationsToQueues();
        lock (_gate)
            return _lostConnections.TryDequeue(out var address) ? address : SystemAddress.Unassigned;
    }

    /// <summary>PacketizedTCP::Stop: the waiting messages and buffered streams go too (PacketizedTCP.cpp:35-42).</summary>
    protected override void OnStopped()
    {
        lock (_gate)
        {
            _waitingPackets.Clear();
            _connections.Clear();
        }
    }

    // PushNotificationsToQueues (PacketizedTCP.cpp:101-130): one of each kind per call, in this order.
    private void PushNotificationsToQueues()
    {
        var address = base.HasNewIncomingConnection();
        lock (_gate)
        {
            if (address != SystemAddress.Unassigned)
            {
                _newIncomingConnections.Enqueue(address);
                AddToConnectionList(address);
            }
        }

        address = base.HasFailedConnectionAttempt();
        lock (_gate)
        {
            if (address != SystemAddress.Unassigned)
                _failedConnectionAttempts.Enqueue(address);
        }

        address = base.HasLostConnection();
        lock (_gate)
        {
            if (address != SystemAddress.Unassigned)
            {
                _lostConnections.Enqueue(address);
                RemoveFromConnectionList(address);
            }
        }

        address = base.HasCompletedConnectionAttempt();
        lock (_gate)
        {
            if (address != SystemAddress.Unassigned)
            {
                _completedConnectionAttempts.Enqueue(address);
                AddToConnectionList(address);
            }
        }
    }

    // PacketizedTCP.cpp:161-255: buffers the received bytes and queues every message they complete, or a progress report.
    private void Buffer(ByteQueue queue, TcpPacket incoming)
    {
        queue.WriteBytes(incoming.Data.Span);
        if (!TryPeekLength(queue, out var length))
            return;

        if (queue.BytesWritten >= length + HeaderLength)
        {
            do
            {
                queue.IncrementReadOffset(HeaderLength);
                var message = new byte[length];
                queue.ReadBytes(message, peek: false);
                _waitingPackets.Enqueue(new TcpPacket(incoming.Address, message));
            }
            while (TryPeekLength(queue, out length) && queue.BytesWritten >= length + HeaderLength);
            return;
        }

        var oldWritten = queue.BytesWritten - incoming.Data.Length;
        var newWritten = queue.BytesWritten;
        if (newWritten / ProgressChunk == oldWritten / ProgressChunk)
            return;

        // The part index, part count and part length are copied in host order, which is little-endian on every
        // platform .NET runs on; bitSize is that of the received packet (PacketizedTCP.cpp:219-246).
        var progress = new byte[1 + 3 * sizeof(uint) + ProgressChunk];
        progress[0] = (byte)MessageId.DownloadProgress;
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(1), (uint)(newWritten / ProgressChunk));
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(5), (uint)(length / ProgressChunk));
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(9), ProgressChunk);
        queue.IncrementReadOffset(HeaderLength);
        queue.ReadBytes(progress.AsSpan(13), peek: true);
        queue.DecrementReadOffset(HeaderLength);
        _waitingPackets.Enqueue(new TcpPacket(incoming.Address, progress) { BitLength = incoming.Data.Length * 8 });
    }

    // RakNet peeks the header even when fewer than four bytes are buffered, leaving the rest of the length undefined;
    // here a partial header is an incomplete message.
    private static bool TryPeekLength(ByteQueue queue, out long length)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        if (queue.BytesWritten < HeaderLength)
        {
            length = 0;
            return false;
        }
        queue.ReadBytes(header, peek: true);
        length = BinaryPrimitives.ReadUInt32BigEndian(header);
        return true;
    }

    // ReturnOutgoingPacket (PacketizedTCP.cpp:264-290): consumed messages are skipped.
    private TcpPacket? ReturnOutgoingPacket()
    {
        while (_waitingPackets.TryDequeue(out var packet))
        {
            if (OfferToPlugins(packet) is { } offered)
                return offered;
        }
        return null;
    }

    private void RemoveFromConnectionList(SystemAddress address)
    {
        if (address != SystemAddress.Unassigned)
            _connections.Remove(address);
    }

    // DataStructures::Map::SetNew replaces a buffered stream already held for the address (PacketizedTCP.cpp:310-315).
    private void AddToConnectionList(SystemAddress address)
    {
        if (address != SystemAddress.Unassigned)
            _connections[address] = new ByteQueue();
    }
}
