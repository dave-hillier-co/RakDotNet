using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's Packet as <see cref="TcpInterface"/> returns it: the bytes of one receive from a connection, or one message
/// of <see cref="PacketizedTcp"/>, and the address they came from. The caller owns it; there is nothing to deallocate.
/// </summary>
/// <remarks>
/// RakNet null-terminates what TCPInterface receives so it can be printed (TCPInterface.cpp:1181-1183);
/// <see cref="Data"/> holds the received bytes without that terminator.
/// </remarks>
public sealed class TcpPacket
{
    public TcpPacket(SystemAddress address, ReadOnlyMemory<byte> data)
    {
        Address = address;
        Data = data;
        BitLength = data.Length * 8;
    }

    /// <summary>systemAddress</summary>
    public SystemAddress Address { get; }

    /// <summary>data and length</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>bitSize; eight times the length except where RakNet sets it otherwise.</summary>
    public int BitLength { get; init; }

    /// <summary>Packet::deleteData as TCPInterface uses it: received from a socket, not pushed back.</summary>
    internal bool FromNetwork { get; init; }
}
