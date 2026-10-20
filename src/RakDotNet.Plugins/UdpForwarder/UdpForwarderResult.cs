namespace RakDotNet.Plugins;

/// <summary>
/// UDPForwarderResult (UDPForwarder.h:33-42): what <see cref="UdpForwarder.StartForwarding"/> did. The values are
/// RakNet's, as UDPProxyServer sends them to UDPProxyCoordinator as a byte.
/// </summary>
public enum UdpForwarderResult : byte
{
    /// <summary>UDPFORWARDER_FORWARDING_ALREADY_EXISTS: the pair is already forwarded.</summary>
    ForwardingAlreadyExists,

    /// <summary>UDPFORWARDER_NO_SOCKETS: more entries are in use than <see cref="UdpForwarder.MaxForwardEntries"/>.</summary>
    NoSockets,

    /// <summary>UDPFORWARDER_BIND_FAILED: the forwarding socket could not be bound.</summary>
    BindFailed,

    /// <summary>UDPFORWARDER_INVALID_PARAMETERS: no timeout, a timeout above the maximum, or an unassigned address.</summary>
    InvalidParameters,

    /// <summary>UDPFORWARDER_NOT_RUNNING: <see cref="UdpForwarder.Startup"/> was not called.</summary>
    NotRunning,

    /// <summary>UDPFORWARDER_SUCCESS</summary>
    Success,
}

/// <summary>
/// The outcome of <see cref="UdpForwarder.StartForwarding"/>: the result, and on success the port datagrams are
/// forwarded through and the socket bound to it. As in RakNet, only success fills them in (UDPForwarder.cpp:153-160), so
/// an existing pair reports port 0.
/// </summary>
/// <param name="Result">What happened.</param>
/// <param name="ForwardingPort">The forwarding socket's port on success, otherwise 0.</param>
/// <param name="ForwardingSocket">
/// The forwarding socket on success, otherwise null. Router2 sends from it; what arrives on it belongs to the forwarder.
/// </param>
public readonly record struct UdpForwarding(UdpForwarderResult Result, ushort ForwardingPort, IDatagramTransport? ForwardingSocket);
