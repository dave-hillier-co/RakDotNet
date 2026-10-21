using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// UDPProxyServer: lets a <see cref="UdpProxyCoordinator"/> it has logged into start forwarding on its
/// <see cref="UdpForwarder"/>, for <see cref="UdpProxyClient"/>s that cannot reach each other.
/// </summary>
/// <remarks>
/// <para>
/// Ported from UDPProxyServer.cpp. <see cref="LoginToCoordinator"/> sends ID_UDP_PROXY_LOGIN_REQUEST_FROM_SERVER_TO_COORDINATOR
/// and the password; the coordinator's answer goes to <see cref="ResultHandler"/>. A forwarding request, the source
/// and target addresses and a uint32 timeout, starts forwarding for the pair, and the reply carries both addresses,
/// <see cref="ServerPublicIp"/>, the <see cref="UdpForwarderResult"/> as a byte and the forwarding port. The forwarder
/// starts when the plugin is attached to a running peer and stops when it is detached or the peer shuts down. Every
/// message is MEDIUM_PRIORITY and RELIABLE_ORDERED on channel 0.
/// </para>
/// <para>
/// RakNet's quirk is kept: a forwarding request is served whoever sends it, since its check for a logged-in
/// coordinator compares an insertion index with -1 (UDPProxyServer.cpp:93-100).
/// </para>
/// <para>Members may be called from any thread; hooks and the result handler run on the dispatch step.</para>
/// </remarks>
public sealed class UdpProxyServer : IRakPlugin
{
    private readonly Lock _gate = new();
    private readonly PeerThreading _forwarderThreading;
    private readonly HashSet<SystemAddress> _loggingInCoordinators = [];
    private readonly HashSet<SystemAddress> _loggedInCoordinators = [];
    private IPluginHost? _host;
    private UdpForwarder? _forwarder;
    private int _maxForwardEntries = UdpForwarder.DefaultMaxForwardEntries;
    private string _serverPublicIp = "";

    /// <param name="forwarderThreading">
    /// Whether the <see cref="Forwarder"/> runs on a thread of its own, as RakNet's does, or on the dispatch step of the
    /// peer the plugin is attached to.
    /// </param>
    public UdpProxyServer(PeerThreading forwarderThreading = PeerThreading.Background) => _forwarderThreading = forwarderThreading;

    /// <summary>SetResultHandler: receives the outcome of <see cref="LoginToCoordinator"/>.</summary>
    public IUdpProxyServerResultHandler? ResultHandler { get; set; }

    /// <summary>
    /// SetServerPublicIP: the address clients are told to send to, instead of the one the coordinator sees this server
    /// at; needed when the two share a LAN. Empty, the default, uses the coordinator's.
    /// </summary>
    public string ServerPublicIp
    {
        get
        {
            lock (_gate)
                return _serverPublicIp;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
                _serverPublicIp = value;
        }
    }

    /// <summary>
    /// The forwarder, bound through the peer's transport factory; null while the plugin is not attached to a running
    /// peer.
    /// </summary>
    public UdpForwarder? Forwarder
    {
        get
        {
            lock (_gate)
                return _forwarder;
        }
    }

    /// <summary>
    /// <see cref="UdpForwarder.MaxForwardEntries"/> of the forwarder, kept for one created later; RakNet's 64 by default.
    /// </summary>
    public int MaxForwardEntries
    {
        get
        {
            lock (_gate)
                return _maxForwardEntries;
        }
        set
        {
            lock (_gate)
            {
                if (_forwarder is not null)
                    _forwarder.MaxForwardEntries = value;
                else
                    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
                _maxForwardEntries = value;
            }
        }
    }

    private RakPeer Peer => _host?.Peer ?? throw new InvalidOperationException("The UdpProxyServer is not attached to a RakPeer.");

    /// <summary>
    /// LoginToCoordinator: asks <paramref name="coordinatorAddress"/>, which this system must be connected to (or be),
    /// to list this server. False, sending nothing, when a login there is in progress or done.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public bool LoginToCoordinator(string password, SystemAddress coordinatorAddress)
    {
        ArgumentNullException.ThrowIfNull(password);
        var peer = Peer;
        lock (_gate)
        {
            if (_loggingInCoordinators.Contains(coordinatorAddress) || _loggedInCoordinators.Contains(coordinatorAddress))
                return false;
            using var stream = UdpProxyWire.Begin(UdpProxyMessage.LoginRequestFromServerToCoordinator);
            UdpProxyWire.WriteRakString(stream, UdpProxyWire.ToRakString(password));
            UdpProxyWire.Send(peer, stream, coordinatorAddress);
            _loggingInCoordinators.Add(coordinatorAddress);
            return true;
        }
    }

    /// <summary>OnAttach: starts the forwarder when the peer is running.</summary>
    void IRakPlugin.OnAttached(IPluginHost host)
    {
        _host = host;
        if (host.Peer is { IsActive: true })
            StartForwarder(host);
    }

    /// <summary>OnDetach: as OnRakPeerShutdown.</summary>
    void IRakPlugin.OnDetached()
    {
        Stop();
        _host = null;
    }

