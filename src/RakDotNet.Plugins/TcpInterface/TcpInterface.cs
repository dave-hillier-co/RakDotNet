using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's TCPInterface: a TCP server and client with a fixed number of connection slots, polled for connection events
/// and data. Telnet, HTTP and the packetized framing of <see cref="PacketizedTcp"/> are built on it.
/// </summary>
/// <remarks>
/// <para>
/// Every member is thread-safe. The I/O runs on tasks of its own, as RakNet's update and connection threads do:
/// accepted and connected sockets are read as data arrives, each receive becoming one <see cref="TcpPacket"/>, and
/// <see cref="Send(ReadOnlySpan{byte}, SystemAddress, bool)"/> appends to the connection's outgoing <see cref="ByteQueue"/>, which those tasks drain. Events are
/// queued until the application polls for them, and plugins run inside the polling call.
/// </para>
/// <para>
/// Slots follow RakNet's remoteClients: a slot is taken when a connection is accepted or an outgoing attempt starts,
/// and freed when the connection is lost or closed. A slot taken by a connection attempt counts as a connection, with
/// the address it last had, until the attempt completes (TCPInterface.cpp:342-352). Addresses are IPv4, as RakNet's
/// default build has no IPv6 (RAKNET_SUPPORT_IPV6 is 0).
/// </para>
/// <para>
/// TLS (OPEN_SSL_CLIENT_SUPPORT in RakNet) is <see cref="StartSslClient"/> over <see cref="SslStream"/>. RakNet's
/// OpenSSL context verifies nothing; here the server certificate is validated by .NET unless
/// <see cref="RemoteCertificateValidation"/> decides otherwise, and a failed handshake closes the connection as lost
/// rather than carrying on in the clear as RakNet does (TCPInterface.cpp:1312-1391).
/// </para>
/// </remarks>
public class TcpInterface : IPluginHost, IDisposable
{
    /// <summary>The most a single receive returns. RakNet reads up to 1 MiB at a time (TCPInterface.cpp:946).</summary>
    public const int ReceiveSize = 64 * 1024;

    private const int SendChunkSize = 64 * 1024;

    // SocketLayer::SetSocketOptions (SocketLayer.cpp:119-136) and SocketConnect (TCPInterface.cpp:830-831).
    private const int SocketReceiveBufferSize = 256 * 1024;
    private const int ListenSendBufferSize = 16 * 1024;

    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<TcpPacket> _incoming = new();
    private readonly ConcurrentQueue<TcpPacket> _headPush = new();
    private readonly ConcurrentQueue<TcpPacket> _tailPush = new();
    private readonly ConcurrentQueue<SystemAddress> _newIncomingConnections = new();
    private readonly ConcurrentQueue<SystemAddress> _lostConnections = new();
    private readonly ConcurrentQueue<SystemAddress> _completedConnectionAttempts = new();
    private readonly ConcurrentQueue<SystemAddress> _failedConnectionAttempts = new();
    private readonly List<SystemAddress> _activeSslConnections = [];
    private readonly List<Socket> _connectingSockets = [];
    private readonly List<Socket> _unservicedSockets = [];
    private readonly long _clockOrigin;
    private volatile ITcpPlugin[] _plugins = [];
    private RemoteClient[] _remoteClients = [];
    private Socket? _listenSocket;
    private CancellationTokenSource? _stopping;
    private volatile bool _isStarted;

    public TcpInterface(TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
        _clockOrigin = TimeProvider.GetTimestamp();
    }

    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Validates the server certificate of a <see cref="StartSslClient"/> connection; null applies .NET's default
    /// validation.
    /// </summary>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; set; }

    /// <summary>WasStarted: between a successful <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool WasStarted => _isStarted;

