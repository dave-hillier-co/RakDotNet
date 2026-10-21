using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// NatTypeDetectionClient: asks a connected <see cref="NatTypeDetectionServer"/> which kind of NAT this system is
/// behind. The answer is ID_NAT_TYPE_DETECTION_RESULT, generated locally, with the
/// <see cref="NatTypeDetectionResult"/> in its second byte.
/// </summary>
/// <remarks>
/// <para>
/// Ported from NatTypeDetectionClient.cpp. <see cref="DetectNatType"/> opens a second socket, C2, on the address of the
/// peer's first socket and sends ID_NAT_TYPE_DETECTION_REQUEST, true as one bit and C2's port (MEDIUM_PRIORITY,
/// RELIABLE, channel 0). The server then tries the tests described on <see cref="NatTypeDetectionServer"/>; the client
/// completes on the first that reaches it: a one-byte NAT_TYPE_NONE on C2, an out-of-band ID_NAT_TYPE_DETECT and its
/// result on the peer's socket, or the server's own ID_NAT_TYPE_DETECTION_RESULT. Asked to send to the server's third
/// address, it sends NAT_TYPE_PORT_RESTRICTED and its GUID there from the peer's socket, unformatted.
/// </para>
/// <para>
/// On completion the result is pushed at the head of the stream from the server's address and GUID: three bytes, as
/// AllocatePacketUnified(sizeof(MessageID)+2) sizes it, whose last byte RakNet leaves uninitialised and this port sets
/// to 0. Unless the server decided it (port restricted or symmetric), the client then tells the server it is done with
/// ID_NAT_TYPE_DETECTION_REQUEST and false (HIGH_PRIORITY, RELIABLE), and closes C2. Losing the connection to the
/// server, shutting down or detaching ends a detection without a result.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class NatTypeDetectionClient : IRakPlugin, IDisposable
{
    private readonly Lock _gate = new();
    private IPluginHost? _host;
    private IDatagramTransport? _c2;
    private SystemAddress _serverAddress;

    /// <summary>IsInProgress: a detection has started and not completed.</summary>
    public bool IsInProgress
    {
        get
        {
            lock (_gate)
                return _serverAddress.IsAssigned;
        }
    }

    /// <summary>
    /// DetectNATType: starts detection with the server at <paramref name="serverAddress"/>, which must already be
    /// connected and run <see cref="NatTypeDetectionServer"/>. False, doing nothing, while a detection is in progress,
    /// and when the second socket cannot be bound, where RakNet would dereference a null socket.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public bool DetectNatType(SystemAddress serverAddress)
    {
        lock (_gate)
        {
            if (_serverAddress.IsAssigned)
                return false;
            var peer = Peer;
            if (_c2 is null)
            {
                _c2 = BoundSocket.TryBind(peer, BoundSocket.AddressOf(peer.Sockets[0].LocalAddress));
                if (_c2 is null)
                    return false;
            }
            _serverAddress = serverAddress;

            Span<byte> request = stackalloc byte[4];
            var writer = new BitStreamWriter(request);
            writer.WriteByte((byte)MessageId.NatTypeDetectionRequest);
            writer.WriteBool(true); // IsRequest
            writer.WriteUInt16(_c2.LocalAddress.Port);
            Send(peer, writer, _serverAddress, Priority.Medium);
            return true;
        }
    }

    /// <summary>Closes the second socket and ends any detection, as the destructor does.</summary>
    public void Dispose()
    {
        lock (_gate)
            Shutdown();
    }

    private RakPeer Peer => _host?.Peer ?? throw new InvalidOperationException("The NatTypeDetectionClient is not attached to a RakPeer.");

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    /// <summary>OnDetach: Shutdown.</summary>
    void IRakPlugin.OnDetached()
    {
        lock (_gate)
        {
            Shutdown();
            _host = null;
        }
    }

    /// <summary>OnRakPeerShutdown: Shutdown.</summary>
    void IRakPlugin.OnShutdown()
    {
        lock (_gate)
            Shutdown();
    }

    /// <summary>Update (NatTypeDetectionClient.cpp:99-125): a one-byte NAT_TYPE_NONE on C2 means there is no NAT.</summary>
    void IRakPlugin.OnUpdate()
    {
        lock (_gate)
        {
            if (!_serverAddress.IsAssigned || _c2 is null)
                return;
            Span<byte> buffer = stackalloc byte[BoundSocket.MaximumMtuSize];
            while (_c2 is { } c2 && c2.TryReceive(buffer, out var length, out _))
            {
                if (length == 1 && buffer[0] == (byte)NatTypeDetectionResult.None)
                    OnCompletion(NatTypeDetectionResult.None);
            }
        }
    }

    /// <summary>OnClosedConnection (NatTypeDetectionClient.cpp:155-162).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
        {
            if (_serverAddress.IsAssigned && connection.Address == _serverAddress)
                Shutdown();
        }
    }

    /// <summary>OnReceive (NatTypeDetectionClient.cpp:126-154).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        lock (_gate)
        {
            if (!_serverAddress.IsAssigned)
                return PluginResult.Continue;
            switch ((MessageId)data[0])
            {
                case MessageId.OutOfBandInternal:
                    if (data.Length >= 3 && data[1] == (byte)OutOfBandId.NatTypeDetect)
                    {
                        OnCompletion((NatTypeDetectionResult)data[2]);
                        return PluginResult.Consume;
                    }
                    break;
                case MessageId.NatTypeDetectionResult:
                    if (!e.IsLocal && data.Length >= 2)
                    {
                        OnCompletion((NatTypeDetectionResult)data[1]);
                        return PluginResult.Consume;
                    }
                    break;
                case MessageId.NatTypeDetectionRequest:
                    OnTestPortRestricted(e.GetReader());
                    return PluginResult.Consume;
            }
        }
        return PluginResult.Continue;
    }

    // OnCompletion (NatTypeDetectionClient.cpp:72-94).
    private void OnCompletion(NatTypeDetectionResult result)
    {
        var peer = Peer;
        ReadOnlySpan<byte> packet = [(byte)MessageId.NatTypeDetectionResult, (byte)result, 0];
        peer.Inject(packet, _serverAddress, peer.GetGuidFromAddress(_serverAddress), atHead: true, generatedLocally: true);

        // Symmetric and port restricted are determined by the server, so it is not told the client is done.
        if (result != NatTypeDetectionResult.PortRestricted && result != NatTypeDetectionResult.Symmetric)
        {
            Span<byte> done = stackalloc byte[2];
            var writer = new BitStreamWriter(done);
            writer.WriteByte((byte)MessageId.NatTypeDetectionRequest);
            writer.WriteBool(false); // Done
            Send(peer, writer, _serverAddress, Priority.High);
        }
        Shutdown();
    }

    // OnTestPortRestricted (NatTypeDetectionClient.cpp:172-198): the server's third address as a RakString and its
    // port. NAT_TYPE_PORT_RESTRICTED and this system's GUID go there from the peer's socket, unformatted.
    private void OnTestPortRestricted(BitStreamReader reader)
    {
        reader.IgnoreBytes(1);
        if (!reader.TryReadRakString(out var addressText) || !reader.TryReadUInt16(out var port)
            || !SystemAddress.TryFromStringExplicitPort(addressText, port, out var s3p4))
            return;
        var peer = Peer;
        Span<byte> message = stackalloc byte[1 + sizeof(ulong)];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)NatTypeDetectionResult.PortRestricted);
        writer.WriteRakNetGuid(peer.RakNetGuid);
        peer.Sockets[0].SendTo(writer.Written, s3p4);
    }

    // Shutdown (NatTypeDetectionClient.cpp:199-218).
    private void Shutdown()
    {
        _serverAddress = SystemAddress.Unassigned;
        _c2?.Dispose();
        _c2 = null;
    }

    // RakPeer::Send returns without sending to UNASSIGNED_SYSTEM_ADDRESS (RakPeer.cpp:1311-1315).
    private static void Send(RakPeer peer, BitStreamWriter writer, SystemAddress target, Priority priority)
    {
        if (target.IsAssigned)
            peer.Send(writer, target, Reliability.Reliable, priority, 0);
    }
}
