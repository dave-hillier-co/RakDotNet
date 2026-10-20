using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's TransportInterface: how a <see cref="ConsoleServer"/> exchanges text with remote consoles.
/// <see cref="TelnetTransport"/> and <see cref="RakNetTransport2"/> implement it. It is unrelated to
/// <see cref="IDatagramTransport"/>, which carries a peer's datagrams.
/// </summary>
/// <remarks>
/// Ported from TransportInterface.h. Send takes the text already formatted, where RakNet's takes a printf format and its
/// arguments, and DeallocatePacket is not needed: a <see cref="TransportPacket"/> is collected.
/// </remarks>
public interface IConsoleTransport
{
    /// <summary>Starts the transport on <paramref name="port"/>; <paramref name="serverMode"/> is unused by RakNet's transports.</summary>
    bool Start(ushort port, bool serverMode);

    /// <summary>Stops the transport and forgets its connections.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "TransportInterface::Stop, kept so ported code reads the same.")]
    void Stop();

    /// <summary>
    /// Sends <paramref name="text"/> to <paramref name="address"/>, or to everyone for
    /// <see cref="SystemAddress.Unassigned"/> where the transport allows it. Text that is empty, or starts with a NUL,
    /// is not sent; text after a NUL is not sent; and at most REMOTE_MAX_TEXT_INPUT - 1 (2047) characters go.
    /// </summary>
    void Send(SystemAddress address, string text);

    /// <summary>Disconnects <paramref name="address"/>.</summary>
    void CloseConnection(SystemAddress address);

    /// <summary>A line of input from a remote console, or null when there is none.</summary>
    TransportPacket? Receive();

    /// <summary>The next system that connected, or <see cref="SystemAddress.Unassigned"/>.</summary>
    SystemAddress HasNewIncomingConnection();

    /// <summary>The next system that disconnected, or <see cref="SystemAddress.Unassigned"/>.</summary>
    SystemAddress HasLostConnection();

    /// <summary>GetCommandParser: the transport's own parser, which <see cref="ConsoleServer"/> installs with it, or null.</summary>
    CommandParser? CommandParser { get; }
}

/// <summary>
/// The Packet a <see cref="IConsoleTransport"/> returns: the text bytes a remote console sent, as Packet::data and
/// Packet::length, and who sent them.
/// </summary>
/// <param name="Address">Packet::systemAddress</param>
/// <param name="Data">Packet::data; the text ends at its first zero byte, if it has one.</param>
public sealed record TransportPacket(SystemAddress Address, ReadOnlyMemory<byte> Data)
{
    /// <summary>Packet::guid; <see cref="RakNetGuid.Unassigned"/> for a transport without GUIDs.</summary>
    public RakNetGuid RakNetGuid { get; init; } = RakNetGuid.Unassigned;

    /// <summary>Packet::bitSize</summary>
    public int BitLength { get; init; }
}