    /// <summary>
    /// Where the server listens, or null when it does not. RakNet has no counterpart; its Start succeeds even when the
    /// listening socket cannot be bound (TCPInterface.cpp:216-223), and this is how to tell.
    /// </summary>
    public IPEndPoint? ListenEndPoint
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _listenSocket?.LocalEndPoint as IPEndPoint;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>GetConnectionCount: the slots in use (TCPInterface.cpp:750-759).</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
                return _remoteClients.Count(c => c.IsActive);
        }
    }

    /// <summary>
    /// Start: takes <paramref name="maxConnections"/> slots (<paramref name="maxIncomingConnections"/> when 0, and at
    /// least 1) and, with incoming connections allowed, listens on <paramref name="port"/> of
    /// <paramref name="bindAddress"/> (every address when null) with that backlog. False only when already started
    /// (TCPInterface.cpp:181-248).
    /// </summary>
    public bool Start(ushort port, ushort maxIncomingConnections, ushort maxConnections = 0, string? bindAddress = null)
    {
        lock (_gate)
        {
            if (_isStarted)
                return false;
            if (maxConnections == 0)
                maxConnections = maxIncomingConnections;
            if (maxConnections == 0)
                maxConnections = 1;
            _remoteClients = [.. Enumerable.Range(0, maxConnections).Select(_ => new RemoteClient())];
            _stopping = new CancellationTokenSource();
            _listenSocket = maxIncomingConnections > 0 ? CreateListenSocket(port, maxIncomingConnections, bindAddress) : null;
            _isStarted = true;
            if (_listenSocket is { } listener)
                _ = AcceptLoopAsync(listener, _stopping.Token);
        }
        foreach (var plugin in _plugins)
            plugin.OnStartup();
        return true;
    }

    /// <summary>
    /// Stop: closes every connection and the listener and discards everything queued, without reporting the
    /// connections as lost (TCPInterface.cpp:249-335). Plugins get <see cref="ITcpPlugin.OnShutdown"/> even when not
    /// started.
    /// </summary>
    public void Stop()
    {
        foreach (var plugin in _plugins)
            plugin.OnShutdown();

        lock (_gate)
        {
            if (!_isStarted)
                return;
            _isStarted = false;
            _stopping?.Cancel();
            _stopping?.Dispose();
            _stopping = null;
            _listenSocket?.Dispose();
            _listenSocket = null;
            foreach (var socket in _connectingSockets)
                socket.Dispose();
            _connectingSockets.Clear();
            foreach (var socket in _unservicedSockets)
                socket.Dispose();
            _unservicedSockets.Clear();
            foreach (var client in _remoteClients)
            {
                client.SetActive(false);
                client.Dispose();
            }
            _remoteClients = [];
            _incoming.Clear();
            _headPush.Clear();
            _tailPush.Clear();
            _newIncomingConnections.Clear();
            _lostConnections.Clear();
            _completedConnectionAttempts.Clear();
            _failedConnectionAttempts.Clear();
            _activeSslConnections.Clear();
        }
        OnStopped();
    }

    /// <summary>
    /// Connect: takes a free slot and connects to <paramref name="host"/>. Blocking, it returns the server's address,
    /// or <see cref="SystemAddress.Unassigned"/> on failure; otherwise it returns Unassigned at once. Either way the
    /// outcome is also queued for <see cref="HasCompletedConnectionAttempt"/> or
    /// <see cref="HasFailedConnectionAttempt"/> (TCPInterface.cpp:336-418). Unassigned with nothing queued when not
    /// started or every slot is taken.
    /// </summary>
    /// <remarks>RakNet's bindAddress argument is written into the destination and then overwritten, so it is not taken.</remarks>
    public SystemAddress Connect(string host, ushort remotePort, bool block = true)
    {
        ArgumentNullException.ThrowIfNull(host);
        RemoteClient client;
        int generation;
        CancellationToken stopping;
        lock (_gate)
        {
            if (!_isStarted)
                return SystemAddress.Unassigned;
            client = Array.Find(_remoteClients, c => !c.IsActive)!;
            if (client is null)
                return SystemAddress.Unassigned;
            client.SetActive(true);
            generation = client.Generation;
            stopping = _stopping!.Token;
        }

        // SystemAddress::FromString resolves the host on the caller's thread for both kinds of attempt.
        var resolved = Resolve(host);
        var address = resolved is null ? SystemAddress.Unassigned : new SystemAddress(resolved, remotePort);
        if (block)
        {
            var socket = resolved is null ? null : ConnectBlocking(address);
            return Connected(client, generation, socket, address) ? address : SystemAddress.Unassigned;
        }
        _ = ConnectAsync(client, generation, address, resolved is not null, stopping);
        return SystemAddress.Unassigned;
    }

    /// <summary>
    /// StartSSLClient: the connection to <paramref name="address"/> continues over TLS, the handshake running before its
    /// next receive or send (TCPInterface.cpp:420-439, 973-999). <paramref name="targetHost"/> is the name the
    /// certificate is checked against; the address when null.
    /// </summary>
    public void StartSslClient(SystemAddress address, string? targetHost = null)
    {
        lock (_gate)
        {
            if (!_activeSslConnections.Contains(address))
                _activeSslConnections.Add(address);
            var client = Array.Find(_remoteClients, c => c.IsActive && c.Address == address);
            client?.RequestSsl(targetHost ?? address.ToString(false));
        }
    }

    /// <summary>IsSSLActive: whether <see cref="StartSslClient"/> was called for the address since it was last closed.</summary>
    public bool IsSslActive(SystemAddress address)
    {
        lock (_gate)
            return _activeSslConnections.Contains(address);
    }

    /// <summary>Send: <see cref="SendList"/> of one part (TCPInterface.cpp:445-448).</summary>
    public virtual void Send(ReadOnlySpan<byte> data, SystemAddress address, bool broadcast) =>
        SendList([data.ToArray()], address, broadcast);

    /// <summary>
    /// SendList: queues the parts, joined, to the connection at <paramref name="address"/>, or with
    /// <paramref name="broadcast"/> to every connection but that one. False when not started, when there is no target,
    /// or when the parts hold no bytes (TCPInterface.cpp:449-500).
    /// </summary>
    public virtual bool SendList(ReadOnlySpan<ReadOnlyMemory<byte>> parts, SystemAddress address, bool broadcast)
    {
        if (!_isStarted)
            return false;
        if (address == SystemAddress.Unassigned && !broadcast)
            return false;
        long total = 0;
        foreach (var part in parts)
            total += part.Length;
        if (total == 0)
            return false;

        lock (_gate)
        {
            foreach (var client in _remoteClients)
            {
                if (broadcast ? client.Address != address : client.Address == address)
                    client.SendOrBuffer(parts);
            }
        }
        return true;
    }

    /// <summary>GetOutgoingDataBufferSize: bytes queued to <paramref name="address"/> and not yet sent (TCPInterface.cpp:761-784).</summary>
    public int GetOutgoingDataBufferSize(SystemAddress address)
    {
        lock (_gate)
        {
            var total = 0;
            foreach (var client in _remoteClients)
            {
                if (client.IsActive && client.Address == address)
                    total += client.OutgoingBytes;
            }
            return total;
        }
    }

    /// <summary>ReceiveHasPackets: whether <see cref="Receive"/> has something to return (TCPInterface.cpp:501-504).</summary>
    public virtual bool ReceiveHasPackets() => !_headPush.IsEmpty || !_incoming.IsEmpty || !_tailPush.IsEmpty;

    /// <summary>
    /// Receive: runs every plugin's <see cref="ITcpPlugin.OnUpdate"/>, then takes the next packet pushed back at the
    /// head, received, or pushed back at the tail, in that order, and offers it to the plugins. Null when there is none
    /// or a plugin consumed it (TCPInterface.cpp:505-548).
    /// </summary>
    public virtual TcpPacket? Receive()
    {
        foreach (var plugin in _plugins)
            plugin.OnUpdate();
        var packet = ReceiveInt();
        return packet is null ? null : OfferToPlugins(packet);
    }

    /// <summary>
    /// CloseConnection: tells the plugins the connection was closed by the user, then frees its slot and closes the
    /// socket; the other side sees the connection lost (TCPInterface.cpp:576-614).
    /// </summary>
    public virtual void CloseConnection(SystemAddress address)
    {
        if (!_isStarted || address == SystemAddress.Unassigned)
            return;
        foreach (var plugin in _plugins)
            plugin.OnDisconnected(address, TcpDisconnectReason.ClosedByUser);
        lock (_gate)
        {
            Array.Find(_remoteClients, c => c.IsActive && c.Address == address)?.SetActive(false);
            _activeSslConnections.Remove(address);
        }
    }

    /// <summary>GetConnectionList: the address of every slot in use, in slot order (TCPInterface.cpp:735-749).</summary>
    public SystemAddress[] GetConnectionList()
    {
        lock (_gate)
            return [.. _remoteClients.Where(c => c.IsActive).Select(c => c.Address)];
    }

    /// <summary>HasCompletedConnectionAttempt: the next connected server, or Unassigned; tells plugins (TCPInterface.cpp:654-670).</summary>
    public virtual SystemAddress HasCompletedConnectionAttempt()
    {
        if (!_completedConnectionAttempts.TryDequeue(out var address))
            return SystemAddress.Unassigned;
        foreach (var plugin in _plugins)
            plugin.OnConnected(address);
        return address;
    }

    /// <summary>HasFailedConnectionAttempt: the next server that could not be reached, or Unassigned; tells plugins (TCPInterface.cpp:671-694).</summary>
    public virtual SystemAddress HasFailedConnectionAttempt()
    {
        if (!_failedConnectionAttempts.TryDequeue(out var address))
            return SystemAddress.Unassigned;
        foreach (var plugin in _plugins)
            plugin.OnConnectFailed(address);
        return address;
    }

    /// <summary>HasNewIncomingConnection: the next accepted client, or Unassigned; tells plugins (TCPInterface.cpp:695-714).</summary>
    public virtual SystemAddress HasNewIncomingConnection()
    {
        if (!_newIncomingConnections.TryDequeue(out var address))
            return SystemAddress.Unassigned;
        foreach (var plugin in _plugins)
            plugin.OnConnected(address);
        return address;
    }

    /// <summary>
    /// HasLostConnection: the next connection the other side closed or that failed, or Unassigned; tells plugins
    /// (TCPInterface.cpp:715-734).
    /// </summary>
    public virtual SystemAddress HasLostConnection()
    {
        if (!_lostConnections.TryDequeue(out var address))
            return SystemAddress.Unassigned;
        foreach (var plugin in _plugins)
            plugin.OnDisconnected(address, TcpDisconnectReason.DisconnectionNotification);
        return address;
    }

    /// <summary>PushBackPacket: queues a packet for <see cref="Receive"/>, ahead of received ones or after them (TCPInterface.cpp:643-649).</summary>
    public virtual void PushBackPacket(TcpPacket packet, bool pushAtHead)
    {
        ArgumentNullException.ThrowIfNull(packet);
        (pushAtHead ? _headPush : _tailPush).Enqueue(packet);
    }

    /// <summary>AttachPlugin: once per plugin (TCPInterface.cpp:551-559).</summary>
    public void AttachPlugin(ITcpPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_gate)
        {
            if (Array.IndexOf(_plugins, plugin) >= 0)
                return;
            _plugins = [.. _plugins, plugin];
        }
        plugin.OnAttached(this);
    }

    /// <summary>
    /// DetachPlugin. RakNet moves the last plugin into the freed place (TCPInterface.cpp:560-575), and so the call
    /// order changes the same way.
    /// </summary>
    public void DetachPlugin(ITcpPlugin plugin)
    {
        if (plugin is null)
            return;
        lock (_gate)
        {
            var index = Array.IndexOf(_plugins, plugin);
            if (index < 0)
                return;
            var plugins = _plugins.ToArray();
            plugins[index] = plugins[^1];
            _plugins = plugins[..^1];
        }
        plugin.OnDetached();
    }

    /// <summary><see cref="Stop"/>.</summary>
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>After <see cref="Stop"/> has stopped a started interface: where a derived class discards its own state.</summary>
    protected virtual void OnStopped()
    {
    }

    /// <summary>The attached plugins, in call order.</summary>
    protected ReadOnlySpan<ITcpPlugin> AttachedPlugins => _plugins;

    /// <summary>ReceiveInt: the next packet, before plugins see it (TCPInterface.cpp:536-548).</summary>
    protected TcpPacket? ReceiveInt()
    {
        if (!_isStarted)
            return null;
        if (_headPush.TryDequeue(out var packet) || _incoming.TryDequeue(out packet) || _tailPush.TryDequeue(out packet))
            return packet;
        return null;
    }

    /// <summary>OnReceive on every plugin in order; null when one consumes the packet.</summary>
    protected TcpPacket? OfferToPlugins(TcpPacket packet)
    {
        foreach (var plugin in _plugins)
        {
            if (plugin.OnReceive(packet) == PluginResult.Consume)
                return null;
        }
        return packet;
    }

    // IPluginHost: PluginInterface2's *Unified helpers with a TCPInterface (PluginInterface2.cpp:37-42, 72-76, 105-108,
    // 128-132, 163-166, 216-221).

    /// <summary>GetMyGUIDUnified: a TCP host has no GUID.</summary>
    public RakNetGuid RakNetGuid => RakNetGuid.Unassigned;

    /// <summary>Milliseconds since this interface was created, on <see cref="TimeProvider"/>.</summary>
    public ulong GetTime() => (ulong)TimeProvider.GetElapsedTime(_clockOrigin).TotalMilliseconds;

    /// <summary>Null: TCP has no <see cref="RakPeer"/>.</summary>
    public RakPeer? Peer => null;

    /// <summary>SendUnified: the bytes go to the target's address; reliability, priority, channel and receipt do not apply.</summary>
    public SendResult Send(OutgoingMessage message, AddressOrGuid target, Reliability reliability, Priority priority = Priority.High,
                           byte orderingChannel = 0, SendReceipt forceReceipt = default) =>
        SendUnified(message, target.Address, broadcast: false);

    /// <summary>SendUnified with broadcast: every connection but <paramref name="except"/>'s address.</summary>
    public SendResult Broadcast(OutgoingMessage message, Reliability reliability, Priority priority = Priority.High, byte orderingChannel = 0,
                                AddressOrGuid except = default, SendReceipt forceReceipt = default) =>
        SendUnified(message, except.Address, broadcast: true);

    /// <summary>PushBackPacketUnified: <see cref="PushBackPacket"/> of a copy; the GUID is not kept.</summary>
    public void Inject(ReadOnlySpan<byte> data, SystemAddress source, RakNetGuid sourceGuid, bool atHead = false, bool generatedLocally = false) =>
        PushBackPacket(new TcpPacket(source, data.ToArray()), atHead);

    /// <summary>PushBackPacketUnified of an event detached from a peer: its bytes and address are copied and it is disposed.</summary>
    public void Inject(OwnedPeerEvent owned, bool atHead = false)
    {
        using (owned)
        {
            var e = owned.Event;
            PushBackPacket(new TcpPacket(e.Address, e.ToArray()) { BitLength = e.BitLength }, atHead);
        }
    }

    private SendResult SendUnified(OutgoingMessage message, SystemAddress address, bool broadcast)
    {
        var bytes = new byte[message.ByteLength];
        message.CopyTo(bytes);
        if (!_isStarted)
            return new SendResult(SendStatus.NotActive, SendReceipt.None);
        return SendList([bytes], address, broadcast)
            ? new SendResult(SendStatus.Queued, SendReceipt.None)
            : new SendResult(SendStatus.NotConnected, SendReceipt.None);
    }

    private static Socket? CreateListenSocket(ushort port, ushort backlog, string? bindAddress)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = SocketReceiveBufferSize,
            SendBufferSize = ListenSendBufferSize,
        };
        try
        {
            // inet_addr of anything but a dotted address gives INADDR_NONE, which cannot be bound (TCPInterface.cpp:110-121).
            var address = string.IsNullOrEmpty(bindAddress) ? IPAddress.Any
                : IPAddress.TryParse(bindAddress, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork ? parsed : IPAddress.None;
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(backlog);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>SystemAddress::FromString's resolution: a dotted address, or the first IPv4 address of a host name.</summary>
    internal static IPAddress? Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var address))
            return address.AddressFamily == AddressFamily.InterNetwork ? address : null;
        try
        {
            return Array.Find(Dns.GetHostAddresses(host), a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private static Socket NewClientSocket() =>
        new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = SocketReceiveBufferSize };

    private Socket? ConnectBlocking(SystemAddress address)
    {
        var socket = NewClientSocket();
        lock (_gate)
            _connectingSockets.Add(socket);
        try
        {
            socket.Connect(address.ToIPEndPoint());
            return socket;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            socket.Dispose();
            return null;
        }
        finally
        {
            lock (_gate)
                _connectingSockets.Remove(socket);
        }
    }

    // ConnectionAttemptLoop (TCPInterface.cpp:889-935), as a task.
    private async Task ConnectAsync(RemoteClient client, int generation, SystemAddress address, bool resolved, CancellationToken stopping)
    {
        Socket? socket = null;
        if (resolved)
        {
            socket = NewClientSocket();
            lock (_gate)
                _connectingSockets.Add(socket);
            try
            {
                await socket.ConnectAsync(address.ToIPEndPoint(), stopping).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                socket.Dispose();
                socket = null;
            }
            finally
            {
                lock (_gate)
                    _connectingSockets.Remove(socket!);
            }
        }
        Connected(client, generation, socket, address);
    }

    // The end of an attempt: the slot gets the socket and the attempt is reported, or the slot is freed and the failure
    // reported. Nothing is reported for an attempt that Stop overtook.
    private bool Connected(RemoteClient client, int generation, Socket? socket, SystemAddress address)
    {
        lock (_gate)
        {
            if (client.Generation != generation || !_isStarted)
            {
                socket?.Dispose();
                return false;
            }
            if (socket is null)
            {
                client.SetActive(false);
                _failedConnectionAttempts.Enqueue(address);
                return false;
            }
            Install(client, socket, address);
            _completedConnectionAttempts.Enqueue(address);
            return true;
        }
    }

    // The accept part of UpdateTCPInterfaceLoop (TCPInterface.cpp:1062-1116).
    private async Task AcceptLoopAsync(Socket listener, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(stopping).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                if (stopping.IsCancellationRequested)
                {
                    socket.Dispose();
                    return;
                }
                SystemAddress address = (IPEndPoint)socket.RemoteEndPoint!;
                var client = Array.Find(_remoteClients, c => !c.IsActive);
                if (client is null)
                {
                    _unservicedSockets.Add(socket);
                    continue;
                }
                client.SetActive(true);
                Install(client, socket, address);
                _newIncomingConnections.Enqueue(address);
            }
        }
    }

    // Called with the gate held.
    private void Install(RemoteClient client, Socket socket, SystemAddress address)
    {
        client.Attach(socket, address);
        var generation = client.Generation;
        var stopping = _stopping!.Token;
        _ = ReceiveLoopAsync(client, generation, stopping);
        _ = SendLoopAsync(client, generation, stopping);
    }

    // The receive part of UpdateTCPInterfaceLoop (TCPInterface.cpp:1144-1212), including InitSSL before the next read.
    private async Task ReceiveLoopAsync(RemoteClient client, int generation, CancellationToken stopping)
    {
        await Task.Yield();
        var buffer = new byte[ReceiveSize];
        try
        {
            while (true)
            {
                Socket socket;
                SslStream? ssl;
                string? sslTargetHost;
                CancellationToken wake;
                lock (_gate)
                {
                    if (client.Generation != generation)
                        return;
                    socket = client.Socket!;
                    ssl = client.Ssl;
                    sslTargetHost = client.SslTargetHost;
                    wake = client.Wake;
                }

                if (sslTargetHost is not null && ssl is null)
                {
                    ssl = await AuthenticateAsync(client, generation, socket, sslTargetHost, stopping).ConfigureAwait(false);
                    if (ssl is null)
                        break;
                    continue;
                }

                int length;
                if (ssl is not null)
                {
                    length = await ssl.ReadAsync(buffer, stopping).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        // A zero-byte receive waits for data without taking any, so StartSslClient can wake it.
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, wake);
                        await socket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                    {
                        continue;
                    }
                    length = socket.Receive(buffer);
                }

                if (length <= 0)
                    break;
                lock (_gate)
                {
                    if (client.Generation != generation)
                        return;
                    _incoming.Enqueue(new TcpPacket(client.Address, buffer.AsSpan(0, length).ToArray()) { FromNetwork = true });
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException or OperationCanceledException or AuthenticationException)
        {
        }

        lock (_gate)
        {
            // Lost, gracefully or not (TCPInterface.cpp:1158-1165, 1203-1210); nothing when the slot was closed or stopped.
            if (client.Generation != generation || !_isStarted)
                return;
            _lostConnections.Enqueue(client.Address);
            client.SetActive(false);
        }
    }

    private async Task<SslStream?> AuthenticateAsync(RemoteClient client, int generation, Socket socket, string targetHost, CancellationToken stopping)
    {
        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                RemoteCertificateValidationCallback = RemoteCertificateValidation,
            }, stopping).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
                client.SslFailed(generation);
            return null;
        }
        lock (_gate)
        {
            if (client.Generation != generation)
            {
                ssl.Dispose();
                return null;
            }
            client.SslReady(ssl);
            return ssl;
        }
    }

    // The send part of UpdateTCPInterfaceLoop (TCPInterface.cpp:1213-1244).
    private async Task SendLoopAsync(RemoteClient client, int generation, CancellationToken stopping)
    {
        ChannelReader<bool> signal;
        lock (_gate)
        {
            if (client.Generation != generation)
                return;
            signal = client.SendSignal!.Reader;
        }
        var buffer = new byte[SendChunkSize];
        try
        {
            while (await signal.WaitToReadAsync(stopping).ConfigureAwait(false))
            {
                signal.TryRead(out _);
                while (true)
                {
                    int length;
                    Socket socket;
                    Task? sslReady;
                    lock (_gate)
                    {
                        if (client.Generation != generation)
                            return;
                        length = client.PeekOutgoing(buffer);
                        socket = client.Socket!;
                        sslReady = client.SslHandshake;
                    }
                    if (length == 0)
                        break;

                    if (sslReady is not null)
                    {
                        await sslReady.ConfigureAwait(false);
                        SslStream ssl;
                        lock (_gate)
                        {
                            if (client.Generation != generation)
                                return;
                            ssl = client.Ssl!;
                        }
                        await ssl.WriteAsync(buffer.AsMemory(0, length), stopping).ConfigureAwait(false);
                    }
                    else
                    {
                        var sent = 0;
                        while (sent < length)
                            sent += await socket.SendAsync(buffer.AsMemory(sent, length - sent), SocketFlags.None, stopping).ConfigureAwait(false);
                    }

                    lock (_gate)
                    {
                        if (client.Generation != generation)
                            return;
                        client.ConsumeOutgoing(length);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException or OperationCanceledException or AuthenticationException)
        {
            // RakNet ignores send errors; the receive side reports the connection lost.
        }
    }

    /// <summary>RakNet's RemoteClient: one connection slot. Its members are called with the interface's gate held.</summary>
    private sealed class RemoteClient : IDisposable
    {
        private readonly ByteQueue _outgoing = new();
        private readonly Lock _outgoingGate = new();
        private CancellationTokenSource _wake = new();
        private TaskCompletionSource? _sslHandshake;

        public bool IsActive { get; private set; }

        /// <summary>Changes whenever the slot is taken or freed, so the tasks of an earlier use leave it alone.</summary>
        public int Generation { get; private set; }

        public SystemAddress Address { get; private set; }

        public void Dispose() => _wake.Dispose();

        public Socket? Socket { get; private set; }

        public SslStream? Ssl { get; private set; }

        public string? SslTargetHost { get; private set; }

        public Task? SslHandshake => _sslHandshake?.Task;

        public Channel<bool>? SendSignal { get; private set; }

        public CancellationToken Wake => _wake.Token;

        public int OutgoingBytes
        {
            get
            {
                lock (_outgoingGate)
                    return _outgoing.BytesWritten;
            }
        }

        // RemoteClient::SetActive (TCPInterface.cpp:1266-1278): a change empties the outgoing data, and freeing the slot
        // closes its socket. The address is kept.
        public void SetActive(bool active)
        {
            if (IsActive == active)
                return;
            IsActive = active;
            Generation++;
            lock (_outgoingGate)
                _outgoing.Clear();
            if (active)
                return;
            SendSignal?.Writer.TryComplete();
            SendSignal = null;
            _sslHandshake?.TrySetCanceled();
            _sslHandshake = null;
            SslTargetHost = null;
            Ssl?.Dispose();
            Ssl = null;
            Socket?.Dispose();
            Socket = null;
            Rewake();
        }

        public void Attach(Socket socket, SystemAddress address)
        {
            Socket = socket;
            Address = address;
            SendSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
            if (OutgoingBytes > 0)
                SendSignal.Writer.TryWrite(true);
        }

        // RemoteClient::SendOrBuffer (TCPInterface.cpp:1279-1310), with ALLOW_SEND_FROM_USER_THREAD false.
        public void SendOrBuffer(ReadOnlySpan<ReadOnlyMemory<byte>> parts)
        {
            if (!IsActive)
                return;
            lock (_outgoingGate)
            {
                foreach (var part in parts)
                    _outgoing.WriteBytes(part.Span);
            }
            SendSignal?.Writer.TryWrite(true);
        }

        public int PeekOutgoing(Span<byte> destination)
        {
            lock (_outgoingGate)
                return _outgoing.ReadBytes(destination, peek: true) ? Math.Min(destination.Length, _outgoing.BytesWritten) : 0;
        }

        public void ConsumeOutgoing(int length)
        {
            lock (_outgoingGate)
                _outgoing.IncrementReadOffset(length);
        }

        public void RequestSsl(string targetHost)
        {
            if (SslTargetHost is not null)
                return;
            SslTargetHost = targetHost;
            _sslHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Rewake();
        }

        // Wakes a receive waiting with the current token. The old source is left to the collector rather than disposed,
        // because the receive may still be linking to its token.
        private void Rewake()
        {
            var wake = _wake;
            _wake = new CancellationTokenSource();
            wake.Cancel();
        }

        public void SslReady(SslStream ssl)
        {
            Ssl = ssl;
            _sslHandshake?.TrySetResult();
        }

        public void SslFailed(int generation)
        {
            if (generation == Generation)
                _sslHandshake?.TrySetCanceled();
        }
    }
}
