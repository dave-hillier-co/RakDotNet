using System.Net;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Peer;

/// <summary>
/// RakPeer's pluginListNTS as each connection's ReliabilityLayer sees it: the reliability-layer hooks of every
/// connection, with the remote system's address added (RakPeer.cpp:5440, 5858-5862).
/// </summary>
internal interface IPeerObserver
{
    /// <inheritdoc cref="ISessionObserver.IsObserving"/>
    bool IsObserving { get; }

    /// <inheritdoc cref="ISessionObserver.OnFrame"/>
    void OnFrame(IPEndPoint remote, in SessionFrame frame);

    /// <inheritdoc cref="ISessionObserver.OnAck"/>
    void OnAck(IPEndPoint remote, UInt24 messageNumber, long nowUs);

    /// <inheritdoc cref="ISessionObserver.OnNotification"/>
    void OnNotification(IPEndPoint remote, string message, int bitLength, bool isError);
}

/// <summary>Forwards one connection's hooks to <see cref="PeerCore.Observer"/> with the connection's current address.</summary>
internal sealed class RemoteSystemObserver(PeerCore core, RemoteSystem system) : ISessionObserver
{
    public bool IsObserving => core.Observer is { IsObserving: true };

    public void OnFrame(in SessionFrame frame) => core.Observer?.OnFrame(system.Address, frame);

    public void OnAck(UInt24 messageNumber, long nowUs) => core.Observer?.OnAck(system.Address, messageNumber, nowUs);

    public void OnNotification(string message, int bitLength, bool isError) =>
        core.Observer?.OnNotification(system.Address, message, bitLength, isError);
}
