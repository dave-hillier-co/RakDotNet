using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>
/// What a plugin may call, independent of host; replaces the C++ *Unified helpers. <see cref="RakPeer"/> implements it,
/// and so does the TCP interface of milestone 8.
/// </summary>
public interface IPluginHost
{
    /// <summary>GetMyGUIDUnified</summary>
    RakNetGuid RakNetGuid { get; }

    TimeProvider TimeProvider { get; }

    ulong GetTime();

    /// <summary>SendUnified, SendListUnified.</summary>
    SendResult Send(OutgoingMessage message, AddressOrGuid target, Reliability reliability,
                    Priority priority = Priority.High, byte orderingChannel = 0,
                    SendReceipt forceReceipt = default);

    SendResult Broadcast(OutgoingMessage message, Reliability reliability, Priority priority = Priority.High,
                         byte orderingChannel = 0, AddressOrGuid except = default,
                         SendReceipt forceReceipt = default);

    /// <summary>
    /// AllocatePacketUnified + PushBackPacketUnified. Called from a plugin hook it acts inside the current dispatch step;
    /// from anywhere else, at the start of the next one. <paramref name="generatedLocally"/> sets
    /// <see cref="PeerEvent.IsLocal"/>, as plugins set Packet::wasGeneratedLocally on what they push back.
    /// </summary>
    void Inject(ReadOnlySpan<byte> data, SystemAddress source, RakNetGuid sourceGuid, bool atHead = false, bool generatedLocally = false);

    /// <inheritdoc cref="Inject(ReadOnlySpan{byte}, SystemAddress, RakNetGuid, bool, bool)"/>
    void Inject(OwnedPeerEvent owned, bool atHead = false);

    /// <summary>Null when hosted by TCP; NAT, Router2 and similar plugins require a <see cref="RakPeer"/>.</summary>
    RakPeer? Peer { get; }
}
