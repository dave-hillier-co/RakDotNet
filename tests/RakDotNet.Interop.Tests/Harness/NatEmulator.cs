using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The NAT behaviours NatTypeDetectionServer tells apart (NatTypeDetectionCommon.h:28-50).</summary>
public enum NatType
{
    /// <summary>One mapping per inside socket; anything sent to it is passed in.</summary>
    FullCone,

    /// <summary>One mapping per inside socket; passes in what comes from a host the mapping has sent to.</summary>
    AddressRestricted,

    /// <summary>One mapping per inside socket; passes in what comes from an address and port the mapping has sent to.</summary>
    PortRestricted,

    /// <summary>A mapping per inside socket and destination, ports taken in sequence; filters as port restricted.</summary>
    Symmetric,
}

/// <summary>What a <see cref="NatRouter"/> did with one datagram.</summary>
/// <param name="Outbound">From the inside host, rather than from outside.</param>
/// <param name="Inside">The inside socket's real address.</param>
/// <param name="Mapping">The router's external address used, or null when there was none.</param>
/// <param name="Remote">The destination of an outbound datagram, the source of an inbound one.</param>
/// <param name="Outcome">"sent", "lan", "ttl expired", "ttl opened mapping", "delivered", "filtered", "no mapping" or "no free port".</param>
public sealed record NatRouterRecord(bool Outbound, IPEndPoint Inside, IPEndPoint? Mapping, IPEndPoint Remote, int Length, string Outcome);

/// <summary>
/// NAT routers on loopback for the interop harness, so NAT type detection and punchthrough run deterministically on
/// one machine. Each <see cref="NatRouter"/> gives the hosts behind it external addresses and maps and filters as its
/// <see cref="NatType"/> says.
/// </summary>
/// <remarks>
/// <para>
/// A host behind a router sends every datagram to the router's <see cref="NatRouter.GatewayEndPoint"/>, after a header
/// naming the destination, and takes what comes from it after a header naming the source: tools/interop-peer does so
/// after the natGateway command, a RakDotNet peer through <see cref="NatGatewayTransportFactory"/>. docs/interop-protocol.md
/// specifies the header. Everything else that reaches such a host is dropped, as nothing outside routes to a private
/// address. Hosts that are not behind a router use their sockets as they are.
/// </para>
/// <para>
/// Every address here is 127.0.0.1, so "the same host" means the same router for a router's mappings, the same group
/// for addresses given to <see cref="AddHost"/>, and otherwise the same address and port. Address restricted filtering
/// compares those hosts, which lets NatTypeDetectionServer's S1P2 count as S1P1's address and S2P3 as another.
/// </para>
/// <para>
/// A router binds a block of consecutive ports up front and maps in order through it, so a symmetric router's ports
/// are predictable, as NatPunchthroughClient's port prediction assumes. A datagram with a time-to-live of 1 dies at the
/// router; one with 2 opens or refreshes its mapping and goes no further, as SendTTL means it to. Mappings never expire.
/// </para>
/// </remarks>
public sealed class NatEmulator : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<NatRouter> _routers = [];
    private readonly Dictionary<IPEndPoint, object> _hosts = [];
    private bool _disposed;

    /// <summary>Ports in each router's block: the most mappings one router can make.</summary>
    public int PortsPerRouter { get; init; } = 64;

    public IReadOnlyList<NatRouter> Routers
    {
        get
        {
            lock (_gate)
                return [.. _routers];
        }
    }

    /// <summary>Starts a router of <paramref name="type"/> with a block of ports of its own.</summary>
    public NatRouter AddRouter(NatType type)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var router = NatRouter.Start(this, type, PortsPerRouter, _routers.Count + 1);
            _routers.Add(router);
            return router;
        }
    }

    /// <summary>Declares that <paramref name="endPoints"/> share one address, as a server's sockets on one IP do.</summary>
    public void AddHost(params IPEndPoint[] endPoints)
    {
        ArgumentNullException.ThrowIfNull(endPoints);
        var host = new object();
        lock (_gate)
        {
            foreach (var endPoint in endPoints)
                _hosts[endPoint] = host;
        }
    }

    /// <summary>The host an outside address belongs to, for address restricted filtering.</summary>
    internal object HostOf(IPEndPoint endPoint)
    {
        lock (_gate)
        {
            foreach (var router in _routers)
            {
                if (router.OwnsPort(endPoint))
                    return router;
            }
            return _hosts.TryGetValue(endPoint, out var host) ? host : endPoint;
        }
    }

    public async ValueTask DisposeAsync()
    {
        NatRouter[] routers;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            routers = [.. _routers];
        }
        foreach (var router in routers)
            await router.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>One NAT router of a <see cref="NatEmulator"/>.</summary>
