using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>Writing and reading what the UDPProxy plugins share.</summary>
internal static class UdpProxyWire
{
    /// <summary>A RakString held as its bytes, which RakNet compares with strcmp; strings are UTF-8 at the API.</summary>
    public static byte[] ToRakString(string value) => Encoding.UTF8.GetBytes(value);

    public static string FromRakString(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);

    /// <summary>A stream holding ID_UDP_PROXY_GENERAL and <paramref name="message"/>.</summary>
    public static BitStream Begin(UdpProxyMessage message)
    {
        var stream = new BitStream();
        stream.WriteByte((byte)MessageId.UdpProxyGeneral);
        stream.WriteByte((byte)message);
        return stream;
    }

    /// <summary>RakString::Serialize: a u16 length then the bytes, aligned.</summary>
    public static void WriteRakString(BitStream stream, ReadOnlySpan<byte> value)
    {
        var writer = stream.BeginWrite(sizeof(ushort) + value.Length + 1);
        writer.WriteRakString(value);
        stream.EndWrite(writer);
    }

    /// <summary>The address and the uint32 RakNet::TimeMS, which RakNet writes as they are.</summary>
    public static uint ToTimeMs(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0 : (uint)Math.Min(duration.TotalMilliseconds, uint.MaxValue);

    /// <summary>MEDIUM_PRIORITY, RELIABLE_ORDERED, channel 0, as every UDPProxy message is sent.</summary>
    public static void Send(RakPeer peer, BitStream stream, SystemAddress target)
    {
        if (target.IsAssigned)
            peer.Send(stream, target, Reliability.ReliableOrdered, Priority.Medium, 0);
    }
}
