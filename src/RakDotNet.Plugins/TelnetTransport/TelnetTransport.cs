using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's TelnetTransport: a <see cref="ConsoleServer"/> transport for telnet clients, over a
/// <see cref="TcpInterface"/> it creates on <see cref="Start"/>. The server assembles each client's keystrokes into
/// lines and echoes them, since telnet clients do not reliably send whole lines or echo locally.
/// </summary>
/// <remarks>
/// <para>
/// Ported from TelnetTransport.cpp with ECHO_INPUT defined, as RakNet builds it. A new connection is told
/// IAC WILL ECHO (255 251 1). Each packet received from a client is handled whole: one that starts with 127 or more, a
/// tab or an escape is dropped, so telnet negotiation and cursor keys are ignored; otherwise it is echoed, a backspace
/// also echoing a space and a backspace, and its characters are added to the client's line. Printable characters are
/// kept, a backspace removes the last one, and a line feed ends the line; a carriage return and other control
/// characters are dropped.
/// </para>
/// <para>
/// RakNet's quirks are kept: a packet from a client whose connection <see cref="HasNewIncomingConnection"/> has not
/// reported yet is dropped; only the first non-empty line of a packet is returned and the rest of the packet is
/// dropped; the up arrow alone in a packet (27 91 65) replaces the line being typed with the last line returned, by
/// sending a backspace for each character of the line buffer, which still holds the tail of longer earlier lines,
/// followed by the last line; a client reported twice is tracked twice; and a lost client whose entry is swapped into
/// the place of the removed one is skipped.
/// </para>
/// <para>
/// The polling members (<see cref="Receive"/>, <see cref="HasNewIncomingConnection"/>, <see cref="HasLostConnection"/>)
/// are for one thread, the console's; <see cref="Send"/> may be called from any thread.
/// </para>
/// </remarks>
public class TelnetTransport : IConsoleTransport, IDisposable
{
    // TelnetTransport::Start: TCPInterface::Start(port, 64) (TelnetTransport.cpp:52).
    private const ushort MaxIncomingConnections = 64;

    // RFC 857: IAC WILL ECHO (TelnetTransport.cpp:237-244).
    private static readonly byte[] WillEcho = [255, 251, 1];

    private static readonly byte[] SpaceThenBack = [(byte)' ', 8];

    private readonly List<TelnetClient> _remoteClients = [];
    private volatile TcpInterface? _tcp;
    private string? _sendPrefix;
    private string? _sendSuffix;

    /// <summary>The address the transport listens on once started; null when it is not listening.</summary>
    public IPEndPoint? ListenEndPoint => _tcp?.ListenEndPoint;

    /// <summary>SetSendPrefix: text put before everything sent; null for none.</summary>
    public string? SendPrefix
    {
        get => _sendPrefix;
        set => _sendPrefix = value;
    }

    /// <summary>SetSendSuffix: text put after everything sent, as far as 2047 characters allow; null for none.</summary>
    public string? SendSuffix
    {
        get => _sendSuffix;
        set => _sendSuffix = value;
    }

    /// <summary>The interface the transport created on <see cref="Start"/>, as RakNet keeps it protected; null when stopped.</summary>
    protected TcpInterface? TcpInterface => _tcp;

    /// <summary>GetCommandParser: none.</summary>
    public virtual CommandParser? CommandParser => null;

    /// <summary>
    /// Start: creates the <see cref="TcpInterface"/> on first use and starts it on <paramref name="port"/> with 64
    /// incoming connections, on every address.
    /// </summary>
    public virtual bool Start(ushort port, bool serverMode)
    {
        var tcp = _tcp ??= new TcpInterface();
        return tcp.Start(port, MaxIncomingConnections);
    }

    /// <summary>Stop: stops and releases the <see cref="TcpInterface"/> and forgets every client.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "TransportInterface::Stop, kept so ported code reads the same.")]
    public virtual void Stop()
    {
        if (_tcp is not { } tcp)
            return;
        tcp.Stop();
        _remoteClients.Clear();
        tcp.Dispose();
        _tcp = null;
    }