public sealed class NatRouter : IAsyncDisposable
{
    /// <summary>The time-to-live byte, the IPv4 address and the port, in network order.</summary>
    public const int HeaderSize = 7;

    private const int FirstBlockPort = 20000;
    private const int LastBlockPort = 48000;
    private const int MaxDatagramSize = 65536 + HeaderSize;
    private const int MaxRecords = 20_000;

    private readonly NatEmulator _emulator;
    private readonly Socket _gateway;
    private readonly Socket[] _block;
    private readonly Mapping?[] _mappingByIndex;
    private readonly Dictionary<(IPEndPoint Inside, IPEndPoint? Destination), Mapping> _mappings = [];
    private readonly HashSet<IPEndPoint> _insides = [];
    private readonly List<NatRouterRecord> _records = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private int _nextIndex;

    private NatRouter(NatEmulator emulator, NatType type, Socket gateway, Socket[] block, int number)
    {
        _emulator = emulator;
        Type = type;
        Number = number;
        _gateway = gateway;
        _block = block;
        _mappingByIndex = new Mapping?[block.Length];
        GatewayEndPoint = (IPEndPoint)gateway.LocalEndPoint!;
        FirstPort = ((IPEndPoint)block[0].LocalEndPoint!).Port;
    }

    public NatType Type { get; }

    /// <summary>The router's number in its emulator, from 1, for test output.</summary>
    public int Number { get; }

    /// <summary>Where the hosts behind the router send, and what they receive from.</summary>
    public IPEndPoint GatewayEndPoint { get; }

    /// <summary>The first port of the router's block; mappings take it and the ports after it in order.</summary>
    public int FirstPort { get; }

    /// <summary>The external addresses the router has mapped, in the order it mapped them.</summary>
    public IReadOnlyList<IPEndPoint> Mappings
    {
        get
        {
            lock (_gate)
                return [.. _mappingByIndex.Take(_nextIndex).Select(m => m!.External)];
        }
    }

    /// <summary>Everything the router did, oldest first, up to the most recent 20000 datagrams.</summary>
    public IReadOnlyList<NatRouterRecord> Records
    {
        get
        {
            lock (_gate)
                return [.. _records];
        }
    }

    internal static NatRouter Start(NatEmulator emulator, NatType type, int ports, int number)
    {
        var gateway = Bind(0);
        var block = BindBlock(ports);
        var router = new NatRouter(emulator, type, gateway, block, number);
        router._loops.Add(Task.Run(router.GatewayLoopAsync));
        for (var i = 0; i < block.Length; i++)
        {
            var index = i;
            router._loops.Add(Task.Run(() => router.MappingLoopAsync(index)));
        }
        return router;
    }

    internal bool OwnsPort(IPEndPoint endPoint) =>
        endPoint.Address.Equals(IPAddress.Loopback) && endPoint.Port >= FirstPort && endPoint.Port < FirstPort + _block.Length;

