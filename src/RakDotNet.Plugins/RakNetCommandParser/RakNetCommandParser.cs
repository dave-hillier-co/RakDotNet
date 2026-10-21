using System.Globalization;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's RakNetCommandParser, named <c>RakNet</c> on the console: remote consoles call the functions of a
/// <see cref="RakPeer"/> by their RakPeerInterface names and see RakNet's answers.
/// </summary>
/// <remarks>
/// <para>
/// Ported from RakNetCommandParser.cpp, with its command table, parameter counts and replies. Numbers are read with
/// atoi, booleans as atoi(...) != 0, and addresses as SystemAddress's constructor reads text, so <c>a.b.c.d|port</c>.
/// Each command maps to the public API: <c>Disconnect</c> is <see cref="RakPeer.ShutdownAsync"/>, waited for as
/// RakNet's Shutdown blocks, and <c>Startup</c> on a peer that has shut down starts a new <see cref="RakPeer"/> with the
/// given maximum connections, port and address, which becomes <see cref="Peer"/>, as RakNet allows Startup after
/// Shutdown; on a running peer it returns RAKNET_ALREADY_STARTED (1). Enum results are RakNet's numbers. A command that
/// changes a peer that has shut down does nothing, as RakNet's do on an inactive peer.
/// </para>
/// <para>
/// RakNet's quirks are kept: <c>IsConnected</c> and <c>IsNetworkSimulatorActive</c> are registered but answer nothing,
/// and <c>GetConnectionState</c> is answered but not registered, so the console never reaches it; <c>Startup</c> takes
/// four parameters and uses three; the security exception commands read the parameter after the one they take, a null
/// pointer, so <c>AddToSecurityExceptionList</c> adds nothing that matches, <c>RemoveFromSecurityExceptionList</c>
/// clears the list and <c>IsInSecurityExceptionList</c> is false; and an address result prints the address of the system
/// asking before the result's, as ReturnResult(SystemAddress) does. <c>InitializeSecurity</c> returns false, as RakNet's
/// does with no keys and on a running peer, because server security is fixed when a peer starts here (decision 11.14),
/// and <c>DisableSecurity</c> does nothing for the same reason. <c>GetIncomingPassword</c> prints the password up to
/// its first zero byte, where RakNet prints an unterminated buffer. Arguments the C# API refuses with an
/// <see cref="ArgumentException"/> get RakNet's reply for them, so no line a console user types escapes
/// <see cref="ConsoleServer.Update"/>: <c>Connect</c> returns false, as RakNet's INVALID_PARAMETER does, and a command
/// that changes the peer does nothing and reports the call.
/// </para>
/// </remarks>
public class RakNetCommandParser : CommandParser
{
    // StartupResult (RakNetTypes.h:41-55) as numbers.
    private const int RakNetStarted = 0;
    private const int RakNetAlreadyStarted = 1;
    private const int InvalidMaxConnections = 3;
    private const int SocketFailedToBind = 6;

