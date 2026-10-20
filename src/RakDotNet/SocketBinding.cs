using System.Net;

namespace RakDotNet;

/// <summary>RakNet's SocketDescriptor. The address family comes from the address.</summary>
public sealed record SocketBinding(IPEndPoint LocalEndPoint)
{
    /// <summary>0.0.0.0:0, SocketDescriptor().</summary>
    public static SocketBinding AnyIPv4 { get; } = new(new IPEndPoint(IPAddress.Any, 0));

    /// <summary>SocketDescriptor(port, 0): every IPv4 interface on <paramref name="port"/>.</summary>
    public static SocketBinding Port(int port) => new(new IPEndPoint(IPAddress.Any, port));

    /// <summary>SO_RCVBUF; RakNet's 256 KiB by default (RakNetSocket2_Berkley.cpp:26-28). Null keeps the OS default.</summary>
    public int? ReceiveBufferSize { get; init; } = 256 * 1024;

    /// <summary>SO_SNDBUF; RakNet's 16 KiB by default (RakNetSocket2_Berkley.cpp:40-43). Null keeps the OS default.</summary>
    public int? SendBufferSize { get; init; } = 16 * 1024;

    /// <summary>A transport created elsewhere, adopted at start; how ConnectWithSocket is expressed.</summary>
    public IDatagramTransport? Transport { get; init; }
}
