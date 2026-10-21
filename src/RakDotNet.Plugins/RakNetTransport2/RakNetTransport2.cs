using System.Collections.Concurrent;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's RakNetTransport2: a <see cref="ConsoleServer"/> transport over the <see cref="RakPeer"/> it is attached to.
/// Console text travels as ID_TRANSPORT_STRING messages, so any RakNet peer is a console client, as
/// Samples/CommandConsoleClient is: it connects and sends the ID, the line and a zero byte.
/// </summary>
/// <remarks>
/// <para>
/// Ported from RakNetTransport2.cpp. Every connection the peer makes or accepts is a console connection. The plugin
/// consumes every ID_TRANSPORT_STRING it receives: one with no text is dropped, and the rest are queued for
/// <see cref="Receive"/> without the ID byte, a trailing zero byte included. <see cref="Send"/> writes the ID, the text
/// and a zero byte, RELIABLE_ORDERED at MEDIUM_PRIORITY on channel 0, to everyone for
/// <see cref="SystemAddress.Unassigned"/>. Start and Stop do nothing but Stop's clearing of the queues; the peer's own
/// lifetime governs the connections.
/// </para>
/// <para>
/// The queues are thread-safe, so the console may run on another thread than the one reading the peer's events.
/// </para>
/// </remarks>
public class RakNetTransport2 : IConsoleTransport, IRakPlugin
{
    private readonly ConcurrentQueue<SystemAddress> _newConnections = new();
    private readonly ConcurrentQueue<SystemAddress> _lostConnections = new();
    private readonly ConcurrentQueue<TransportPacket> _packets = new();
    private IPluginHost? _host;

    /// <summary>GetCommandParser: none.</summary>
    public virtual CommandParser? CommandParser => null;

    /// <summary>Start: nothing to start; true.</summary>
    public virtual bool Start(ushort port, bool serverMode) => true;

    /// <summary>Stop: forgets queued connections and input.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "TransportInterface::Stop, kept so ported code reads the same.")]
    public virtual void Stop()
    {
        _newConnections.Clear();
        _lostConnections.Clear();
        _packets.Clear();
    }

    /// <summary>
    /// Send (RakNetTransport2.cpp:56-72). Nothing is sent before the plugin is attached or once its peer has shut down,
    /// as RakNet's Send does nothing on an inactive peer.
    /// </summary>
    public virtual void Send(SystemAddress address, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = ConsoleText.Truncate(ConsoleText.CString(text), ConsoleText.MaxTextInput);
        if (text.Length == 0 || _host is not { } host)
            return;

        var message = new byte[1 + text.Length + 1];
        message[0] = (byte)MessageId.TransportString;
        ConsoleText.ToBytes(text).CopyTo(message, 1);
        try
        {
            if (address == SystemAddress.Unassigned)
                host.Broadcast(new OutgoingMessage(message), Reliability.ReliableOrdered, Priority.Medium, 0);
            else
                host.Send(new OutgoingMessage(message), address, Reliability.ReliableOrdered, Priority.Medium, 0);
        }
        catch (ObjectDisposedException)
        {
            // The peer has shut down.
        }
    }

    /// <summary>CloseConnection: closes the connection with a notification on channel 0.</summary>
    public virtual void CloseConnection(SystemAddress address)
    {
        if (_host?.Peer is not { IsActive: true } peer)
            return;
        try
        {
            peer.Close(address, notify: true, orderingChannel: 0);
        }
        catch (ObjectDisposedException)
        {
            // The peer has shut down.
        }
    }

    public virtual TransportPacket? Receive() => _packets.TryDequeue(out var packet) ? packet : null;

    public virtual SystemAddress HasNewIncomingConnection() =>
        _newConnections.TryDequeue(out var address) ? address : SystemAddress.Unassigned;

    public virtual SystemAddress HasLostConnection() =>
        _lostConnections.TryDequeue(out var address) ? address : SystemAddress.Unassigned;

    public virtual void OnAttached(IPluginHost host) => _host = host;

    public virtual void OnDetached() => _host = null;

    /// <summary>OnReceive (RakNetTransport2.cpp:100-121).</summary>
    public virtual PluginResult OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || data[0] != (byte)MessageId.TransportString)
            return PluginResult.Continue;
        if (data.Length == 1)
            return PluginResult.Consume;
        _packets.Enqueue(new TransportPacket(e.Address, data[1..].ToArray())
        {
            RakNetGuid = e.RakNetGuid,
            BitLength = e.BitLength - 8,
        });
        return PluginResult.Consume;
    }

    /// <summary>OnNewConnection, for incoming and outgoing connections alike.</summary>
    public virtual void OnConnected(RakConnection connection, bool isIncoming)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _newConnections.Enqueue(connection.Address);
    }

    /// <summary>OnClosedConnection</summary>
    public virtual void OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _lostConnections.Enqueue(connection.Address);
    }
}
