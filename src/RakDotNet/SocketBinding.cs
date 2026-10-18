using System.Net;

namespace RakDotNet;

/// <summary>RakNet's SocketDescriptor. The address family comes from the address.</summary>
public sealed record SocketBinding(IPEndPoint LocalEndPoint)
{
    /// <summary>0.0.0.0:0, SocketDescriptor().</summary>
    public static SocketBinding AnyIPv4 { get; } = new(new IPEndPoint(IPAddress.Any, 0));

    /// <summary>SocketDescriptor(port, 0): every IPv4 interface on <paramref name="port"/>.</summary>
    public static SocketBinding Port(int port) => new(new IPEndPoint(IPAddress.Any, port));

    public int? ReceiveBufferSize { get; init; }

    public int? SendBufferSize { get; init; }

    /// <summary>A transport created elsewhere, adopted at start; how ConnectWithSocket is expressed.</summary>
    public IDatagramTransport? Transport { get; init; }
}
