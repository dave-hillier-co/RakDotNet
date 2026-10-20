using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Peer;

/// <summary>
/// The sans-IO core of a RakNet peer, ported from RakPeer.cpp: it routes <c>(endpoint, bytes)</c> in and out across
/// many connections, runs the offline and connected handshakes in both roles, and produces one ordered
/// <see cref="PeerEvent"/> stream.
/// </summary>
/// <remarks>
/// <para>
/// Times are microseconds and always passed in; RakNet::Time values written on the wire are those times in
/// milliseconds.
/// </para>
/// <para>
/// Inputs: <see cref="Receive"/> for each datagram the socket reads (the offline half of ProcessNetworkPacket runs
/// immediately; connected datagrams go to the connection's <see cref="ReliabilityLayer"/>), <see cref="Update"/> for
/// each tick of RunUpdateCycle (connection attempts, keepalive and connected pings, reliability updates, failure checks,
/// then the messages each connection delivered), and the commands <see cref="Connect"/>, <see cref="Send(IPEndPoint, ReadOnlySpan{byte}, Reliability, Priority, byte, long)"/>,
/// <see cref="CloseConnection(IPEndPoint, bool, byte, Priority, long)"/>, <see cref="Ping"/>, <see cref="PingConnected"/>,
/// <see cref="AdvertiseSystem"/> and the rest. Commands apply at
/// once rather than being buffered for the next tick as RakPeer does across threads; their effects still leave on
/// the next <see cref="Update"/>, as RakPeer's do.
/// </para>
/// <para>
/// Outputs: <see cref="TryDequeueDatagram"/> and <see cref="TryDequeueEvent"/>.
/// </para>
/// <para>
/// The client discovers the MTU as RakPeer does: OPEN_CONNECTION_REQUEST_1 probes step down through
/// <see cref="RakNetConstants.MtuSizes"/> as unanswered attempts accumulate, and the server answers with the probe's
/// size capped at <see cref="RakNetConstants.MaximumMtuSize"/>. Every offline rejection RakPeer sends or reports is
/// produced, including the security ones the offline handshake detects before any cryptography runs.
/// </para>
/// <para>
/// Each connection keeps RakPeer's history of its last five pongs, from which the ping statistics and the clock
/// differential come. Incoming ID_TIMESTAMP messages leave the core as they arrived: RakPeer::Receive shifts them as the
/// application reads them (RakPeer.cpp:1548-1555), which is the layer above's job.
/// </para>
/// </remarks>
internal sealed class PeerCore
{
    // How long a connection may stay half-open, RakPeer.cpp:5877-5881.
    private const long HandshakeTimeoutMs = 10_000;

    // RakPeer.cpp:5926: ping every 5 s until the first pong.
    private const long PingIntervalMs = 5_000;

    // defaultMTUSize, mtuSizes[NUM_MTU_SIZES-1] (RakPeer.cpp:206).
    private const int DefaultMtu = 576;

    // RakPeer.cpp:3611-3612: with the frequency limit, an IP may take a new slot once per 100 ms.
    private const long ConnectionFrequencyWindowMs = 100;

    private readonly PeerOptions _options;
    private readonly RemoteSystem?[] _slots;
    private readonly List<RemoteSystem> _active = [];
    private readonly List<ConnectionAttempt> _attempts = [];
    private readonly Queue<OutgoingDatagram> _datagrams = new();
    private readonly Queue<PeerEvent> _events = new();
    private readonly BanTable _bans = new();
    private byte[] _offlinePingResponse = [];
    private long _defaultTimeoutMs;
    private long _unreliableTimeoutMs;
    private int _splitMessageProgressInterval;
    private uint _nextSendReceipt = 1;

    // firstExternalID (RakPeer.cpp:240): this peer's address as the first system it connected with saw it.
    private IPEndPoint _firstExternalAddress = InternalAddressList.Unassigned;

