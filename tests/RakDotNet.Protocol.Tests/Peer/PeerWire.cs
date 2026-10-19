using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Peer;

/// <summary>Encoding and draining helpers for tests that hand a <see cref="PeerCore"/> datagrams directly.</summary>
internal static class PeerWire
{
    public static byte[] Encode<T>(T message)
        where T : IWireMessage<T>
    {
        var writer = new BitStreamWriter(new byte[2048]);
        message.Write(ref writer);
        return writer.Written.ToArray();
    }

    public static T Decode<T>(byte[] bytes)
        where T : IWireMessage<T>
    {
        var reader = new BitStreamReader(bytes);
        Assert.True(T.TryRead(ref reader, out var value), $"{typeof(T).Name} failed to decode {Convert.ToHexStringLower(bytes)}");
        return value;
    }

    public static List<OutgoingDatagram> Datagrams(PeerCore peer)
    {
        var datagrams = new List<OutgoingDatagram>();
        while (peer.TryDequeueDatagram(out var datagram))
            datagrams.Add(datagram);
        return datagrams;
    }

    public static List<PeerEvent> Events(PeerCore peer)
    {
        var events = new List<PeerEvent>();
        while (peer.TryDequeueEvent(out var e))
            events.Add(e);
        return events;
    }

    /// <summary>The messages carried by the connected (reliability-layer) datagrams among <paramref name="datagrams"/>.</summary>
    public static List<EncapsulatedMessage> ConnectedMessages(IEnumerable<byte[]> datagrams)
    {
        var messages = new List<EncapsulatedMessage>();
        foreach (var datagram in datagrams.Where(d => (d[0] & 0x80) != 0))
        {
            if (!EncapsulatedMessageReader.TryOpenDatagram(datagram, out _, out var reader))
                continue;
            while (reader.TryReadNext(out var message))
                messages.Add(message);
        }
        return messages;
    }
}
