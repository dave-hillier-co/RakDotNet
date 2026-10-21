using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// NatTypeDetectionServer: tells a connected <see cref="NatTypeDetectionClient"/> which kind of NAT it is behind, by
/// sending to it from four sockets of its own on up to four addresses.
/// </summary>
/// <remarks>
/// <para>
/// Ported from NatTypeDetectionServer.cpp. The peer's socket is S1P1; <see cref="Startup"/> binds S1P2 on S1P1's address
/// and S2P3, S3P4 and S4P5 on three other addresses. The client connects to S1P1 from its socket C1 and names a second
/// socket, C2. Each request is tested in turn, every state twice, one state each <c>3 * lastPing + 50</c> ms:
/// </para>
/// <list type="number">
/// <item>S4P5 sends one byte, NAT_TYPE_NONE, to C2's port at C1's address. If it arrives there is no NAT.</item>
/// <item>S2P3 (another address and port) sends ID_NAT_TYPE_DETECT and NAT_TYPE_FULL_CONE out of band to C1: full cone.</item>
/// <item>S1P2 (the same address, another port) sends NAT_TYPE_ADDRESS_RESTRICTED the same way: address restricted.</item>
/// <item>
/// S1P1 asks the client, with ID_NAT_TYPE_DETECTION_REQUEST, S3P4's address string and port, to send to S3P4. If S3P4
/// sees C1 at the address S1P1 does the NAT is port restricted, otherwise symmetric; the result goes to the client as
/// ID_NAT_TYPE_DETECTION_RESULT.
/// </item>
/// <item>With no answer after the fourth test, the result is NAT_TYPE_SYMMETRIC.</item>
/// </list>
/// <para>
/// A client that completes another way sends ID_NAT_TYPE_DETECTION_REQUEST and false, which ends its test, as does its
/// disconnection. Attempts are a list from which entries are removed by moving the last one into their place, as
/// RemoveAtIndexFast does, and times are RakNet::TimeMS, 32 bits. The sockets are closed by <see cref="Shutdown"/>, by
/// <see cref="Dispose"/>, and when the peer shuts down: a RakPeer here never starts again (docs/api-design.md decision
/// 11.10), where RakNet keeps them until the plugin is destroyed.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class NatTypeDetectionServer : IRakPlugin, IDisposable
{
    // NATDetectionState (NatTypeDetectionServer.h:84-96).
    private const int StateNone = 0;
    private const int StateTestingNone1 = 1;
    private const int StateTestingNone2 = 2;
    private const int StateTestingFullCone1 = 3;
    private const int StateTestingFullCone2 = 4;
    private const int StateTestingAddressRestricted1 = 5;
    private const int StateTestingAddressRestricted2 = 6;
    private const int StateTestingPortRestricted1 = 7;
    private const int StateTestingPortRestricted2 = 8;

    private readonly Lock _gate = new();
    private readonly List<DetectionAttempt> _attempts = [];
    private IPluginHost? _host;
    private IDatagramTransport? _s1p2;
    private IDatagramTransport? _s2p3;
    private IDatagramTransport? _s3p4;
    private IDatagramTransport? _s4p5;
    private string _s3p4Address = "";

    /// <summary>
    /// The addresses S1P2, S2P3, S3P4 and S4P5 are bound to, in that order; empty when not started. RakNet keeps them
    /// protected; they are exposed here so a test network can tell which addresses belong together.
    /// </summary>
    public IReadOnlyList<SystemAddress> SocketAddresses
    {
        get
        {
            lock (_gate)
                return _s1p2 is null || _s2p3 is null || _s3p4 is null || _s4p5 is null
                    ? []
                    : [_s1p2.LocalAddress, _s2p3.LocalAddress, _s3p4.LocalAddress, _s4p5.LocalAddress];
        }
    }

    /// <summary>
    /// Startup: binds S1P2 on the address of the peer's first socket, and S2P3, S3P4 and S4P5 on
    /// <paramref name="nonRakNetIp2"/>, <paramref name="nonRakNetIp3"/> and <paramref name="nonRakNetIp4"/>, three
    /// addresses the peer does not use, each on an ephemeral port. False, binding nothing, when an address is not one or
    /// a socket cannot be bound, where RakNet would keep a null socket and crash on its first use.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public bool Startup(string nonRakNetIp2, string nonRakNetIp3, string nonRakNetIp4)
    {
        ArgumentNullException.ThrowIfNull(nonRakNetIp2);
        ArgumentNullException.ThrowIfNull(nonRakNetIp3);
        ArgumentNullException.ThrowIfNull(nonRakNetIp4);
        lock (_gate)
        {
            var peer = Peer;
            Shutdown();
            if (!System.Net.IPAddress.TryParse(nonRakNetIp2, out var ip2) || !System.Net.IPAddress.TryParse(nonRakNetIp3, out var ip3)
                || !System.Net.IPAddress.TryParse(nonRakNetIp4, out var ip4))
                return false;
            _s1p2 = BoundSocket.TryBind(peer, BoundSocket.AddressOf(peer.Sockets[0].LocalAddress));
            _s2p3 = BoundSocket.TryBind(peer, ip2);
            _s3p4 = BoundSocket.TryBind(peer, ip3);
            _s4p5 = BoundSocket.TryBind(peer, ip4);
            if (_s1p2 is null || _s2p3 is null || _s3p4 is null || _s4p5 is null)
            {
                Shutdown();
                return false;
            }
            _s3p4Address = nonRakNetIp3;
            return true;
        }
    }

    /// <summary>Shutdown: closes the sockets Startup bound.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _s1p2?.Dispose();
            _s2p3?.Dispose();
            _s3p4?.Dispose();
            _s4p5?.Dispose();
            _s1p2 = _s2p3 = _s3p4 = _s4p5 = null;
        }
    }

    /// <inheritdoc cref="Shutdown"/>
    public void Dispose() => Shutdown();

    private RakPeer Peer => _host?.Peer ?? throw new InvalidOperationException("The NatTypeDetectionServer is not attached to a RakPeer.");

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    void IRakPlugin.OnShutdown() => Shutdown();

    /// <summary>Update (NatTypeDetectionServer.cpp:135-338).</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host?.Peer is not { } peer)
            return;
        lock (_gate)
        {
            var time = (uint)peer.GetTime();
            ReceivePortRestrictedReplies(peer);

            for (var i = 0; i < _attempts.Count; i++)
            {
                var attempt = _attempts[i];
                if (!(time > attempt.NextStateTime))
                    continue;
                attempt.DetectionState++;
                attempt.NextStateTime = time + attempt.TimeBetweenAttempts;
                switch (attempt.DetectionState)
                {
                    case StateTestingNone1:
                    case StateTestingNone2:
                        // S4P5 sends to C2. If it arrives there is no NAT.
                        _s4p5?.TrySend([(byte)NatTypeDetectionResult.None], attempt.SystemAddress.WithPort(attempt.C2Port));
                        break;
                    case StateTestingFullCone1:
                    case StateTestingFullCone2:
                        // S2P3 sends to C1: another address and port, to the port the client already used.
                        SendDetect(peer, _s2p3, NatTypeDetectionResult.FullCone, attempt.SystemAddress);
                        break;
                    case StateTestingAddressRestricted1:
                    case StateTestingAddressRestricted2:
                        // S1P2 sends to C1: the same address, another port.
                        SendDetect(peer, _s1p2, NatTypeDetectionResult.AddressRestricted, attempt.SystemAddress);
                        break;
                    case StateTestingPortRestricted1:
                    case StateTestingPortRestricted2:
                        RequestPortRestrictedTest(peer, attempt.SystemAddress);
                        break;
                    default:
                        SendResult(peer, NatTypeDetectionResult.Symmetric, attempt.SystemAddress);
                        RemoveAtIndexFast(i);
                        i--;
                        break;
                }
            }
        }
    }

    /// <summary>OnReceive (NatTypeDetectionServer.cpp:339-348).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || data[0] != (byte)MessageId.NatTypeDetectionRequest || _host?.Peer is not { } peer)
            return PluginResult.Continue;
        lock (_gate)
            OnDetectionRequest(peer, e.GetReader(), e.Address, e.RakNetGuid);
        return PluginResult.Consume;
    }

    /// <summary>OnClosedConnection (NatTypeDetectionServer.cpp:349-359).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
        {
            var i = IndexOf(connection.Address);
            if (i >= 0)
                RemoveAtIndexFast(i);
        }
    }

    // OnDetectionRequest (NatTypeDetectionServer.cpp:360-389).
    private void OnDetectionRequest(RakPeer peer, BitStreamReader reader, SystemAddress sender, RakNetGuid guid)
    {
        var i = IndexOf(sender);
        reader.IgnoreBytes(1);
        reader.TryReadBool(out var isRequest);
        if (isRequest)
        {
            if (i >= 0)
                return; // Already in progress
            reader.TryReadUInt16(out var c2Port);
            _attempts.Add(new DetectionAttempt
            {
                SystemAddress = sender,
                RakNetGuid = guid,
                C2Port = c2Port,
                DetectionState = StateNone,
                NextStateTime = 0,
                TimeBetweenAttempts = unchecked((uint)(LastPing(peer, sender) * 3 + 50)),
            });
        }
        else if (i >= 0)
        {
            // They are done.
            RemoveAtIndexFast(i);
        }
    }

    // The datagrams on S3P4 (NatTypeDetectionServer.cpp:144-199): NAT_TYPE_PORT_RESTRICTED and the client's GUID. The
    // other sockets only send; what reaches them is discarded, as RakNet's Update does.
    private void ReceivePortRestrictedReplies(RakPeer peer)
    {
        Span<byte> buffer = stackalloc byte[BoundSocket.MaximumMtuSize];
        Drain(_s1p2, buffer);
        Drain(_s2p3, buffer);
        Drain(_s4p5, buffer);
        while (_s3p4 is { } s3p4 && s3p4.TryReceive(buffer, out var length, out var sender))
        {
            if (length < 1 || buffer[0] != (byte)NatTypeDetectionResult.PortRestricted)
                continue;
            var reader = new BitStreamReader(buffer[..length]);
            reader.IgnoreBytes(1);
            if (!reader.TryReadRakNetGuid(out var senderGuid))
                continue;
            var i = IndexOf(senderGuid);
            if (i < 0)
                continue;
            // If the address differs from the connection's, the NAT is symmetric.
            var attempt = _attempts[i];
            SendResult(peer, sender != attempt.SystemAddress ? NatTypeDetectionResult.Symmetric : NatTypeDetectionResult.PortRestricted, attempt.SystemAddress);
            RemoveAtIndexFast(i);
        }
    }

    private static void Drain(IDatagramTransport? socket, Span<byte> buffer)
    {
        while (socket is not null && socket.TryReceive(buffer, out _, out _))
        {
        }
    }

    // WriteOutOfBandHeader, ID_NAT_TYPE_DETECT and the type, from one of the extra sockets.
    private static void SendDetect(RakPeer peer, IDatagramTransport? socket, NatTypeDetectionResult type, SystemAddress client)
    {
        if (socket is null)
            return;
        Span<byte> datagram = stackalloc byte[Protocol.Wire.Messages.OutOfBandHeader.Length + 2];
        var writer = new BitStreamWriter(datagram);
        peer.LowLevel.WriteOutOfBandHeader(ref writer);
        writer.WriteByte((byte)OutOfBandId.NatTypeDetect);
        writer.WriteByte((byte)type);
        socket.TrySend(writer.Written, client);
    }

    // ID_NAT_TYPE_DETECTION_REQUEST, S3P4's address string and port (HIGH_PRIORITY, RELIABLE, channel 0).
    private void RequestPortRestrictedTest(RakPeer peer, SystemAddress client)
    {
        if (_s3p4 is null || !client.IsAssigned)
            return;
        var addressBytes = System.Text.Encoding.UTF8.GetByteCount(_s3p4Address);
        var message = new byte[1 + sizeof(ushort) + addressBytes + sizeof(ushort)];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)MessageId.NatTypeDetectionRequest);
        writer.WriteRakString(_s3p4Address.AsSpan());
        writer.WriteUInt16(_s3p4.LocalAddress.Port);
        peer.Send(writer, client, Reliability.Reliable, Priority.High, 0);
    }

    private static void SendResult(RakPeer peer, NatTypeDetectionResult result, SystemAddress client)
    {
        ReadOnlySpan<byte> message = [(byte)MessageId.NatTypeDetectionResult, (byte)result];
        if (client.IsAssigned)
            peer.Send(message, client, Reliability.Reliable, Priority.High, 0);
    }

    // RakPeer::GetLastPing (RakPeer.cpp:2177-2190): -1 with no connection, 65535 before the first pong.
    private static int LastPing(RakPeer peer, SystemAddress address)
    {
        if (!peer.TryGetConnection(address, out var connection))
            return -1;
        return connection.LastPing is { } ping ? (int)ping.TotalMilliseconds : ushort.MaxValue;
    }

    private int IndexOf(SystemAddress address) => _attempts.FindIndex(a => a.SystemAddress == address);

    private int IndexOf(RakNetGuid guid) => _attempts.FindIndex(a => a.RakNetGuid == guid);

    // DataStructures::List::RemoveAtIndexFast: the last entry takes the removed one's place.
    private void RemoveAtIndexFast(int index)
    {
        _attempts[index] = _attempts[^1];
        _attempts.RemoveAt(_attempts.Count - 1);
    }

    /// <summary>NATDetectionAttempt (NatTypeDetectionServer.h:98-106).</summary>
    private sealed class DetectionAttempt
    {
        public SystemAddress SystemAddress { get; init; }

        public int DetectionState { get; set; }

        public uint NextStateTime { get; set; }

        public uint TimeBetweenAttempts { get; init; }

        public ushort C2Port { get; init; }

        public RakNetGuid RakNetGuid { get; init; }
    }
}