    public PeerCore(PeerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConnections);
        if (options.LocalAddresses.Count != RakNetConstants.MaximumNumberOfInternalIds)
            throw new ArgumentException($"The local address list must have {RakNetConstants.MaximumNumberOfInternalIds} entries.", nameof(options));
        _options = options;
        _slots = new RemoteSystem?[options.MaxConnections];
        MaxIncomingConnections = Math.Min(options.MaxIncomingConnections, options.MaxConnections);
        LimitIpConnectionFrequency = options.LimitIpConnectionFrequency;
        _defaultTimeoutMs = options.TimeoutMs;
        _unreliableTimeoutMs = options.UnreliableTimeoutMs;
        _splitMessageProgressInterval = options.SplitMessageProgressInterval;
        OutgoingBandwidthLimit = options.OutgoingBandwidthLimit;
        AllowConnectionResponseIpMigration = options.AllowConnectionResponseIpMigration;
        IncomingPassword = options.IncomingPassword;
    }

    public ulong Guid => _options.Guid;

    /// <summary>SetMaximumIncomingConnections.</summary>
    public int MaxIncomingConnections { get; set; }

    /// <summary>
    /// SetLimitIPConnectionFrequency: refuse a new connection from an IP address, other than this host's own, that took
    /// a slot less than 100 ms ago (RakPeer.cpp:3604-3625). The server answers ID_IP_RECENTLY_CONNECTED; a client
    /// abandons the attempt without an event.
    /// </summary>
    public bool LimitIpConnectionFrequency { get; set; }

    /// <summary>
    /// SetOccasionalPing (RakPeer.cpp:2218-2221): keep pinging every connected system every 5 s rather than only until
    /// its first pong (RakPeer.cpp:5924). Off by default, as GET_TIME_SPIKE_LIMIT is 0 (RakPeer.cpp:225-229).
    /// </summary>
    public bool OccasionalPing { get; set; }

    /// <summary>
    /// SetUnreliableTimeout (RakPeer.cpp:2719-2725): unreliable messages still queued this long after they were sent are
    /// discarded. Applies to every connection, now and later.
    /// </summary>
    public long UnreliableTimeoutMs
    {
        get => _unreliableTimeoutMs;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _unreliableTimeoutMs = value;
            foreach (var system in _slots)
            {
                if (system is not null)
                    system.Session.UnreliableTimeoutMs = value;
            }
        }
    }

    /// <summary>
    /// SetSplitMessageProgressInterval (RakPeer.cpp:2697-2704): while a split message arrives, ID_DOWNLOAD_PROGRESS is
    /// delivered every this many parts; 0 never. Applies to every connection, now and later.
    /// </summary>
    public int SplitMessageProgressInterval
    {
        get => _splitMessageProgressInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _splitMessageProgressInterval = value;
            foreach (var system in _slots)
            {
                if (system is not null)
                    system.Session.SplitMessageProgressInterval = value;
            }
        }
    }

    /// <summary>
    /// SetPerConnectionOutgoingBandwidthLimit (RakPeer.cpp:2961-2964): bits per second of new user data each connection
    /// may send, passed to every reliability layer update (RakPeer.cpp:5871); 0 is unlimited.
    /// </summary>
    public uint OutgoingBandwidthLimit { get; set; }

    /// <summary>
    /// AllowConnectionResponseIPMigration (RakPeer.cpp:2666-2670): accept ID_CONNECTION_REQUEST_ACCEPTED from a system
    /// that is not waiting for one (RakPeer.cpp:6133-6137).
    /// </summary>
    public bool AllowConnectionResponseIpMigration { get; set; }

    /// <summary>SetIncomingPassword: what later ID_CONNECTION_REQUEST messages must carry.</summary>
    public ReadOnlyMemory<byte> IncomingPassword { get; set; }

    /// <summary>The ban list. Thread-safe, as RakPeer's is, so the layer above may query and change it from any thread.</summary>
    public BanTable Bans => _bans;

    /// <summary>
    /// ChangeSystemAddress (RakPeer.cpp:2846-2856, 5646-5654): the system with <paramref name="guid"/> is from now on
    /// reached at, and recognised by, <paramref name="address"/>. Nothing happens when no system has the GUID.
    /// </summary>
    public void ChangeSystemAddress(ulong guid, IPEndPoint address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (FindActive(guid) is { } system)
        {
            system.Address = address;
            SlotsVersion++;
        }
    }

    /// <summary>
    /// SetTimeoutTime (RakPeer.cpp:2524-2547): how long a connection may go without hearing from the remote system
    /// while it has reliable messages unacknowledged before it is lost.
    /// </summary>
    /// <param name="timeoutMs">The timeout.</param>
    /// <param name="remote">The connected system to change, or null to change the default and every active
    /// connection.</param>
    public void SetTimeoutTime(long timeoutMs, IPEndPoint? remote)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);
        if (remote is null)
        {
            _defaultTimeoutMs = timeoutMs;
            foreach (var system in _active)
                system.Session.TimeoutMs = timeoutMs;
        }
        else if (FindActive(remote) is { } system)
        {
            system.Session.TimeoutMs = timeoutMs;
        }
    }

    /// <summary>
    /// GetTimeoutTime (RakPeer.cpp:2551-2564). Like RakPeer, it returns the default even when
    /// <paramref name="remote"/> has its own timeout: the C++ reads the connection's timeout and discards it.
    /// </summary>
    public long GetTimeoutTime(IPEndPoint? remote) => _defaultTimeoutMs;

    /// <summary>
    /// GetAveragePing (RakPeer.cpp:2149-2170): the mean round trip in milliseconds of the pongs in the history, or -1
    /// when there are none or no system is known at <paramref name="remote"/>.
    /// </summary>
    public int GetAveragePing(IPEndPoint remote) => AveragePing(FindForQuery(remote));

    /// <inheritdoc cref="GetAveragePing(IPEndPoint)"/>
    public int GetAveragePing(ulong guid) => AveragePing(FindActive(guid));

    /// <summary>
    /// GetLastPing (RakPeer.cpp:2178-2191): the most recent round trip in milliseconds, 65535 before the first pong,
    /// or -1 when no system is known.
    /// </summary>
    public int GetLastPing(IPEndPoint remote) => LastPing(FindForQuery(remote));

    /// <inheritdoc cref="GetLastPing(IPEndPoint)"/>
    public int GetLastPing(ulong guid) => LastPing(FindActive(guid));

    /// <summary>
    /// GetLowestPing (RakPeer.cpp:2200-2208): the lowest round trip in milliseconds ever measured, 65535 before the
    /// first pong, or -1 when no system is known.
    /// </summary>
    public int GetLowestPing(IPEndPoint remote) => FindForQuery(remote)?.LowestPing ?? -1;

    /// <inheritdoc cref="GetLowestPing(IPEndPoint)"/>
    public int GetLowestPing(ulong guid) => FindActive(guid)?.LowestPing ?? -1;

    /// <summary>
    /// GetClockDifferential (RakPeer.cpp:2228-2258): how many milliseconds the remote clock is ahead of this one,
    /// taken from the pong in the history with the lowest round trip, or 0 when there is none. Subtracting it from a
    /// remote time gives the local time; incoming ID_TIMESTAMP messages are already shifted by it.
    /// </summary>
    public long GetClockDifferential(IPEndPoint remote) => ClockDifferential(FindForQuery(remote));

    /// <inheritdoc cref="GetClockDifferential(IPEndPoint)"/>
    public long GetClockDifferential(ulong guid) => ClockDifferential(FindActive(guid));

    /// <summary>
    /// AddToBanList (RakPeer.cpp:1861-1900): every datagram from a matching address is answered with
    /// ID_CONNECTION_BANNED and otherwise ignored, connected or not.
    /// </summary>
    /// <param name="pattern">A dotted IPv4 address, where <c>*</c> matches everything from that character on, such as
    /// <c>128.0.0.*</c>. Empty patterns and those over 15 characters are ignored.</param>
    /// <param name="durationMs">How long the ban lasts; 0 bans forever. Banning a pattern again replaces its duration.</param>
    /// <param name="nowUs">The current time.</param>
    public void AddToBanList(string pattern, long durationMs, long nowUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);
        _bans.Add(pattern, durationMs, nowUs / 1000);
    }

    /// <summary>RemoveFromBanList (RakPeer.cpp:1910-1941): lifts the ban on exactly <paramref name="pattern"/>.</summary>
    public void RemoveFromBanList(string pattern) => _bans.Remove(pattern);

    /// <summary>ClearBanList (RakPeer.cpp:1948-1963).</summary>
    public void ClearBanList() => _bans.Clear();

    /// <summary>IsBanned (RakPeer.cpp:1981-2049) for a dotted address without a port.</summary>
    public bool IsBanned(string address, long nowUs) => _bans.IsBanned(address, nowUs / 1000);

    /// <summary>
    /// GetMTUSize (RakPeer.cpp:2570-2581): the MTU agreed with the connected system at <paramref name="remote"/>, or
    /// the default of 576 when there is none.
    /// </summary>
    public int GetMtuSize(IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return FindActive(remote)?.Mtu ?? DefaultMtu;
    }

    /// <summary>SetOfflinePingResponse: the data appended to ID_UNCONNECTED_PONG, under 400 bytes (RakPeer.cpp:2271).</summary>
    public void SetOfflinePingResponse(ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(data.Length, RakNetConstants.MaxOfflineDataLength, nameof(data));
        _offlinePingResponse = data.ToArray();
    }

    public bool TryDequeueDatagram(out OutgoingDatagram datagram) => _datagrams.TryDequeue(out datagram);

    /// <summary>
    /// The next event, in the order RakPeer adds packets to packetReturnQueue. An ID_TIMESTAMP message is not shifted
    /// here: RakPeer::Receive shifts every ID_TIMESTAMP packet when it is read, pushed-back ones included
    /// (RakPeer.cpp:1548-1555).
    /// </summary>
    public bool TryDequeueEvent([NotNullWhen(true)] out PeerEvent? peerEvent) => _events.TryDequeue(out peerEvent);

    /// <summary>
    /// remoteSystemList: one entry per slot, null until first used. A slot keeps its last system, with
    /// <see cref="RemoteSystem.IsActive"/> clear and its GUID unassigned, until it is reused.
    /// </summary>
    public IReadOnlyList<RemoteSystem?> Slots => _slots;

    /// <summary>
    /// activeSystemList in RakPeer's order: appended to on assignment, and a removal moves the last entry into the
    /// hole (AddToActiveSystemList and RemoveFromActiveSystemList, RakPeer.cpp:3940-3958).
    /// </summary>
    public IReadOnlyList<RemoteSystem> ActiveSystems => _active;

    /// <summary>Changes whenever a slot is assigned, deactivated or given a new address, so the layer above can tell when to look again.</summary>
    public int SlotsVersion { get; private set; }

    // RakPeer.cpp:2157-2169.
    internal static int AveragePing(RemoteSystem? system)
    {
        if (system is null)
            return -1;
        int sum = 0, quantity;
        for (quantity = 0; quantity < RemoteSystem.PingHistoryLength; quantity++)
        {
            var pingTime = system.PingHistory[quantity].PingTime;
            if (pingTime == ushort.MaxValue)
                break;
            sum += pingTime;
        }
        return quantity > 0 ? sum / quantity : -1;
    }

    // RakPeer.cpp:2187-2190.
    internal static int LastPing(RemoteSystem? system) =>
        system is null
            ? -1
            : system.PingHistory[system.PingWriteIndex == 0 ? RemoteSystem.PingHistoryLength - 1 : system.PingWriteIndex - 1].PingTime;

    // GetClockDifferentialInt, RakPeer.cpp:2238-2258.
    internal static long ClockDifferential(RemoteSystem? system)
    {
        if (system is null)
            return 0;
        var lowestPingSoFar = (int)ushort.MaxValue;
        ulong clockDifferential = 0;
        foreach (var sample in system.PingHistory)
        {
            if (sample.PingTime == ushort.MaxValue)
                break;
            if (sample.PingTime < lowestPingSoFar)
            {
                clockDifferential = sample.ClockDifferential;
                lowestPingSoFar = sample.PingTime;
            }
        }
        return unchecked((long)clockDifferential);
    }

    /// <summary>
    /// RakPeer::Connect and SendConnectionRequest (RakPeer.cpp:990-1018, 3266-3321): queue an offline handshake with
    /// <paramref name="remote"/>. The first OPEN_CONNECTION_REQUEST_1 leaves on the first <see cref="Update"/> after
    /// <paramref name="nowUs"/>.
    /// </summary>
    /// <param name="remote">The server.</param>
    /// <param name="password">Sent in ID_CONNECTION_REQUEST; at most 255 bytes are used.</param>
    /// <param name="attemptCount">sendConnectionAttemptCount: requests are sent until this many plus one have gone.</param>
    /// <param name="intervalMs">timeBetweenSendConnectionAttemptsMS.</param>
    /// <param name="timeoutMs">The connection's timeout, or 0 for <see cref="PeerOptions.TimeoutMs"/>.</param>
    /// <param name="nowUs">The current time.</param>
    /// <param name="security">The client's public key mode, or null for an insecure connection. The offline handshake
    /// reports ID_OUR_SYSTEM_REQUIRES_SECURITY and ID_PUBLIC_KEY_MISMATCH from it; a secure handshake itself does not
    /// start, so a secure server's reply carrying the expected key is ignored.</param>
    /// <exception cref="ArgumentException">The expected server key is neither empty nor
    /// <see cref="SecureOpenConnectionReply1.PublicKeyLength"/> bytes.</exception>
    public ConnectionAttemptResult Connect(IPEndPoint remote, ReadOnlySpan<byte> password, int attemptCount, int intervalMs, long timeoutMs, long nowUs, ClientSecurity? security = null)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (security is not null && !security.ServerPublicKey.IsEmpty && security.ServerPublicKey.Length != SecureOpenConnectionReply1.PublicKeyLength)
            throw new ArgumentException($"A server public key is {SecureOpenConnectionReply1.PublicKeyLength} bytes.", nameof(security));
        if (remote.Port == 0)
            return ConnectionAttemptResult.InvalidParameter;
        if (password.Length > 255)
            password = password[..255];
        if (FindActive(remote) is not null)
            return ConnectionAttemptResult.AlreadyConnectedToEndpoint;
        if (FindAttempt(remote) is not null)
            return ConnectionAttemptResult.ConnectionAttemptAlreadyInProgress;

        _attempts.Add(new ConnectionAttempt(remote, password.ToArray(), attemptCount, intervalMs, timeoutMs, security, nowUs / 1000));
        return ConnectionAttemptResult.ConnectionAttemptStarted;
    }

    /// <summary>
    /// CancelConnectionAttempt (RakPeer.cpp:1686-1710): forget a <see cref="Connect"/> still in its offline handshake,
    /// without an event. A connection already past the offline handshake is not affected.
    /// </summary>
    public void CancelConnectionAttempt(IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (FindAttempt(remote) is { } attempt)
            _attempts.Remove(attempt);
    }

    /// <summary>
    /// The transport could not send an MTU probe (<see cref="OutgoingDatagram.DoNotFragment"/>) to
    /// <paramref name="remote"/> because it is larger than the path allows. As RakPeer does on WSAEMSGSIZE
    /// (RakPeer.cpp:5791-5797), the attempts left at that MTU are skipped and the next size is probed on the next
    /// <see cref="Update"/>.
    /// </summary>
    public void ReportDatagramTooLarge(IPEndPoint remote, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var attempt = FindAttempt(remote);
        if (attempt is null)
            return;
        var attemptsPerMtu = attempt.AttemptCount / RakNetConstants.MtuSizes.Length;
        // With fewer attempts than MTU sizes the C++ sets requestsMade to 0 and probes the same size on every update
        // without end; the attempt fails instead.
        attempt.RequestsMade = attemptsPerMtu <= 0
            ? unchecked((byte)(attempt.AttemptCount + 1))
            : unchecked((byte)((attempt.LastMtuIndex + 1) * attemptsPerMtu));
        attempt.NextRequestTimeMs = nowUs / 1000;
    }

    /// <summary>
    /// The transport took more than 100 ms to send an MTU probe (<see cref="OutgoingDatagram.DoNotFragment"/>) to
    /// <paramref name="remote"/>. As RakPeer does (RakPeer.cpp:5798-5812), the attempt drops to the smallest MTU and
    /// probes again on the next <see cref="Update"/>, or ends if it has already reached the smallest MTU.
    /// </summary>
    public void ReportProbeSendSlow(IPEndPoint remote, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var attempt = FindAttempt(remote);
        if (attempt is null)
            return;
        var lowestMtuIndex = attempt.AttemptCount / RakNetConstants.MtuSizes.Length * (RakNetConstants.MtuSizes.Length - 1);
        if (lowestMtuIndex > attempt.RequestsMade)
        {
            attempt.RequestsMade = (byte)lowestMtuIndex;
            attempt.NextRequestTimeMs = nowUs / 1000;
        }
        else
        {
            attempt.RequestsMade = unchecked((byte)(attempt.AttemptCount + 1));
        }
    }

    /// <summary>
    /// RakPeer::Ping(host, port, onlyReplyOnAcceptingConnections) (RakPeer.cpp:2090-2142): an unconnected ping.
    /// </summary>
    public bool Ping(IPEndPoint remote, bool onlyIfAccepting, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        SendOffline(remote, new UnconnectedPing(onlyIfAccepting, (ulong)(nowUs / 1000), Guid));
        return true;
    }

    /// <summary>
    /// RakPeer::Ping(SystemAddress) (RakPeer.cpp:2073-2076, PingInternal at 4103-4115): an unreliable ID_CONNECTED_PING
    /// to a connected system, whose pong adds to its ping and clock differential history. Nothing is sent to an
    /// address that is not connected. Like RakPeer, which sends the ping through Send, it uses up a send receipt
    /// number either way (RakPeer.cpp:1385-1389).
    /// </summary>
    public void PingConnected(IPEndPoint remote, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var receipt = NextSendReceipt();
        if (FindActive(remote) is { } system)
            PingInternal(system, Reliability.Unreliable, nowUs, receipt);
    }

    /// <summary>
    /// AdvertiseSystem (RakPeer.cpp:2683-2689): sends ID_ADVERTISE_SYSTEM and <paramref name="data"/> out of band, so
    /// the remote system sees it whether or not it is connected or accepting connections.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="data"/> is longer than
    /// <see cref="Wire.Messages.AdvertiseSystem.MaxDataLength"/>, which the receiver would drop.</exception>
    public void AdvertiseSystem(IPEndPoint remote, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, Wire.Messages.AdvertiseSystem.MaxDataLength, nameof(data));
        SendOffline(remote, new AdvertiseSystem(Guid, data.ToArray()));
    }

    /// <summary>
    /// SendOutOfBand (RakPeer.cpp:2997-3040): sends <paramref name="data"/>, which starts with a message ID, after the
    /// out-of-band header. The remote system reports it as ID_OUT_OF_BAND_INTERNAL followed by the data, or as
    /// ID_ADVERTISE_SYSTEM when the data starts with that ID; data that is empty is sent but ignored by the receiver.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="data"/> is longer than
    /// <see cref="OutOfBandMessage.MaxPayloadLength"/>, which the receiver would drop.</exception>
    public void SendOutOfBand(IPEndPoint remote, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, OutOfBandMessage.MaxPayloadLength, nameof(data));
        SendOffline(remote, new OutOfBandMessage(Guid, data.ToArray()));
    }

    /// <summary>
    /// RakPeer::Send (RakPeer.cpp:1308-1352): sends to a connected system and returns the receipt number used.
    /// Like RakPeer, a receipt is returned even when the target is not connected or the data is empty and nothing is
    /// sent (RakPeer.cpp:1317 rejects only a negative length; ReliabilityLayer.cpp:1567-1570 drops a 0-bit message).
    /// </summary>
    public uint Send(IPEndPoint remote, ReadOnlySpan<byte> data, Reliability reliability, Priority priority, byte channel, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var receipt = NextSendReceipt();
        var system = FindActive(remote);
        if (system is not null)
            SendImmediate(system, data, reliability, priority, channel, receipt, nowUs);
        return receipt;
    }

    /// <summary>Sends <paramref name="bitLength"/> bits of <paramref name="data"/>, as Send(BitStream*) does (RakPeer.cpp:1408).</summary>
    public uint Send(ulong guid, ReadOnlySpan<byte> data, int bitLength, Reliability reliability, Priority priority, byte channel, long nowUs)
    {
        if (guid == RakNetConstants.UnassignedGuid)
            return 0;
        var receipt = NextSendReceipt();
        if (FindActive(guid) is { } system)
            SendImmediate(system, data, bitLength, reliability, priority, channel, receipt, nowUs);
        return receipt;
    }

    /// <summary>
    /// One recipient of a broadcast send (SendImmediate with broadcast true, RakPeer.cpp:4335-4370): queued on
    /// <paramref name="system"/> while its slot is active, whatever its connect mode, and returns the receipt number used.
    /// </summary>
    public uint SendToSlot(RemoteSystem system, ReadOnlySpan<byte> data, int bitLength, Reliability reliability, Priority priority, byte channel, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(system);
        var receipt = NextSendReceipt();
        if (system.IsActive)
        {
            system.Session.Send(data, bitLength, reliability, priority, channel, receipt, nowUs);
            if (reliability.IsReliable())
                system.LastReliableSendMs = nowUs / 1000;
        }
        return receipt;
    }

    /// <summary>Sends to the system with <paramref name="guid"/>.</summary>
    public uint Send(ulong guid, ReadOnlySpan<byte> data, Reliability reliability, Priority priority, byte channel, long nowUs)
    {
        // RakPeer.cpp:1323-1324: an undefined target returns 0 without using a receipt number.
        if (guid == RakNetConstants.UnassignedGuid)
            return 0;
        var system = FindActive(guid);
        if (system is null)
            return NextSendReceipt();
        return Send(system.Address, data, reliability, priority, channel, nowUs);
    }

    /// <summary>
    /// RakPeer::CloseConnection (RakPeer.cpp:1650-1679, 4117-4187, 3557-3571). With notification, ID_DISCONNECTION_NOTIFICATION
    /// is sent RELIABLE_ORDERED and the connection closes once everything sent has been acknowledged; both sides then
    /// see <see cref="Disconnected"/>. Without, the connection is dropped at once and this peer sees
    /// <see cref="ConnectionLost"/> if it was connected.
    /// </summary>
    public void CloseConnection(IPEndPoint remote, bool sendDisconnectionNotification, byte channel, Priority priority, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var system = FindActive(remote);
        if (system is null)
            return;

        if (sendDisconnectionNotification)
        {
            SendImmediate(system, Encode(new DisconnectionNotification()), Reliability.ReliableOrdered, priority, channel, 0, nowUs);
            system.Mode = ConnectMode.DisconnectAsap;
            return;
        }

        var wasConnected = system.Mode == ConnectMode.Connected;
        var guid = system.Guid;
        Deactivate(system);
        if (wasConnected)
            _events.Enqueue(new ConnectionLost(remote, guid));
    }

    /// <summary>Closes the connection to the system with <paramref name="guid"/>.</summary>
    public void CloseConnection(ulong guid, bool sendDisconnectionNotification, byte channel, Priority priority, long nowUs)
    {
        var system = FindActive(guid);
        if (system is not null)
            CloseConnection(system.Address, sendDisconnectionNotification, channel, priority, nowUs);
    }

    /// <summary>The number of remote systems with an active slot, in any connect mode.</summary>
    public int ActiveSystemCount => _active.Count;

    /// <summary>
    /// The first half of RakPeer::Shutdown (RakPeer.cpp:1050-1058): NotifyAndFlagForShutdown for every active system,
    /// so each closes once its ID_DISCONNECTION_NOTIFICATION is delivered. The caller keeps updating until
    /// <see cref="ActiveSystemCount"/> is 0 or its block time runs out.
    /// </summary>
    public void NotifyAllForShutdown(byte channel, Priority priority, long nowUs)
    {
        foreach (var system in _active.ToList())
            CloseConnection(system.Address, sendDisconnectionNotification: true, channel, priority, nowUs);
    }

    /// <summary>RakPeer::GetConnectionState for an address (RakPeer.cpp:1717-1770).</summary>
    public ConnectionState GetConnectionState(IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (FindAttempt(remote) is not null)
            return ConnectionState.IsPending;
        // GetIndexFromSystemAddress(address, false) prefers an active slot, then one previously used by the address.
        return StateOf(FindForQuery(remote));
    }

    /// <summary>RakPeer::GetConnectionState for a GUID; a closed connection's GUID is forgotten.</summary>
    public ConnectionState GetConnectionState(ulong guid) => StateOf(FindActive(guid));

    /// <summary>The address of the connected system with <paramref name="guid"/>.</summary>
    public bool TryGetAddress(ulong guid, [NotNullWhen(true)] out IPEndPoint? address)
    {
        address = FindActive(guid)?.Address;
        return address is not null;
    }

    /// <summary>ProcessNetworkPacket (RakPeer.cpp:5429-5470) for one datagram read from the socket.</summary>
    public void Receive(IPEndPoint from, ReadOnlySpan<byte> datagram, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(from);
        // RakPeer.cpp:4550-4580: a banned address gets ID_CONNECTION_BANNED whatever it sent.
        if (!_bans.IsEmpty && _bans.IsBanned(from.Address.ToString(), nowUs / 1000))
        {
            SendOffline(from, new ConnectionBanned(Guid));
            return;
        }
        if (ProcessOffline(from, datagram, nowUs))
            return;
        FindActive(from)?.Session.Receive(datagram, nowUs);
    }

    /// <summary>One RunUpdateCycle (RakPeer.cpp:5567-6253), after the datagrams it would have read were received.</summary>
    public void Update(long nowUs)
    {
        var nowMs = nowUs / 1000;
        UpdateConnectionAttempts(nowMs);

        // RakPeer.cpp:5832: by index over activeSystemList. A system closed during its own turn has the last entry moved
        // into its place, and the loop moves on, so that entry waits for the next update.
        for (var index = 0; index < _active.Count; index++)
        {
            var system = _active[index];
            // Keepalive, RakPeer.cpp:5857-5869: a reliable ping so a dead connection is noticed.
            if (system.Mode == ConnectMode.Connected
                && nowMs > system.LastReliableSendMs
                && nowMs - system.LastReliableSendMs > system.Session.TimeoutMs / 2
                && system.Session.MessagesInResendBuffer == 0)
            {
                PingInternal(system, Reliability.Reliable, nowUs);
                system.LastReliableSendMs = nowMs;
            }

            system.Session.Update(nowUs, OutgoingBandwidthLimit);
            while (system.Session.TryDequeueDatagram(out var datagram))
                _datagrams.Enqueue(new OutgoingDatagram(system.Address, datagram));

            if (HasFailed(system, nowMs))
            {
                ReportClosed(system);
                Deactivate(system);
                continue;
            }

            // RakPeer.cpp:5924-5931: ping until the first pong arrives, or every 5 s with occasional pings.
            if (system.Mode == ConnectMode.Connected && nowMs > system.NextPingTimeMs && (OccasionalPing || system.LowestPing == ushort.MaxValue))
            {
                system.NextPingTimeMs = nowMs + PingIntervalMs;
                PingInternal(system, Reliability.Unreliable, nowUs);
            }

            while (system.IsActive && system.Session.TryDequeueEvent(out var sessionEvent))
                ProcessSessionEvent(system, sessionEvent, nowUs);
        }
    }

    // RakPeer.cpp:5691-5829.
    private void UpdateConnectionAttempts(long nowMs)
    {
        foreach (var attempt in _attempts.ToList())
        {
            if (attempt.NextRequestTimeMs >= nowMs)
                continue;
            if (attempt.RequestsMade == attempt.AttemptCount + 1)
            {
                _attempts.Remove(attempt);
                _events.Enqueue(new ConnectionAttemptFailed(attempt.Address, RakNetConstants.UnassignedGuid, new[] { (byte)MessageId.ConnectionAttemptFailed }));
                continue;
            }
            var mtuIndex = ProbeMtuIndex(attempt);
            attempt.LastMtuIndex = mtuIndex;
            attempt.RequestsMade++;
            attempt.NextRequestTimeMs = nowMs + attempt.IntervalMs;
            var mtu = RakNetConstants.MtuSizes[mtuIndex];
            var probe = Encode(new OpenConnectionRequest1(RakNetConstants.ProtocolVersion, mtu), mtu);
            _datagrams.Enqueue(new OutgoingDatagram(attempt.Address, probe) { DoNotFragment = true });
        }
    }

    // RakPeer.cpp:5750-5752: each MTU size gets an equal share of the attempts, and the rest go to the smallest.
    private static int ProbeMtuIndex(ConnectionAttempt attempt)
    {
        var attemptsPerMtu = attempt.AttemptCount / RakNetConstants.MtuSizes.Length;
        // Below three attempts the C++ divides by zero, which arm64 evaluates as 0: the native peer probes the largest
        // size every time, so the core does too.
        if (attemptsPerMtu <= 0)
            return 0;
        return Math.Min(attempt.RequestsMade / attemptsPerMtu, RakNetConstants.MtuSizes.Length - 1);
    }

    // The failure conditions of RakPeer.cpp:5874-5882.
    private static bool HasFailed(RemoteSystem system, long nowMs)
    {
        var session = system.Session;
        return session.IsDead
            || (system.Mode is ConnectMode.DisconnectAsap or ConnectMode.DisconnectAsapSilently && !session.IsOutgoingDataWaiting)
            || (system.Mode == ConnectMode.DisconnectOnNoAck && (!session.AreAcksWaiting || session.AckTimeout(nowMs)))
            || (system.Mode is ConnectMode.RequestedConnection or ConnectMode.HandlingConnectionRequest or ConnectMode.UnverifiedSender
                && nowMs > system.ConnectionTimeMs
                && nowMs - system.ConnectionTimeMs > HandshakeTimeoutMs);
    }

    // RakPeer.cpp:5889-5913.
    private void ReportClosed(RemoteSystem system)
    {
        switch (system.Mode)
        {
            case ConnectMode.RequestedConnection:
                _events.Enqueue(new ConnectionAttemptFailed(system.Address, system.Guid, new[] { (byte)MessageId.ConnectionAttemptFailed }));
                break;
            case ConnectMode.Connected:
                _events.Enqueue(new ConnectionLost(system.Address, system.Guid));
                break;
            case ConnectMode.DisconnectAsap or ConnectMode.DisconnectOnNoAck:
                _events.Enqueue(new Disconnected(system.Address, system.Guid));
                break;
            default:
                break; // Shutting down silently, or never verified: the application is not told.
        }
    }

    private void ProcessSessionEvent(RemoteSystem system, SessionEvent sessionEvent, long nowUs)
    {
        switch (sessionEvent)
        {
            case MessageDelivered delivered:
                ProcessMessage(system, delivered.Payload, delivered.BitLength, nowUs);
                break;
            case ReceiptAcknowledged acknowledged:
                _events.Enqueue(new SendReceiptAcked(system.Address, system.Guid, acknowledged.Receipt));
                break;
            case ReceiptLost lost:
                _events.Enqueue(new SendReceiptLost(system.Address, system.Guid, lost.Receipt));
                break;
            case ReceiptDiscarded discarded:
                _events.Enqueue(new SendReceiptDiscarded(system.Address, system.Guid, discarded.Receipt));
                break;
            default:
                break; // ConnectionDied is acted on by the failure check.
        }
    }

    // The Receive loop of RunUpdateCycle, RakPeer.cpp:5939-6248.
    private void ProcessMessage(RemoteSystem system, ReadOnlyMemory<byte> payload, int bitLength, long nowUs)
    {
        var data = payload.Span;
        var id = (MessageId)data[0];
        var nowMs = nowUs / 1000;

        // Internal notifications never arrive from the network (RakPeer.cpp:5944-5949).
        if (id == MessageId.ConnectionAttemptFailed)
            return;

        if (system.Mode == ConnectMode.UnverifiedSender)
        {
            if (id == MessageId.ConnectionRequest)
                ParseConnectionRequest(system, data, nowUs);
            else
            {
                // RakPeer.cpp:5962-5974: closed, and the address banned for the connection's timeout.
                Deactivate(system);
                _bans.Add(system.Address.Address.ToString(), system.Session.TimeoutMs, nowMs);
            }
            return;
        }

        switch (id)
        {
            case MessageId.ConnectionRequest:
                if (system.Mode == ConnectMode.RequestedConnection)
                    ParseConnectionRequest(system, data, nowUs);
                else
                    // Already connected: reply normally. The C++ reads the time from after a magic that is not there
                    // (RakPeer.cpp:5992-5997); that misreading is kept.
                    OnConnectionRequest(system, data.Length >= 33 ? BinaryPrimitives.ReadUInt64BigEndian(data[25..]) : 0, nowUs);
                break;

            // RakPeer.cpp:6005: longer than the ID, a 4-byte address, a port and two times.
            case MessageId.NewIncomingConnection when data.Length > 1 + 4 + 2 + 8 * 2:
                if (system.Mode == ConnectMode.HandlingConnectionRequest && TryDecode<NewIncomingConnection>(data, out var incoming))
                {
                    system.Mode = ConnectMode.Connected;
                    PingInternal(system, Reliability.Unreliable, nowUs);
                    system.TheirInternalAddresses = incoming.InternalAddresses;
                    OnConnectedPong(system, incoming.SendPingTime, incoming.SendPongTime, nowMs);
                    system.MyExternalAddress = incoming.ServerAddress;
                    SetFirstExternalAddress(incoming.ServerAddress);
                    _events.Enqueue(new IncomingConnectionCompleted(system.Address, system.Guid, payload));
                }
                break;

            case MessageId.ConnectedPong:
                if (TryDecode<ConnectedPong>(data, out var pong))
                    OnConnectedPong(system, pong.SendPingTime, pong.SendPongTime, nowMs);
                break;

            case MessageId.ConnectedPing:
                if (TryDecode<ConnectedPing>(data, out var ping))
                {
                    var reply = Encode(new ConnectedPong(ping.SendPingTime, (ulong)nowMs));
                    SendImmediate(system, reply, Reliability.Unreliable, Priority.Immediate, 0, 0, nowUs);
                }
                break;

            case MessageId.DisconnectionNotification:
                // Close once our ack of the notification has gone (RakPeer.cpp:6095-6102).
                system.Mode = ConnectMode.DisconnectOnNoAck;
                break;

            case MessageId.DetectLostConnections:
                break;

            case MessageId.InvalidPassword:
                if (system.Mode == ConnectMode.RequestedConnection)
                {
                    _events.Enqueue(new ConnectionAttemptFailed(system.Address, system.Guid, payload));
                    system.Mode = ConnectMode.DisconnectAsapSilently;
                }
                break;

            case MessageId.ConnectionRequestAccepted:
                OnConnectionRequestAccepted(system, payload, nowUs);
                break;

            default:
                // RakPeer.cpp:6226-6241: application messages and receipts; other internal IDs are dropped.
                if (data[0] >= (byte)MessageId.Timestamp || id is MessageId.SndReceiptAcked or MessageId.SndReceiptLoss)
                    _events.Enqueue(new MessageReceived(system.Address, system.Guid, payload) { BitLength = bitLength });
                break;
        }
    }

    // RakPeer.cpp:6127-6220.
    private void OnConnectionRequestAccepted(RemoteSystem system, ReadOnlyMemory<byte> payload, long nowUs)
    {
        // Longer than the ID, a 4-byte address, a port, the system index and two times (RakPeer.cpp:6129).
        if (payload.Length <= 1 + 4 + 2 + 2 + 8 * 2)
            return;
        if (system.Mode is not (ConnectMode.HandlingConnectionRequest or ConnectMode.RequestedConnection) && !AllowConnectionResponseIpMigration)
            return;
        var alreadyConnected = system.Mode == ConnectMode.HandlingConnectionRequest;
        if (!TryDecode<ConnectionRequestAccepted>(payload.Span, out var accepted))
            return;

        var nowMs = nowUs / 1000;
        system.TheirInternalAddresses = accepted.InternalAddresses;
        OnConnectedPong(system, accepted.RequestTime, accepted.ReplyTime, nowMs);
        system.MyExternalAddress = accepted.ClientAddress;
        system.Mode = ConnectMode.Connected;
        SetFirstExternalAddress(accepted.ClientAddress);
        _events.Enqueue(new OutgoingConnectionCompleted(system.Address, system.Guid, payload, accepted.ClientAddress));

        var reply = new NewIncomingConnection(system.Address, _options.LocalAddresses, accepted.ReplyTime, (ulong)nowMs);
        SendImmediate(system, Encode(reply), Reliability.ReliableOrdered, Priority.Immediate, 0, 0, nowUs);
        if (!alreadyConnected)
            PingInternal(system, Reliability.Unreliable, nowUs);
    }

    // ParseConnectionRequestPacket, RakPeer.cpp:3447-3538.
    private void ParseConnectionRequest(RemoteSystem system, ReadOnlySpan<byte> data, long nowUs)
    {
        if (!TryDecode<ConnectionRequest>(data, out var request))
            return;
        if (!request.Password.Span.SequenceEqual(IncomingPassword.Span))
        {
            SendImmediate(system, Encode(new InvalidPassword(Guid)), Reliability.Reliable, Priority.Immediate, 0, 0, nowUs);
            system.Mode = ConnectMode.DisconnectAsapSilently;
            return;
        }
        system.Mode = ConnectMode.HandlingConnectionRequest;
        OnConnectionRequest(system, request.RequestTime, nowUs);
    }

    // OnConnectionRequest, RakPeer.cpp:3540-3554.
    private void OnConnectionRequest(RemoteSystem system, ulong requestTime, long nowUs)
    {
        var accepted = new ConnectionRequestAccepted(system.Address, (ushort)system.Index, _options.LocalAddresses, requestTime, (ulong)(nowUs / 1000));
        SendImmediate(system, Encode(accepted), Reliability.ReliableOrdered, Priority.Immediate, 0, 0, nowUs);
    }

    // OnConnectedPong, RakPeer.cpp:4393-4415.
    private static void OnConnectedPong(RemoteSystem system, ulong sendPingTime, ulong sendPongTime, long nowMs)
    {
        var time = (ulong)nowMs;
        var ping = time > sendPingTime ? time - sendPingTime : 0;
        // Each time is halved before adding, so the sum cannot overflow (RakPeer.cpp:4406-4408).
        system.PingHistory[system.PingWriteIndex] = new PingSample(unchecked((ushort)ping), unchecked(sendPongTime - (time / 2 + sendPingTime / 2)));
        if (system.LowestPing == ushort.MaxValue || system.LowestPing > (int)ping)
            system.LowestPing = unchecked((ushort)ping);
        if (++system.PingWriteIndex == RemoteSystem.PingHistoryLength)
            system.PingWriteIndex = 0;
    }

    // PingInternal, RakPeer.cpp:4103-4115.
    private static void PingInternal(RemoteSystem system, Reliability reliability, long nowUs, uint receipt = 0) =>
        SendImmediate(system, Encode(new ConnectedPing((ulong)(nowUs / 1000))), reliability, Priority.Immediate, 0, receipt, nowUs);

    // SendImmediate for one system, RakPeer.cpp:4286-4384.
    private static void SendImmediate(RemoteSystem system, ReadOnlySpan<byte> data, Reliability reliability, Priority priority, byte channel, uint receipt, long nowUs) =>
        SendImmediate(system, data, data.Length * 8, reliability, priority, channel, receipt, nowUs);

    private static void SendImmediate(RemoteSystem system, ReadOnlySpan<byte> data, int bitLength, Reliability reliability, Priority priority, byte channel, uint receipt, long nowUs)
    {
        if (!system.IsActive || system.Mode is ConnectMode.DisconnectAsap or ConnectMode.DisconnectAsapSilently or ConnectMode.DisconnectOnNoAck)
            return;
        system.Session.Send(data, bitLength, reliability, priority, channel, receipt, nowUs);
        if (reliability.IsReliable())
            system.LastReliableSendMs = nowUs / 1000;
    }

    // IncrementNextSendReceipt, RakPeer.cpp:1279-1287: never 0.
    private uint NextSendReceipt()
    {
        var receipt = _nextSendReceipt;
        if (++_nextSendReceipt == 0)
            _nextSendReceipt = 1;
        return receipt;
    }

    // ProcessOfflineNetworkPacket, RakPeer.cpp:4540-5423. Returns true when the datagram was an offline message, which
    // never reaches a reliability layer.
    private bool ProcessOffline(IPEndPoint from, ReadOnlySpan<byte> data, long nowUs)
    {
        if (!IsOfflineMessage(data))
            return false;
        if (data.Length <= 2)
            return true;

        switch ((MessageId)data[0])
        {
            case MessageId.UnconnectedPing or MessageId.UnconnectedPingOpenConnections:
                OnUnconnectedPing(from, data);
                break;
            case MessageId.UnconnectedPong:
                OnUnconnectedPong(from, data);
                break;
            case MessageId.OpenConnectionReply1:
                OnOpenConnectionReply1(from, data);
                break;
            case MessageId.OpenConnectionReply2:
                OnOpenConnectionReply2(from, data, nowUs);
                break;
            case MessageId.ConnectionAttemptFailed
                or MessageId.NoFreeIncomingConnections
                or MessageId.ConnectionBanned
                or MessageId.AlreadyConnected
                or MessageId.IpRecentlyConnected
                or MessageId.IncompatibleProtocolVersion:
                OnRejection(from, data);
                break;
            case MessageId.OpenConnectionRequest1:
                OnOpenConnectionRequest1(from, data);
                break;
            case MessageId.OpenConnectionRequest2:
                OnOpenConnectionRequest2(from, data, nowUs);
                break;
            case MessageId.OutOfBandInternal:
                OnOutOfBand(from, data);
                break;
            default:
                break;
        }
        return true;
    }

    // RakPeer.cpp:4583-4627. The C++ checks the pong's length against a 4-byte TimeMS but its magic at the 8-byte
    // RakNet::Time offset; the magic is only compared here when the bytes are actually there.
    private static bool IsOfflineMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 2)
            return true;
        const int guidSize = 8;
        const int timeSize = 8;
        switch ((MessageId)data[0])
        {
            case MessageId.UnconnectedPing or MessageId.UnconnectedPingOpenConnections:
                return MagicAt(data, 1 + timeSize);
            case MessageId.UnconnectedPong:
                return MagicAt(data, 1 + timeSize + guidSize);
            case MessageId.OutOfBandInternal:
                return MagicAt(data, 1 + guidSize);
            case MessageId.OpenConnectionReply1
                or MessageId.OpenConnectionReply2
                or MessageId.OpenConnectionRequest1
                or MessageId.OpenConnectionRequest2
                or MessageId.ConnectionAttemptFailed
                or MessageId.NoFreeIncomingConnections
                or MessageId.ConnectionBanned
                or MessageId.AlreadyConnected
                or MessageId.IpRecentlyConnected:
                return data.Length >= 1 + guidSize + OfflineMagic.Length && MagicAt(data, 1);
            case MessageId.IncompatibleProtocolVersion:
                return data.Length == 2 + guidSize + OfflineMagic.Length && MagicAt(data, 2);
            default:
                return false;
        }
    }

    private static bool MagicAt(ReadOnlySpan<byte> data, int offset) =>
        data.Length >= offset + OfflineMagic.Length && OfflineMagic.Matches(data[offset..]);

    // RakPeer.cpp:4635-4680.
    private void OnUnconnectedPing(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!TryDecode<UnconnectedPing>(data, out var ping))
            return;
        if (ping.OpenConnectionsOnly && !AllowIncomingConnections())
            return;
        SendOffline(from, new UnconnectedPong(ping.SendPingTime, Guid, _offlinePingResponse));
        _events.Enqueue(new UnconnectedPingReceived(from, ping.ClientGuid ?? RakNetConstants.UnassignedGuid, ping.OpenConnectionsOnly));
    }

    // RakPeer.cpp:4682-4705.
    private void OnUnconnectedPong(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (TryDecode<UnconnectedPong>(data, out var pong))
            _events.Enqueue(new UnconnectedPongReceived(from, pong.ServerGuid, pong.SendPingTime, pong.ResponseData));
    }

    // RakPeer.cpp:4706-4730: ID_ADVERTISE_SYSTEM loses its header; anything else follows ID_OUT_OF_BAND_INTERNAL.
    private void OnOutOfBand(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!TryDecode<OutOfBandMessage>(data, out var message))
            return;
        if (message.Payload.Span[0] == (byte)MessageId.AdvertiseSystem)
            _events.Enqueue(new AdvertiseSystemReceived(from, message.SenderGuid, message.Payload[1..]));
        else
            _events.Enqueue(new OutOfBandReceived(from, message.SenderGuid, message.Payload));
    }

    // RakPeer.cpp:4737-4870: answer the server's MTU reply with OPEN_CONNECTION_REQUEST_2. The security rejections
    // leave the request queued, so it is probed again and each reply reports again until the attempts run out.
    private void OnOpenConnectionReply1(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (TryDecode<OpenConnectionReply1>(data, out var reply))
        {
            var attempt = FindAttempt(from);
            if (attempt is null)
                return;
            // RakPeer.cpp:4824-4838: we passed a public key but the server has no security.
            if (attempt.Security is not null)
            {
                _events.Enqueue(new ConnectionAttemptFailed(from, reply.ServerGuid, new[] { (byte)MessageId.OurSystemRequiresSecurity }));
                return;
            }
            SendOffline(from, new OpenConnectionRequest2(from, reply.MtuSize, Guid));
        }
        else if (TryDecode<SecureOpenConnectionReply1>(data, out var secure))
        {
            var attempt = FindAttempt(from);
            if (attempt is null)
                return;
            // RakPeer.cpp:4788-4801. PKM_ACCEPT_ANY_PUBLIC_KEY takes the server's key as the expected one; with
            // PKM_INSECURE_CONNECTION remote_public_key is never set (RakPeer.cpp:3197-3205), so no key matches it.
            var expected = attempt.Security switch
            {
                null => ReadOnlySpan<byte>.Empty,
                { ServerPublicKey.IsEmpty: true } => secure.ServerPublicKey.Span,
                _ => attempt.Security.ServerPublicKey.Span,
            };
            if (!expected.SequenceEqual(secure.ServerPublicKey.Span))
                _events.Enqueue(new ConnectionAttemptFailed(from, secure.ServerGuid, new[] { (byte)MessageId.PublicKeyMismatch }));
            // Otherwise the client would answer with its handshake challenge, which needs the cat key agreement
            // (RakPeer.cpp:4803-4818); without it the reply is ignored and the attempt runs out.
        }
    }

    // RakPeer.cpp:4871-5074: the offline handshake is done, so start the connected one.
    private void OnOpenConnectionReply2(IPEndPoint from, ReadOnlySpan<byte> data, long nowUs)
    {
        if (!TryDecode<OpenConnectionReply2>(data, out var reply))
        {
            // RakPeer.cpp:4914-4930: the server requires security and we passed no public key. The packet is the ID and
            // 0 (the server public key is missing), and the request stays queued.
            if (reply.RequiresSecurity && FindAttempt(from) is { Security: null })
                _events.Enqueue(new ConnectionAttemptFailed(from, reply.ServerGuid, new byte[] { (byte)MessageId.RemoteSystemRequiresPublicKey, 0 }));
            return;
        }
        var attempt = FindAttempt(from);
        if (attempt is null)
            return;
        // RakPeer.cpp:4966-4992: a request made with a ClientSecurity always processes the server's handshake answer,
        // which an insecure reply does not carry, and returns before ID_CONNECTION_REQUEST with the request still
        // queued. Otherwise a forged insecure reply would downgrade a secure connect. (The C++ assigns the slot before
        // reading the uninitialised answer; here nothing is assigned for a reply that can never complete.)
        if (attempt.Security is not null)
            return;

        // A cross connection may already have made this system (RakPeer.cpp:4941-4954).
        var system = FindActive(from);
        var connectedRecently = false;
        if (system is null)
            system = Assign(from, ConnectMode.UnverifiedSender, reply.MtuSize, reply.ServerGuid, nowUs, out connectedRecently);
        if (connectedRecently)
        {
            // RakPeer.cpp:4958 skips the request and the failure alike; the attempt is dropped without an event.
        }
        else if (system is null)
        {
            _events.Enqueue(new ConnectionAttemptFailed(from, reply.ServerGuid, new[] { (byte)MessageId.ConnectionAttemptFailed }));
        }
        else
        {
            system.WeInitiatedTheConnection = true;
            system.Mode = ConnectMode.RequestedConnection;
            if (attempt.TimeoutMs != 0)
                system.Session.TimeoutMs = attempt.TimeoutMs;
            var request = new ConnectionRequest(Guid, (ulong)(nowUs / 1000), attempt.Password);
            SendImmediate(system, Encode(request), Reliability.Reliable, Priority.Immediate, 0, 0, nowUs);
        }
        _attempts.Remove(attempt);
    }

    // RakPeer.cpp:5075-5126: the server turned the attempt down.
    private void OnRejection(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        // Every rejection is ID, magic, GUID, except that ID_INCOMPATIBLE_PROTOCOL_VERSION has its version byte first.
        var guidOffset = (MessageId)data[0] == MessageId.IncompatibleProtocolVersion ? 2 + OfflineMagic.Length : 1 + OfflineMagic.Length;
        var guid = BinaryPrimitives.ReadUInt64BigEndian(data[guidOffset..]);
        var attempt = FindAttempt(from);
        if (attempt is null)
            return;
        _attempts.Remove(attempt);
        _events.Enqueue(new ConnectionAttemptFailed(from, guid, new[] { data[0] }));
    }

    // RakPeer.cpp:5127-5197: answer an MTU probe.
    private void OnOpenConnectionRequest1(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (data.Length <= 1 + OfflineMagic.Length)
            return;
        var remoteProtocol = data[1 + OfflineMagic.Length];
        if (remoteProtocol != RakNetConstants.ProtocolVersion)
        {
            SendOffline(from, new IncompatibleProtocolVersion(RakNetConstants.ProtocolVersion, Guid));
            return;
        }
        // Lower the MTU if it exceeds our own limit (RakPeer.cpp:5182-5186).
        var mtu = Math.Min(data.Length + RakNetConstants.UdpHeaderSize, RakNetConstants.MaximumMtuSize);
        SendOffline(from, new OpenConnectionReply1(Guid, (ushort)mtu));
    }

    // RakPeer.cpp:5198-5418: accept or refuse the connection.
    private void OnOpenConnectionRequest2(IPEndPoint from, ReadOnlySpan<byte> data, long nowUs)
    {
        if (!TryDecode<OpenConnectionRequest2>(data, out var request))
            return;

        var fromAddress = FindActive(from);
        var fromGuid = FindActive(request.ClientGuid);
        var addressInUse = fromAddress is not null;
        var guidInUse = fromGuid is not null;
        var reply = new OpenConnectionReply2(Guid, from, request.MtuSize);

        if (addressInUse && guidInUse)
        {
            if (ReferenceEquals(fromAddress, fromGuid) && fromAddress!.Mode == ConnectMode.UnverifiedSender)
            {
                // A duplicate request from packet loss: send the same answer (outcome 1).
                SendOffline(from, reply);
                return;
            }
            SendOffline(from, new AlreadyConnected(Guid)); // Outcome 2.
            return;
        }
        if (addressInUse || guidInUse)
        {
            // Someone else took this GUID (outcome 3), or the client restarted without disconnecting (outcome 4).
            SendOffline(from, new AlreadyConnected(Guid));
            return;
        }

        if (!AllowIncomingConnections())
        {
            SendOffline(from, new NoFreeIncomingConnections(Guid));
            return;
        }

        Assign(from, ConnectMode.UnverifiedSender, request.MtuSize, request.ClientGuid, nowUs, out var connectedRecently);
        if (connectedRecently)
        {
            SendOffline(from, new IpRecentlyConnected(Guid)); // RakPeer.cpp:5366-5384.
            return;
        }
        // The answer goes out even when no slot is free (RakPeer.cpp:5366-5417).
        SendOffline(from, reply);
    }

    // AllowIncomingConnections and GetNumberOfRemoteInitiatedConnections, RakPeer.cpp:4038-4041, 3573-3592.
    private bool AllowIncomingConnections() =>
        _active.Count(s => s.Mode == ConnectMode.Connected && !s.WeInitiatedTheConnection) < MaxIncomingConnections;

    // AssignSystemAddressToRemoteSystemList, RakPeer.cpp:3595-3770. Returns null when no slot is free, or when the
    // frequency limit refuses the address, which sets connectedRecently.
    private RemoteSystem? Assign(IPEndPoint address, ConnectMode mode, int incomingMtu, ulong guid, long nowUs, out bool connectedRecently)
    {
        var nowMs = nowUs / 1000;
        connectedRecently = LimitIpConnectionFrequency
            && !IsOwnAddress(address.Address)
            && _active.Exists(s => s.Address.Address.Equals(address.Address)
                && nowMs >= s.ConnectionTimeMs
                && nowMs - s.ConnectionTimeMs < ConnectionFrequencyWindowMs);
        if (connectedRecently)
            return null;

        var index = Array.FindIndex(_slots, s => s is null || !s.IsActive);
        if (index < 0)
            return null;
        // MTUSize starts at defaultMTUSize and is raised to the incoming MTU (RakPeer.cpp:3638-3643). The C++ asserts
        // the result is at most MAXIMUM_MTU_SIZE (RakPeer.cpp:3644); larger values are clamped here.
        var mtu = Math.Clamp(incomingMtu, DefaultMtu, RakNetConstants.MaximumMtuSize);
        // RakPeer.cpp:3645-3648.
        var session = new ReliabilityLayer(mtu, nowUs, _options.SessionLimits)
        {
            TimeoutMs = _defaultTimeoutMs,
            UnreliableTimeoutMs = _unreliableTimeoutMs,
            SplitMessageProgressInterval = _splitMessageProgressInterval,
        };
        var system = new RemoteSystem(index, address, guid, session, mode, nowMs) { Mtu = mtu };
        _slots[index] = system;
        _active.Add(system);
        SlotsVersion++;
        return system;
    }

    // IsLoopbackAddress(address, false), RakPeer.cpp:4006-4027: one of ipList, or the address the first remote system
    // saw, ignoring ports.
    private bool IsOwnAddress(IPAddress address)
    {
        foreach (var local in _options.LocalAddresses)
        {
            if (local.Equals(InternalAddressList.Unassigned))
                break;
            if (local.Address.Equals(address))
                return true;
        }
        return _firstExternalAddress.Address.Equals(address);
    }

    // RakPeer.cpp:6035-6042 and 6175-6182: set once, by the first connection to complete.
    private void SetFirstExternalAddress(IPEndPoint address)
    {
        if (_firstExternalAddress.Equals(InternalAddressList.Unassigned))
            _firstExternalAddress = address;
    }

    // CloseConnectionInternal without notification (RakPeer.cpp:4148-4174): the slot keeps the address but forgets
    // the GUID.
    private void Deactivate(RemoteSystem system)
    {
        if (!system.IsActive)
            return;
        system.IsActive = false;
        system.Guid = RakNetConstants.UnassignedGuid;
        // RemoveFromActiveSystemList, RakPeer.cpp:3945-3958: the last entry fills the hole.
        var index = _active.IndexOf(system);
        _active[index] = _active[^1];
        _active.RemoveAt(_active.Count - 1);
        SlotsVersion++;
    }

    private static ConnectionState StateOf(RemoteSystem? system) =>
        system switch
        {
            null => ConnectionState.IsNotConnected,
            { IsActive: false } => ConnectionState.IsDisconnected,
            _ => system.Mode switch
            {
                ConnectMode.DisconnectAsap or ConnectMode.DisconnectOnNoAck => ConnectionState.IsDisconnecting,
                ConnectMode.DisconnectAsapSilently => ConnectionState.IsSilentlyDisconnecting,
                ConnectMode.RequestedConnection or ConnectMode.HandlingConnectionRequest or ConnectMode.UnverifiedSender =>
                    ConnectionState.IsConnecting,
                ConnectMode.Connected => ConnectionState.IsConnected,
                _ => ConnectionState.IsNotConnected,
            },
        };

    private RemoteSystem? FindActive(IPEndPoint address) => _active.Find(s => s.Address.Equals(address));

    // GetRemoteSystemFromSystemAddress(address, false, false), RakPeer.cpp:3407-3424: an active slot, else the first
    // slot the address used before.
    private RemoteSystem? FindForQuery(IPEndPoint address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return FindActive(address) ?? Array.Find(_slots, s => s is not null && s.Address.Equals(address));
    }

    private RemoteSystem? FindActive(ulong guid) =>
        guid == RakNetConstants.UnassignedGuid ? null : _active.Find(s => s.Guid == guid);

    private ConnectionAttempt? FindAttempt(IPEndPoint address) => _attempts.Find(a => a.Address.Equals(address));

    private void SendOffline<T>(IPEndPoint to, T message, int capacity = 512)
        where T : IWireMessage<T> =>
        _datagrams.Enqueue(new OutgoingDatagram(to, Encode(message, capacity)));

    private static byte[] Encode<T>(T message, int capacity = 512)
        where T : IWireMessage<T>
    {
        var writer = new BitStreamWriter(new byte[capacity]);
        message.Write(ref writer);
        return writer.Written.ToArray();
    }

    private static bool TryDecode<T>(ReadOnlySpan<byte> data, [MaybeNullWhen(false)] out T value)
        where T : IWireMessage<T>
    {
        var reader = new BitStreamReader(data);
        return T.TryRead(ref reader, out value);
    }

    /// <summary>
    /// The state of a slot the layer above already holds, without a lookup; <see cref="ConnectionState.IsDisconnected"/>
    /// once the slot is deactivated.
    /// </summary>
    public static ConnectionState GetConnectionState(RemoteSystem system) => StateOf(system);

    /// <summary>The slot of the active system with <paramref name="guid"/>, read by the layer above for its queries.</summary>
    public bool TryGetRemoteSystem(ulong guid, [NotNullWhen(true)] out RemoteSystem? system)
    {
        system = FindActive(guid);
        return system is not null;
    }
}
