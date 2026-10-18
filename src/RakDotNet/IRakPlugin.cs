using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>What <see cref="IRakPlugin.OnEvent"/> does with the event: RakNet's PluginReceiveResult.</summary>
public enum PluginResult : byte
{
    /// <summary>RR_CONTINUE_PROCESSING</summary>
    Continue,

    /// <summary>
    /// RR_STOP_PROCESSING_AND_DEALLOCATE: stops the chain and releases the event. For RR_STOP_PROCESSING, call
    /// <see cref="PeerEvent.Detach"/> first and keep the owned event.
    /// </summary>
    Consume,
}

/// <summary>A plugin: RakNet's PluginInterface2. Every hook is optional.</summary>
/// <remarks>
/// <para>
/// Every hook runs on the dispatch step (a read on <see cref="RakPeer.Events"/>, <see cref="RakPeer.Update"/>, or
/// shutdown's plugin step), one caller at a time, so plugin state touched only from hooks needs no locks.
/// </para>
/// <para>
/// A plugin that also implements <see cref="INetworkObserver"/> is in RakNet's reliability-layer list
/// (UsesReliabilityLayer() true); every hook runs on the other list first, then on that one, each in attach order
/// (RakPeer.cpp:2760-2781).
/// </para>
/// </remarks>
public interface IRakPlugin
{
    /// <summary>
    /// SetRakPeerInterface + OnAttach. Runs before any other hook of the plugin; for plugins in
    /// <see cref="RakPeerOptions.Plugins"/>, during <see cref="RakPeer.Start"/> on the caller.
    /// </summary>
    void OnAttached(IPluginHost host) { }

    /// <summary>OnDetach, at the dispatch step after <see cref="PluginCollection.Detach"/> (RakPeer.cpp:2788-2818).</summary>
    void OnDetached() { }

    /// <summary>OnRakPeerStartup. Only plugins attached through <see cref="RakPeerOptions.Plugins"/> get it, as in RakNet.</summary>
    void OnStartup() { }

    /// <summary>OnRakPeerShutdown, once (RakPeer.cpp:1086-1093).</summary>
    void OnShutdown() { }

    /// <summary>Update: once per read, before anything is popped, including reads that return false.</summary>
    void OnUpdate() { }

    /// <summary>OnNewConnection, for ID_NEW_INCOMING_CONNECTION and ID_CONNECTION_REQUEST_ACCEPTED (RakPeer.cpp:6411-6461).</summary>
    void OnConnected(RakConnection connection, bool isIncoming) { }

    /// <summary>
    /// OnClosedConnection, for ID_DISCONNECTION_NOTIFICATION and ID_CONNECTION_LOST. A local close reaches plugins when
    /// its packet is dispatched.
    /// </summary>
    void OnDisconnected(RakConnection connection, DisconnectReason reason) { }

    /// <summary>OnFailedConnectionAttempt, for the ten failure IDs.</summary>
    void OnConnectFailed(PeerEvent e, ConnectFailure failure) { }

    /// <summary>
    /// OnReceive. Called for every event but ReceiptAbandoned, locally generated ones included (RakPeer.cpp:1559-1562).
    /// The first <see cref="PluginResult.Consume"/> stops the chain.
    /// </summary>
    PluginResult OnEvent(PeerEvent e) => PluginResult.Continue;

    /// <summary>OnPushBackPacket: when an injection is applied, before the entry is queued (RakPeer.cpp:2826-2843).</summary>
    void OnInjected(ReadOnlySpan<byte> data, int bitLength, SystemAddress source) { }
}