    public RakNetCommandParser()
    {
        RegisterCommand(4, "Startup", "( unsigned int maxConnections, unsigned short localPort, const char *forceHostAddress );");
        RegisterCommand(0, "InitializeSecurity", "();");
        RegisterCommand(0, "DisableSecurity", "( void );");
        RegisterCommand(1, "AddToSecurityExceptionList", "( const char *ip );");
        RegisterCommand(1, "RemoveFromSecurityExceptionList", "( const char *ip );");
        RegisterCommand(1, "IsInSecurityExceptionList", "( const char *ip );");
        RegisterCommand(1, "SetMaximumIncomingConnections", "( unsigned short numberAllowed );");
        RegisterCommand(0, "GetMaximumIncomingConnections", "( void ) const;");
        RegisterCommand(4, "Connect", "( const char* host, unsigned short remotePort, const char *passwordData, int passwordDataLength );");
        RegisterCommand(2, "Disconnect", "( unsigned int blockDuration, unsigned char orderingChannel=0 );");
        RegisterCommand(0, "IsActive", "( void ) const;");
        RegisterCommand(0, "GetConnectionList", "() const;");
        RegisterCommand(3, "CloseConnection", "( const SystemAddress target, bool sendDisconnectionNotification, unsigned char orderingChannel=0 );");
        RegisterCommand(2, "IsConnected", "( );");
        RegisterCommand(1, "GetIndexFromSystemAddress", "( const SystemAddress systemAddress );");
        RegisterCommand(1, "GetSystemAddressFromIndex", "( unsigned int index );");
        RegisterCommand(2, "AddToBanList", "( const char *IP, RakNet::TimeMS milliseconds=0 );");
        RegisterCommand(1, "RemoveFromBanList", "( const char *IP );");
        RegisterCommand(0, "ClearBanList", "( void );");
        RegisterCommand(1, "IsBanned", "( const char *IP );");
        RegisterCommand(1, "Ping1", "( const SystemAddress target );");
        RegisterCommand(3, "Ping2", "( const char* host, unsigned short remotePort, bool onlyReplyOnAcceptingConnections );");
        RegisterCommand(1, "GetAveragePing", "( const SystemAddress systemAddress );");
        RegisterCommand(1, "GetLastPing", "( const SystemAddress systemAddress ) const;");
        RegisterCommand(1, "GetLowestPing", "( const SystemAddress systemAddress ) const;");
        RegisterCommand(1, "SetOccasionalPing", "( bool doPing );");
        RegisterCommand(2, "SetOfflinePingResponse", "( const char *data, const unsigned int length );");
        RegisterCommand(0, "GetInternalID", "( void ) const;");
        RegisterCommand(1, "GetExternalID", "( const SystemAddress target ) const;");
        RegisterCommand(2, "SetTimeoutTime", "( RakNet::TimeMS timeMS, const SystemAddress target );");
        RegisterCommand(0, "GetMTUSize", "( void ) const;");
        RegisterCommand(0, "GetNumberOfAddresses", "( void );");
        RegisterCommand(1, "GetLocalIP", "( unsigned int index );");
        RegisterCommand(1, "AllowConnectionResponseIPMigration", "( bool allow );");
        RegisterCommand(4, "AdvertiseSystem", "( const char *host, unsigned short remotePort, const char *data, int dataLength );");
        RegisterCommand(2, "SetIncomingPassword", "( const char* passwordData, int passwordDataLength );");
        RegisterCommand(0, "GetIncomingPassword", "( void );");
        RegisterCommand(0, "IsNetworkSimulatorActive", "( void );");
    }

    public override string Name => "RakNet";

    /// <summary>SetRakPeerInterface: the peer the commands call; without one the parser answers nothing.</summary>
    public RakPeer? Peer { get; set; }

