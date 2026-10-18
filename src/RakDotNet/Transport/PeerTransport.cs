using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Transport;

/// <summary>
/// Puts one <see cref="PeerCore"/> on one UDP socket: RakPeer's receive thread and update thread
/// (RakPeer.cpp:6302-6370) as a receive loop and a timer loop sharing a lock.
/// </summary>
/// <remarks>
/// <para>
/// Each datagram read is handed to <see cref="PeerCore.Receive"/> and followed at once by an update cycle, as RakPeer
/// signals quitAndDataEvents when data arrives. Otherwise an update cycle runs every
/// <see cref="PeerTransportOptions.UpdateInterval"/>. Commands run through <see cref="Execute{T}"/>, which also
/// updates straight away, as RakPeer's buffered commands wake the update thread.
/// </para>
/// <para>
/// Everything the core produces is flushed after each call: datagrams to the socket and events, in order, to
/// <see cref="Events"/>.
/// </para>
/// </remarks>
internal sealed class PeerTransport : IAsyncDisposable
{
    // Larger than any datagram RakNet sends (MAXIMUM_MTU_SIZE), so an oversized one is read whole and then ignored
    // rather than truncated.
    private const int ReceiveBufferSize = 64 * 1024;

    // RakPeer::Shutdown polls for active systems every 15 ms (RakPeer.cpp:1082).
    private static readonly TimeSpan ShutdownPollInterval = TimeSpan.FromMilliseconds(15);

    private readonly Socket _socket;
    private readonly PeerCore _core;
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private readonly Lock _gate = new();
    private readonly EndPointCache _endPoints = new();
    private readonly Channel<PeerEvent> _events = Channel.CreateUnbounded<PeerEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly CancellationTokenSource _stopping = new();
    private readonly PeriodicTimer _timer;
    private Task _receiveLoop = Task.CompletedTask;
    private Task _updateLoop = Task.CompletedTask;
    private bool _stopped;