    /// <summary>
    /// Send (TelnetTransport.cpp:65-98): the prefix and the text, together at most 2047 characters, then as much of the
    /// suffix as fits in the same 2047.
    /// </summary>
    public virtual void Send(SystemAddress address, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_tcp is not { } tcp || text.Length == 0 || text[0] == '\0')
            return;
        var prefix = ConsoleText.CString(_sendPrefix ?? "");
        var line = ConsoleText.Truncate(prefix + ConsoleText.CString(text), ConsoleText.MaxTextInput);
        if (_sendSuffix is { } suffix)
        {
            var available = ConsoleText.MaxTextInput - line.Length - 1;
            suffix = ConsoleText.CString(suffix);
            line += suffix.Length <= available ? suffix : suffix[..available];
        }
        tcp.Send(ConsoleText.ToBytes(line), address, broadcast: false);
    }

    public virtual void CloseConnection(SystemAddress address) => _tcp?.CloseConnection(address);

    /// <summary>Receive (TelnetTransport.cpp:103-221): handles one packet and returns the line it completed, if any.</summary>
    public virtual TransportPacket? Receive()
    {
        if (_tcp is not { } tcp || tcp.Receive() is not { } packet)
            return null;

        // The last client with the address, as RakNet's loop leaves it.
        TelnetClient? client = null;
        foreach (var candidate in _remoteClients)
        {
            if (candidate.Address == packet.Address)
                client = candidate;
        }
        if (client is null)
            return null;

        var data = packet.Data.Span;
        if (data.Length == 3 && data[0] == 27 && data[1] == 91 && data[2] == 65)
        {
            client.RecallLastLine(tcp, packet.Address);
            return null;
        }

        // 127 and up (delete, telnet commands), tab and escape (cursor keys among them). RakNet's later test for cursor
        // keys (TelnetTransport.cpp:171-175) is never reached, as they start with escape.
        if (data.IsEmpty || data[0] >= 127 || data[0] == 9 || data[0] == 27)
            return null;

        tcp.Send(data, packet.Address, broadcast: false);
        foreach (var c in data)
        {
            if (c == 8)
                tcp.Send(SpaceThenBack, packet.Address, broadcast: false);
            if (client.ReassembleLine(c) && client.TakeLine() is { } line)
                return new TransportPacket(packet.Address, line) { BitLength = line.Length * 8 };
        }
        return null;
    }

    /// <summary>HasNewIncomingConnection (TelnetTransport.cpp:228-281): sends IAC WILL ECHO and starts tracking the client.</summary>
    public virtual SystemAddress HasNewIncomingConnection()
    {
        if (_tcp is not { } tcp)
            return SystemAddress.Unassigned;
        var address = tcp.HasNewIncomingConnection();
        if (address == SystemAddress.Unassigned)
            return address;

        tcp.Send(WillEcho, address, broadcast: false);
        TelnetClient? client = null;
        foreach (var candidate in _remoteClients)
        {
            if (candidate.Address == address)
            {
                client = candidate;
                client.CursorPosition = 0;
            }
        }
        _remoteClients.Add(client ?? new TelnetClient(address));
        return address;
    }

    /// <summary>HasLostConnection (TelnetTransport.cpp:282-300): stops tracking the client.</summary>
    public virtual SystemAddress HasLostConnection()
    {
        if (_tcp is not { } tcp)
            return SystemAddress.Unassigned;
        var address = tcp.HasLostConnection();
        if (address == SystemAddress.Unassigned)
            return address;
        for (var i = 0; i < _remoteClients.Count; i++)
        {
            if (_remoteClients[i].Address == address)
            {
                _remoteClients[i] = _remoteClients[^1];
                _remoteClients.RemoveAt(_remoteClients.Count - 1);
            }
        }
        return address;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            Stop();
    }

    /// <summary>
    /// A client's line buffers as C strings: textInput, which ReassembleLine terminates only at the end of a line or a
    /// backspace, and lastSentTextInput, each REMOTE_MAX_TEXT_INPUT long. They are sized here so that what overruns
    /// RakNet's arrays stays in bounds.
    /// </summary>
    private sealed class TelnetClient(SystemAddress address)
    {
        private readonly byte[] _textInput = new byte[(2 * ConsoleText.MaxTextInput) + 1];
        private readonly byte[] _lastSentTextInput = new byte[ConsoleText.MaxTextInput + 1];

        public SystemAddress Address { get; } = address;

        public int CursorPosition { get; set; }

        // ReassembleLine (TelnetTransport.cpp:336-368).
        public bool ReassembleLine(byte c)
        {
            if (c == '\n')
            {
                _textInput[CursorPosition] = 0;
                CursorPosition = 0;
                return true;
            }
            if (c == 8)
            {
                if (CursorPosition > 0)
                    _textInput[--CursorPosition] = 0;
            }
            else if (c is >= 32 and < 127 && CursorPosition < ConsoleText.MaxTextInput)
            {
                _textInput[CursorPosition++] = c;
            }
            return false;
        }

        // TelnetTransport.cpp:200-216: a non-empty line becomes the last line sent and is returned.
        public byte[]? TakeLine()
        {
            var length = Strlen(_textInput);
            if (length == 0)
                return null;
            var line = _textInput.AsSpan(0, Math.Min(length, ConsoleText.MaxTextInput)).ToArray();
            line.CopyTo(_lastSentTextInput, 0);
            _lastSentTextInput[line.Length] = 0;
            return line;
        }

        // TelnetTransport.cpp:140-154.
        public void RecallLastLine(TcpInterface tcp, SystemAddress address)
        {
            var lastLength = Strlen(_lastSentTextInput);
            if (lastLength == 0)
                return;
            // strcat past the end of textInput is undefined in RakNet; here the line is cut to fit.
            var length = Math.Min(Strlen(_textInput), _textInput.Length - 1 - lastLength);
            Array.Fill(_textInput, (byte)8, 0, length);
            _lastSentTextInput.AsSpan(0, lastLength).CopyTo(_textInput.AsSpan(length));
            _textInput[length + lastLength] = 0;
            tcp.Send(_textInput.AsSpan(0, length + lastLength), address, broadcast: false);
            _lastSentTextInput.AsSpan(0, lastLength + 1).CopyTo(_textInput);
            CursorPosition = lastLength;
        }

        private static int Strlen(byte[] text)
        {
            var end = Array.IndexOf(text, (byte)0);
            return end >= 0 ? end : text.Length;
        }
    }
}
