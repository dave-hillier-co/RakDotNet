using System.Net;
using System.Net.Sockets;

namespace RakDotNet.Transport;

/// <summary>
/// Converts between the <see cref="SocketAddress"/> the socket reads into and the <see cref="IPEndPoint"/> the peer
/// core keys connections by, so a steady stream from known remotes allocates nothing per datagram.
/// </summary>
/// <remarks>Not thread-safe; the transport uses it under its lock.</remarks>
internal sealed class EndPointCache(int capacity = EndPointCache.DefaultCapacity)
{
    /// <summary>Remotes remembered before the cache starts over, bounding what a flood of spoofed senders costs.</summary>
    public const int DefaultCapacity = 1024;

    private readonly Dictionary<SocketAddress, IPEndPoint> _endPoints = [];
    private readonly Dictionary<IPEndPoint, SocketAddress> _addresses = [];

    /// <summary>The endpoint <paramref name="address"/> holds. The address may be reused by the caller afterwards.</summary>
    public IPEndPoint ToEndPoint(SocketAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_endPoints.TryGetValue(address, out var endPoint))
            return endPoint;

        if (_endPoints.Count >= capacity)
            _endPoints.Clear();
        var key = new SocketAddress(address.Family, address.Size);
        address.Buffer.Span[..address.Size].CopyTo(key.Buffer.Span);
        endPoint = (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(key);
        _endPoints.Add(key, endPoint);
        return endPoint;
    }

    /// <summary>The socket address to send to <paramref name="endPoint"/>.</summary>
    public SocketAddress ToSocketAddress(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        if (_addresses.TryGetValue(endPoint, out var address))
            return address;

        if (_addresses.Count >= capacity)
            _addresses.Clear();
        address = endPoint.Serialize();
        _addresses.Add(endPoint, address);
        return address;
    }
}