    void IRakPlugin.OnStartup()
    {
        if (_host is { Peer: not null } host)
            StartForwarder(host);
    }

    /// <summary>OnRakPeerShutdown: stops the forwarder and forgets the coordinators.</summary>
    void IRakPlugin.OnShutdown() => Stop();

    void IRakPlugin.OnUpdate()
    {
        UdpForwarder? forwarder;
        lock (_gate)
            forwarder = _forwarder;
        if (_forwarderThreading == PeerThreading.Manual)
            forwarder?.Update();
    }

    /// <summary>OnClosedConnection: a coordinator that disconnects is forgotten.</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
        {
            _loggingInCoordinators.Remove(connection.Address);
            _loggedInCoordinators.Remove(connection.Address);
        }
    }

    /// <summary>OnReceive (UDPProxyServer.cpp:86-142).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.Length <= 1 || data[0] != (byte)MessageId.UdpProxyGeneral || _host?.Peer is not { } peer)
            return PluginResult.Continue;
        switch ((UdpProxyMessage)data[1])
        {
            case UdpProxyMessage.ForwardingRequestFromCoordinatorToServer:
                OnForwardingRequestFromCoordinatorToServer(peer, e.GetReader(), e.Address);
                return PluginResult.Consume;
            case UdpProxyMessage.NoPasswordSetFromCoordinatorToServer:
            case UdpProxyMessage.WrongPasswordFromCoordinatorToServer:
            case UdpProxyMessage.AlreadyLoggedInFromCoordinatorToServer:
            case UdpProxyMessage.LoginSuccessFromCoordinatorToServer:
                OnLoginResult((UdpProxyMessage)data[1], e.GetReader(), e.Address);
                return PluginResult.Consume;
            default:
                return PluginResult.Continue;
        }
    }

    private void OnLoginResult(UdpProxyMessage message, BitStreamReader reader, SystemAddress coordinator)
    {
        lock (_gate)
        {
            if (!_loggingInCoordinators.Remove(coordinator))
                return;
            if (message == UdpProxyMessage.LoginSuccessFromCoordinatorToServer)
                _loggedInCoordinators.Add(coordinator);
        }
        reader.IgnoreBytes(2);
        var password = reader.TryReadRakStringBytes(out var bytes) ? UdpProxyWire.FromRakString(bytes) : "";
        var handler = ResultHandler;
        switch (message)
        {
            case UdpProxyMessage.NoPasswordSetFromCoordinatorToServer:
                handler?.OnNoPasswordSet(password, this);
                break;
            case UdpProxyMessage.WrongPasswordFromCoordinatorToServer:
                handler?.OnWrongPassword(password, this);
                break;
            case UdpProxyMessage.AlreadyLoggedInFromCoordinatorToServer:
                handler?.OnAlreadyLoggedIn(password, this);
                break;
            case UdpProxyMessage.LoginSuccessFromCoordinatorToServer:
                handler?.OnLoginSuccess(password, this);
                break;
        }
    }

    // OnForwardingRequestFromCoordinatorToServer (UDPProxyServer.cpp:157-178).
    private void OnForwardingRequestFromCoordinatorToServer(RakPeer peer, BitStreamReader reader, SystemAddress coordinator)
    {
        reader.IgnoreBytes(2);
        reader.TryReadSystemAddress(out var sourceAddress);
        reader.TryReadSystemAddress(out var targetAddress);
        reader.TryReadUInt32(out var timeoutOnNoDataMs);
        UdpForwarder? forwarder;
        string serverPublicIp;
        lock (_gate)
        {
            forwarder = _forwarder;
            serverPublicIp = _serverPublicIp;
        }
        var forwarding = forwarder?.StartForwarding(sourceAddress, targetAddress, TimeSpan.FromMilliseconds(timeoutOnNoDataMs))
                         ?? new UdpForwarding(UdpForwarderResult.NotRunning, 0, null);

        using var stream = UdpProxyWire.Begin(UdpProxyMessage.ForwardingReplyFromServerToCoordinator);
        stream.WriteAddress(sourceAddress);
        stream.WriteAddress(targetAddress);
        UdpProxyWire.WriteRakString(stream, UdpProxyWire.ToRakString(serverPublicIp));
        stream.WriteByte((byte)forwarding.Result);
        stream.WriteUInt16(forwarding.ForwardingPort);
        UdpProxyWire.Send(peer, stream, coordinator);
    }

    private void StartForwarder(IPluginHost host)
    {
        lock (_gate)
        {
            _forwarder ??= new UdpForwarder(host.Peer!.LowLevel.TransportFactory, host.TimeProvider, _forwarderThreading)
            {
                MaxForwardEntries = _maxForwardEntries,
            };
            _forwarder.Startup();
        }
    }

    private void Stop()
    {
        UdpForwarder? forwarder;
        lock (_gate)
        {
            forwarder = _forwarder;
            _forwarder = null;
            _loggingInCoordinators.Clear();
            _loggedInCoordinators.Clear();
        }
        forwarder?.Dispose();
    }
}