    public override string ToString() => $"router {Number} ({Type}, gateway {GatewayEndPoint}, ports {FirstPort}-{FirstPort + _block.Length - 1})";

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _gateway.Dispose();
        foreach (var socket in _block)
            socket.Dispose();
        await Task.WhenAll(_loops).ConfigureAwait(false);
        _stop.Dispose();
    }

    private static Socket Bind(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // A run of free consecutive ports below the ephemeral range, so nothing else is handed one of them.
    private static Socket[] BindBlock(int ports)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var first = Random.Shared.Next(FirstBlockPort, LastBlockPort - ports);
            var block = new List<Socket>(ports);
            try
            {
                for (var i = 0; i < ports; i++)
                    block.Add(Bind(first + i));
                return [.. block];
            }
            catch (SocketException)
            {
                foreach (var socket in block)
                    socket.Dispose();
            }
        }
        throw new InvalidOperationException($"No run of {ports} free ports found between {FirstBlockPort} and {LastBlockPort}.");
    }

    private async Task GatewayLoopAsync()
    {
        var buffer = new byte[MaxDatagramSize];
        while (await ReceiveAsync(_gateway, buffer).ConfigureAwait(false) is { } received)
        {
            if (received.ReceivedBytes >= HeaderSize)
                Outbound((IPEndPoint)received.RemoteEndPoint, buffer.AsSpan(0, received.ReceivedBytes));
        }
    }

    private async Task MappingLoopAsync(int index)
    {
        var buffer = new byte[MaxDatagramSize];
        while (await ReceiveAsync(_block[index], buffer).ConfigureAwait(false) is { } received)
            Inbound(index, (IPEndPoint)received.RemoteEndPoint, buffer.AsSpan(0, received.ReceivedBytes));
    }

    // A host behind the router sends: map, remember the destination, and send from the mapping.
    private void Outbound(IPEndPoint inside, ReadOnlySpan<byte> datagram)
    {
        var ttl = datagram[0];
        var destination = ReadAddress(datagram[1..]);
        var payload = datagram[HeaderSize..];
        Mapping? mapping;
        lock (_gate)
        {
            _insides.Add(inside);
            if (ttl == 1)
            {
                Record(true, inside, null, destination, payload.Length, "ttl expired");
                return;
            }
            if (_insides.Contains(destination))
            {
                // Two hosts behind this router reach each other on the LAN.
                Record(true, inside, null, destination, payload.Length, "lan");
                SendToInside(destination, inside, payload);
                return;
            }
            mapping = MappingFor(inside, destination);
            if (mapping is null)
            {
                Record(true, inside, null, destination, payload.Length, "no free port");
                return;
            }
            mapping.SentTo.Add(destination);
            if (ttl == 2)
            {
                Record(true, inside, mapping.External, destination, payload.Length, "ttl opened mapping");
                return;
            }
            Record(true, inside, mapping.External, destination, payload.Length, "sent");
        }
        Send(mapping.Socket, payload, destination);
    }

    // Something outside sends to a mapping: pass it in if the mapping admits it.
    private void Inbound(int index, IPEndPoint source, ReadOnlySpan<byte> payload)
    {
        Mapping? mapping;
        lock (_gate)
            mapping = _mappingByIndex[index];
        var external = (IPEndPoint)_block[index].LocalEndPoint!;
        if (mapping is null)
        {
            Record(false, new IPEndPoint(IPAddress.None, 0), external, source, payload.Length, "no mapping");
            return;
        }
        var admitted = Admits(mapping, source);
        lock (_gate)
            Record(false, mapping.Inside, external, source, payload.Length, admitted ? "delivered" : "filtered");
        if (admitted)
            SendToInside(mapping.Inside, source, payload);
    }

    private bool Admits(Mapping mapping, IPEndPoint source)
    {
        IPEndPoint[] sentTo;
        lock (_gate)
            sentTo = [.. mapping.SentTo];
        return Type switch
        {
            NatType.FullCone => true,
            NatType.AddressRestricted => sentTo.Any(d => Equals(_emulator.HostOf(d), _emulator.HostOf(source))),
            _ => sentTo.Contains(source),
        };
    }

    // Caller holds _gate.
    private Mapping? MappingFor(IPEndPoint inside, IPEndPoint destination)
    {
        var key = (inside, Type == NatType.Symmetric ? destination : null);
        if (_mappings.TryGetValue(key, out var existing))
            return existing;
        if (_nextIndex >= _block.Length)
            return null;
        var index = _nextIndex++;
        var mapping = new Mapping(inside, _block[index], (IPEndPoint)_block[index].LocalEndPoint!);
        _mappingByIndex[index] = mapping;
        _mappings.Add(key, mapping);
        return mapping;
    }

    private void SendToInside(IPEndPoint inside, IPEndPoint source, ReadOnlySpan<byte> payload)
    {
        var datagram = new byte[HeaderSize + payload.Length];
        WriteAddress(datagram.AsSpan(1), source);
        payload.CopyTo(datagram.AsSpan(HeaderSize));
        Send(_gateway, datagram, inside);
    }

    // Caller holds _gate.
    private void Record(bool outbound, IPEndPoint inside, IPEndPoint? mapping, IPEndPoint remote, int length, string outcome)
    {
        if (_records.Count == MaxRecords)
            _records.RemoveAt(0);
        _records.Add(new NatRouterRecord(outbound, inside, mapping, remote, length, outcome));
    }

    internal static IPEndPoint ReadAddress(ReadOnlySpan<byte> header) =>
        new(new IPAddress(header[..4]), BinaryPrimitives.ReadUInt16BigEndian(header[4..6]));

    internal static void WriteAddress(Span<byte> header, IPEndPoint address)
    {
        address.Address.TryWriteBytes(header[..4], out _);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], (ushort)address.Port);
    }

    private static void Send(Socket via, ReadOnlySpan<byte> payload, IPEndPoint to)
    {
        try
        {
            via.SendTo(payload, SocketFlags.None, to);
        }
        catch (SocketException)
        {
            // The destination went away; a real network would lose the datagram too.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<SocketReceiveFromResult?> ReceiveAsync(Socket socket, byte[] buffer)
    {
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                return await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (SocketException)
            {
                // ICMP port unreachable surfaces as ConnectionReset on some platforms; keep routing.
            }
        }
        return null;
    }

    private sealed class Mapping(IPEndPoint inside, Socket socket, IPEndPoint external)
    {
        public IPEndPoint Inside { get; } = inside;

        public Socket Socket { get; } = socket;

        public IPEndPoint External { get; } = external;

        public HashSet<IPEndPoint> SentTo { get; } = [];
    }
}