    public override void SendHelp(IConsoleTransport transport, SystemAddress address)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (Peer is not null)
        {
            transport.Send(address, "The RakNet parser provides mirror functions to RakPeer\r\n");
            transport.Send(address, "SystemAddresss take two parameters: send <BinaryAddress> <Port>.\r\n");
            transport.Send(address, "For bool, send 1 or 0.\r\n");
        }
        else
        {
            transport.Send(address, "Parser not active.  Call SetRakPeerInterface.\r\n");
        }
    }

    /// <summary>OnCommand (RakNetCommandParser.cpp:80-289). False when there is no peer.</summary>
    public override bool OnCommand(string command, IReadOnlyList<string> parameters, IConsoleTransport transport, SystemAddress address,
                                   string originalString)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(transport);
        if (Peer is not { } peer)
            return false;

        string? P(int index) => Parameter(parameters, index);
        int Int(int index) => ConsoleText.Atoi(P(index));
        bool Bool(int index) => Int(index) != 0;
        SystemAddress Address(int index) => ToSystemAddress(P(index));
        void Done() => ReturnResult(command, transport, address);

        switch (command)
        {
            case "Startup":
                ReturnResult(Startup(peer, (ushort)Int(0), (ushort)Int(1), P(2)), command, transport, address);
                break;
            case "InitializeSecurity":
                ReturnResult(false, command, transport, address);
                break;
            case "DisableSecurity":
                Done();
                break;
            case "AddToSecurityExceptionList":
                // RakString of the null parameter is empty, a pattern no address matches (RakPeer.cpp:837-843).
                Done();
                break;
            case "RemoveFromSecurityExceptionList":
                // A null address clears the list (RakPeer.cpp:845-873).
                Change(() => peer.SecurityExceptions.Clear());
                Done();
                break;
            case "IsInSecurityExceptionList":
                ReturnResult(false, command, transport, address);
                break;
            case "SetMaximumIncomingConnections":
                Change(() => peer.MaxIncomingConnections = (ushort)Int(0));
                Done();
                break;
            case "GetMaximumIncomingConnections":
                ReturnResult(peer.MaxIncomingConnections, command, transport, address);
                break;
            case "Connect":
                ReturnResult(Connect(peer, P(0), (ushort)Int(1), P(2), Int(3)), command, transport, address);
                break;
            case "Disconnect":
                Change(() => peer.ShutdownAsync(TimeSpan.FromMilliseconds((uint)Int(0)), (byte)Int(1)).AsTask().GetAwaiter().GetResult());
                Done();
                break;
            case "IsActive":
                ReturnResult(peer.IsActive, command, transport, address);
                break;
            case "GetConnectionList":
                SendConnectionList(peer, transport, address);
                break;
            case "CloseConnection":
                Change(() => peer.Close(Address(0), Int(1) != 0, (byte)Int(2)));
                Done();
                break;
            case "GetConnectionState":
                ReturnResult(ConnectionStateNumber(peer.GetConnectionState(Address(0))), command, transport, address);
                break;
            case "GetIndexFromSystemAddress":
                ReturnResult(ConnectionAt(peer, Address(0))?.SystemIndex ?? -1, command, transport, address);
                break;
            case "GetSystemAddressFromIndex":
                ReturnResult(peer.TryGetConnectionByIndex(Int(0), out var byIndex) ? byIndex.Address : SystemAddress.Unassigned,
                    command, transport, address);
                break;
            case "AddToBanList":
                Change(() => peer.BanList.Add(P(0) ?? "", TimeSpan.FromMilliseconds((uint)Int(1))));
                Done();
                break;
            case "RemoveFromBanList":
                Change(() => peer.BanList.Remove(P(0) ?? ""));
                Done();
                break;
            case "ClearBanList":
                Change(() => peer.BanList.Clear());
                Done();
                break;
            case "IsBanned":
                ReturnResult(peer.IsActive && peer.BanList.IsBanned(P(0) ?? ""), command, transport, address);
                break;
            case "Ping1":
                Change(() => ConnectionAt(peer, Address(0))?.Ping());
                Done();
                break;
            case "Ping2":
                Change(() =>
                {
                    if (Resolve(P(0)) is { } host)
                        peer.PingUnconnected(new SystemAddress(host, (ushort)Int(1)), Bool(2));
                });
                Done();
                break;
            case "GetAveragePing":
                ReturnResult(ConnectionAt(peer, Address(0)) is { } averaged ? Milliseconds(averaged.AveragePing, -1) : -1, command, transport, address);
                break;
            case "GetLastPing":
                ReturnResult(ConnectionAt(peer, Address(0)) is { } last ? Milliseconds(last.LastPing, ushort.MaxValue) : -1, command, transport, address);
                break;
            case "GetLowestPing":
                ReturnResult(ConnectionAt(peer, Address(0)) is { } lowest ? Milliseconds(lowest.LowestPing, ushort.MaxValue) : -1, command, transport, address);
                break;
            case "SetOccasionalPing":
                Change(() => peer.OccasionalPing = Bool(0));
                Done();
                break;
            case "SetOfflinePingResponse":
                // SetOfflinePingResponse asserts the length is under 400 (RakPeer.cpp:2271).
                Change(() => peer.OfflinePingResponse = Bytes(P(0), Int(1), 399));
                Done();
                break;
            case "GetInternalID":
                ReturnResult(peer.InternalAddresses.Count > 0 ? peer.InternalAddresses[0] : SystemAddress.Unassigned, command, transport, address);
                break;
            case "GetExternalID":
                ReturnResult(ExternalAddress(peer, Address(0)), command, transport, address);
                break;
            case "SetTimeoutTime":
                Change(() => SetTimeout(peer, (uint)Int(0), Address(1)));
                Done();
                break;
            case "GetMTUSize":
                // GetMTUSize(UNASSIGNED_SYSTEM_ADDRESS) is defaultMTUSize, the smallest MTU tried (RakPeer.cpp:206, 2572-2581).
                ReturnResult(RakNetConstants.MtuSizes[^1], command, transport, address);
                break;
            case "GetNumberOfAddresses":
                ReturnResult(peer.LocalAddresses.Count, command, transport, address);
                break;
            case "GetLocalIP":
                ReturnResult(LocalIp(peer, Int(0)), command, transport, address);
                break;
            case "AllowConnectionResponseIPMigration":
                Change(() => peer.AllowConnectionResponseIpMigration = Bool(0));
                Done();
                break;
            case "AdvertiseSystem":
                Change(() =>
                {
                    if (Resolve(P(0)) is { } host)
                        peer.AdvertiseSystem(new SystemAddress(host, (ushort)Int(1)), Bytes(P(2), Int(3), int.MaxValue).Span);
                });
                Done();
                break;
            case "SetIncomingPassword":
                // SetIncomingPassword keeps at most 255 bytes (RakPeer.cpp:938-955).
                Change(() => peer.IncomingPassword = Bytes(P(0), Int(1), 255));
                Done();
                break;
            case "GetIncomingPassword":
                var password = peer.IncomingPassword;
                if (password.Length > 0)
                    ReturnResult(ConsoleText.FromBytes(password.Span), command, transport, address);
                else
                    ReturnResult(0, command, transport, address);
                break;
        }
        return true;
    }

    // Startup (RakPeer.cpp:372-375, 419-422): RAKNET_ALREADY_STARTED on an active peer, INVALID_MAX_CONNECTIONS for 0;
    // otherwise a new peer.
    private int Startup(RakPeer peer, ushort maxConnections, ushort localPort, string? hostAddress)
    {
        if (peer.IsActive)
            return RakNetAlreadyStarted;
        if (maxConnections == 0)
            return InvalidMaxConnections;
        if (!IPAddress.TryParse(string.IsNullOrEmpty(hostAddress) ? "0.0.0.0" : hostAddress, out var bindAddress))
            return SocketFailedToBind;
        try
        {
            Peer = RakPeer.Start(new RakPeerOptions
            {
                MaxConnections = maxConnections,
                Bindings = [new SocketBinding(new IPEndPoint(bindAddress, localPort))],
            });
            return RakNetStarted;
        }
        catch (RakStartupException exception)
        {
            return StartupResultNumber(exception.Error);
        }
    }

    private static bool Connect(RakPeer peer, string? host, ushort port, string? password, int passwordLength)
    {
        if (!peer.IsActive || Resolve(host) is not { } address)
            return false;
        try
        {
            return peer.Connect(new SystemAddress(address, port), new ConnectOptions { Password = Bytes(password, passwordLength, 255) })
                   == ConnectAttemptResult.Started;
        }
        catch (ArgumentException)
        {
            // Port 0 and the other arguments RakPeer::Connect answers with INVALID_PARAMETER (RakPeer.cpp:993-1001).
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    // RakNetCommandParser.cpp:138-162: up to 32 systems.
    private static void SendConnectionList(RakPeer peer, IConsoleTransport transport, SystemAddress to)
    {
        if (!peer.IsActive)
        {
            transport.Send(to, "GetConnectionList() returned false.\r\n");
            return;
        }
        var connections = peer.Connections;
        if (connections.Count == 0)
        {
            transport.Send(to, "GetConnectionList() returned no systems connected.\r\n");
            return;
        }
        transport.Send(to, "GetConnectionList() returned:\r\n");
        for (var i = 0; i < Math.Min(connections.Count, 32); i++)
            transport.Send(to, string.Create(CultureInfo.InvariantCulture, $"{i} {connections[i].Address.ToString(writePort: true)}\r\n"));
    }

    private static RakConnection? ConnectionAt(RakPeer peer, SystemAddress address) =>
        peer.IsActive && peer.TryGetConnection(address, out var connection) ? connection : null;

    private static SystemAddress ExternalAddress(RakPeer peer, SystemAddress target)
    {
        if (target == SystemAddress.Unassigned)
            return peer.ExternalAddress;
        return ConnectionAt(peer, target)?.ExternalAddress ?? SystemAddress.Unassigned;
    }

    private static void SetTimeout(RakPeer peer, uint milliseconds, SystemAddress target)
    {
        // A timeout of 0 is refused by the API; RakNet would time every connection out at once.
        if (milliseconds == 0)
            return;
        var timeout = TimeSpan.FromMilliseconds(milliseconds);
        if (target == SystemAddress.Unassigned)
            peer.DefaultTimeout = timeout;
        else if (ConnectionAt(peer, target) is { } connection)
            connection.Timeout = timeout;
    }

    // GetLocalIP (RakPeer.cpp:2611-2621): ipList[index] as text, UNASSIGNED_SYSTEM_ADDRESS for an empty entry.
    private static string LocalIp(RakPeer peer, int index) =>
        (uint)index < (uint)peer.InternalAddresses.Count
            ? peer.InternalAddresses[index].ToString(writePort: false)
            : SystemAddress.Unassigned.ToString(writePort: false);

    // A slot's pings start at 65535, which RakNet returns raw before the first pong (RakPeer.cpp:3740-3748).
    private static int Milliseconds(TimeSpan? ping, int none) => ping is { } value ? (int)value.TotalMilliseconds : none;

    // SystemAddress(const char*): FromString with '|' on an address with port 0 (RakNetTypes.cpp:386-392).
    private static SystemAddress ToSystemAddress(string? text) =>
        text is not null && SystemAddress.TryParse(text, '|', out var address) ? address : SystemAddress.Unassigned;

    // RakNet resolves a host name where an address is not given (RakNetSocket2::DomainNameToIP).
    private static IPAddress? Resolve(string? host)
    {
        if (string.IsNullOrEmpty(host))
            return null;
        if (IPAddress.TryParse(host, out var address))
            return address.AddressFamily == AddressFamily.InterNetwork ? address : null;
        try
        {
            return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // A name the resolver refuses, such as one over 255 characters, resolves to nothing.
            return null;
        }
    }

    // passwordData and its length: RakNet copies the length given from the parameter's characters and its NUL; what lies
    // beyond them is undefined, so the bytes stop there.
    private static ReadOnlyMemory<byte> Bytes(string? text, int length, int maximum)
    {
        if (text is null || length <= 0)
            return ReadOnlyMemory<byte>.Empty;
        var bytes = ConsoleText.ToBytes(text + "\0");
        return bytes.AsMemory(0, Math.Min(Math.Min(length, bytes.Length), maximum));
    }

    private static void Change(Action change)
    {
        try
        {
            change();
        }
        catch (ObjectDisposedException)
        {
            // The peer has shut down; RakNet's setters on an inactive peer change nothing that matters.
        }
        catch (ArgumentException)
        {
            // Arguments the API refuses, where RakNet does nothing or only asserts and the parser still reports the call:
            // CloseConnection(UNASSIGNED_SYSTEM_ADDRESS), AdvertiseSystem with 399 bytes or more, an ordering channel of
            // 32 or more. A console user's line never escapes ConsoleServer.Update.
        }
    }

    // ConnectionState (RakNetTypes.h:69-85) as numbers.
    private static int ConnectionStateNumber(ConnectionState state) => state switch
    {
        ConnectionState.Pending => 0,
        ConnectionState.Connecting => 1,
        ConnectionState.Connected => 2,
        ConnectionState.Disconnecting => 3,
        ConnectionState.SilentlyDisconnecting => 4,
        ConnectionState.Disconnected => 5,
        _ => 6,
    };

    // StartupResult (RakNetTypes.h:41-55) as numbers.
    private static int StartupResultNumber(StartupError error) => error switch
    {
        StartupError.SocketFamilyNotSupported => 4,
        StartupError.SocketPortAlreadyInUse => 5,
        StartupError.SocketFailedToBind => 6,
        StartupError.SocketFailedTestSend => 7,
        StartupError.FailedToCreateNetworkThread => 9,
        _ => 11,
    };
}