    private PeerTransport(Socket socket, PeerCore core, PeerTransportOptions options, long startTimestamp)
    {
        _socket = socket;
        _core = core;
        _time = options.TimeProvider;
        _startTimestamp = startTimestamp;
        _timer = new PeriodicTimer(options.UpdateInterval, _time);
        BoundEndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    /// <summary>The address the socket is bound to, with the port actually bound.</summary>
    public IPEndPoint BoundEndPoint { get; }

    public ulong Guid => _core.Guid;

    /// <summary>The peer's ordered event stream. It completes when the transport stops.</summary>
    public ChannelReader<PeerEvent> Events => _events.Reader;

    /// <summary>Milliseconds since the transport started, the clock every time on the wire is read from.</summary>
    public long NowMs => NowUs / 1000;

    private long NowUs => (long)_time.GetElapsedTime(_startTimestamp).TotalMicroseconds;

    /// <summary>Binds the socket, builds the peer core and starts the receive and update loops.</summary>
    public static async Task<PeerTransport> StartAsync(PeerTransportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var socket = new Socket(options.LocalEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(options.LocalEndPoint);
            var bound = (IPEndPoint)socket.LocalEndPoint!;
            var hostAddresses = await GetHostAddressesAsync(cancellationToken).ConfigureAwait(false);
            var core = new PeerCore(new PeerOptions
            {
                Guid = options.Guid ?? GenerateGuid(),
                MaxConnections = options.MaxConnections,
                MaxIncomingConnections = options.MaxIncomingConnections,
                LocalAddresses = LocalAddressList.Build(hostAddresses, (ushort)bound.Port),
            });
            var transport = new PeerTransport(socket, core, options, options.TimeProvider.GetTimestamp());
            transport._receiveLoop = Task.Run(transport.ReceiveLoopAsync, CancellationToken.None);
            transport._updateLoop = Task.Run(transport.UpdateLoopAsync, CancellationToken.None);
            return transport;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a command against the peer core with the current time, then an update cycle so its effects leave at once.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The transport has stopped.</exception>
    public T Execute<T>(Func<PeerCore, long, T> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var result = command(_core, NowUs);
            UpdateAndFlush();
            return result;
        }
    }

    /// <inheritdoc cref="Execute{T}"/>
    public void Execute(Action<PeerCore, long> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Execute((core, nowUs) =>
        {
            command(core, nowUs);
            return true;
        });
    }

    /// <summary>
    /// RakPeer::Shutdown (RakPeer.cpp:1041-1221): with a block time, notifies every connected system and keeps running
    /// until all have closed or the time is up; then stops the loops, closes the socket and completes
    /// <see cref="Events"/>.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan blockDuration, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            if (blockDuration > TimeSpan.Zero)
            {
                _core.NotifyAllForShutdown(channel: 0, Priority.Low, NowUs);
                UpdateAndFlush();
            }
        }

        try
        {
            var startedMs = NowMs;
            while (NowMs - startedMs < blockDuration.TotalMilliseconds)
            {
                lock (_gate)
                {
                    if (_core.ActiveSystemCount == 0)
                        break;
                }
                await Task.Delay(ShutdownPollInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // A cancelled wait still stops the transport, as the block time running out would.
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops without notifying anyone, as Shutdown(0) does.</summary>
    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _timer.Dispose();
        _socket.Dispose();
        try
        {
            await Task.WhenAll(_receiveLoop, _updateLoop).ConfigureAwait(false);
        }
        finally
        {
            _events.Writer.TryComplete();
            _stopping.Dispose();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = GC.AllocateArray<byte>(ReceiveBufferSize, pinned: true);
        var from = new SocketAddress(_socket.AddressFamily);
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            int length;
            try
            {
                length = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, from, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception) when (IsPerDatagramError(exception.SocketErrorCode) || token.IsCancellationRequested)
            {
                continue;
            }
            catch (SocketException exception)
            {
                // The socket itself failed: nothing more will arrive, so readers of the event stream are told why.
                _events.Writer.TryComplete(exception);
                return;
            }

            lock (_gate)
            {
                if (_stopped)
                    return;
                _core.Receive(_endPoints.ToEndPoint(from), buffer.AsSpan(0, length), NowUs);
                UpdateAndFlush();
            }
        }
    }

    private async Task UpdateLoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_stopped)
                        return;
                    UpdateAndFlush();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // One RunUpdateCycle, then everything it and any preceding call produced. Called under the lock.
    private void UpdateAndFlush()
    {
        _core.Update(NowUs);
        while (_core.TryDequeueDatagram(out var datagram))
        {
            try
            {
                _socket.SendTo(datagram.Data, SocketFlags.None, _endPoints.ToSocketAddress(datagram.Remote));
            }
            catch (SocketException)
            {
                // RakNet ignores send failures (RNS2_Berkley::Send returns the error and nobody checks it); the
                // reliability layer resends or times out.
            }
        }
        while (_core.TryDequeueEvent(out var peerEvent))
            _events.Writer.TryWrite(peerEvent);
    }

    // Errors that concern one datagram, not the socket: an ICMP port unreachable from an earlier send, or an
    // oversized datagram.
    private static bool IsPerDatagramError(SocketError error) =>
        error is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.MessageSize or SocketError.NetworkReset or SocketError.HostUnreachable;

    // RakPeer gets ipList from gethostbyname of the host name (RakNetSocket2_Windows_Linux.cpp:70-103); a host that does
    // not resolve has no internal addresses.
    private static async Task<IPAddress[]> GetHostAddressesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(Dns.GetHostName(), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    // RakPeer::GenerateGUID (RakPeer.cpp:4525-4528), avoiding UNASSIGNED_RAKNET_GUID.
    private static ulong GenerateGuid()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong guid;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            guid = BitConverter.ToUInt64(bytes);
        }
        while (guid == RakNetConstants.UnassignedGuid);
        return guid;
    }
}
