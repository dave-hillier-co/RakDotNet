using System.Net;
using System.Net.Sockets;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// DynDNS: updates a DynDNS host to this system's address over a <see cref="TcpInterface"/> of its own, one update at a
/// time (DynDNS.cpp). <see cref="UpdateHostIp"/> starts it and each <see cref="Update"/> moves it on.
/// </summary>
/// <remarks>
/// <para>
/// It first asks the check-IP server for this system's public address with "GET\n\n", and stops with
/// <see cref="DynDnsResultCode.DnsAlreadySet"/> if the host already resolves to it. Then, or a second after the check-IP
/// server last had a chance to reply, it sends the update request to the update server and reads the result from the
/// first packet that comes back.
/// </para>
/// <para>
/// <see cref="CheckIpServer"/> and <see cref="UpdateServer"/> are where RakNet connects, checkip.dyndns.org and
/// members.dyndns.org on port 80; the request's Host header names members.dyndns.org wherever it is sent. Text is
/// written as UTF-8 and replies are read up to their first zero byte.
/// </para>
/// </remarks>
public sealed class DynDns : IDisposable
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromMilliseconds(1000);

    // DynDNS.cpp:25-41, in RakNet's order, which is the order the codes are tried.
    private static readonly (string Description, string Code, DynDnsResultCode Result)[] ResultTable =
    [
        ("DNS update success.\nPlease wait up to 60 seconds for the change to take effect.\n", "good", DynDnsResultCode.Success),
        ("No change", "nochg", DynDnsResultCode.NoChange),
        ("Host has been blocked. You will need to contact DynDNS to reenable.", "abuse", DynDnsResultCode.Abuse),
        ("Useragent is blocked", "badagent", DynDnsResultCode.BadAgent),
        ("Username/password pair bad", "badauth", DynDnsResultCode.BadAuth),
        ("Bad system parameter", "badsys", DynDnsResultCode.BadSys),
        ("DNS inconsistency", "dnserr", DynDnsResultCode.DnsError),
        ("Paid account feature", "!donator", DynDnsResultCode.NotDonator),
        ("No such host in system", "nohost", DynDnsResultCode.NoHost),
        ("Invalid hostname format", "notfqdn", DynDnsResultCode.NotFqdn),
        ("Serious error", "numhost", DynDnsResultCode.NumHost),
        ("This host exists, but does not belong to you", "!yours", DynDnsResultCode.NotYours),
        ("911", "911", DynDnsResultCode.Error911),
    ];

    private readonly TimeProvider _time;
    private readonly long _origin;
    private TcpInterface? _tcp;
    private Phase _phase = Phase.Idle;
    private string _host = "";
    private byte[] _getString = [];
    private long _phaseTimeout;
    private SystemAddress _checkIpAddress = SystemAddress.Unassigned;

    public DynDns(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
    }

    private enum Phase
    {
        ConnectingToCheckIp,
        WaitingForCheckIpResponse,
        ConnectingToDynDns,
        WaitingForDynDnsResponse,
        Idle,
    }

    /// <summary>Where the public address is asked for: checkip.dyndns.org, port 80.</summary>
    public DnsEndPoint CheckIpServer { get; set; } = new("checkip.dyndns.org", 80);

    /// <summary>Where the update is sent: members.dyndns.org, port 80.</summary>
    public DnsEndPoint UpdateServer { get; set; } = new("members.dyndns.org", 80);

    /// <summary>
    /// RakNet's DomainNameToIP: the IPv4 address a name resolves to, or null. Used for the host being updated, and for
    /// a reported address that is not numeric. The system resolver unless replaced.
    /// </summary>
    public Func<string, IPAddress?> ResolveHost { get; init; } = ResolveIPv4;

    /// <summary>IsRunning: an update is in progress.</summary>
    public bool IsRunning => _phase != Phase.Idle;

    /// <summary>IsCompleted: no update is in progress.</summary>
    public bool IsCompleted => _phase == Phase.Idle;

    /// <summary>GetCompletedResultCode: how the last update ended.</summary>
    public DynDnsResultCode CompletedResultCode { get; private set; }

    /// <summary>GetCompletedDescription: RakNet's text for <see cref="CompletedResultCode"/>.</summary>
    public string? CompletedDescription { get; private set; }

    /// <summary>WasResultSuccessful: success, already set, or no change.</summary>
    public bool WasResultSuccessful => CompletedResultCode is DynDnsResultCode.Success or DynDnsResultCode.DnsAlreadySet or DynDnsResultCode.NoChange;

    /// <summary>
    /// GetMyPublicIP: the address the servers reported, as SystemAddress::ToString(false) writes it; empty until one
    /// is reported in the current update.
    /// </summary>
    public string MyPublicIp { get; private set; } = "";

    private long NowMs => (long)_time.GetElapsedTime(_origin).TotalMilliseconds;

    /// <summary>
    /// UpdateHostIPAsynch: starts updating <paramref name="dnsHost"/> to <paramref name="newIpAddress"/>, or to the
    /// address the update comes from when null. <paramref name="usernameAndPassword"/> is "username:password"
    /// (DynDNS.cpp:74-107).
    /// </summary>
    public void UpdateHostIp(string dnsHost, string? newIpAddress, string usernameAndPassword)
    {
        ArgumentNullException.ThrowIfNull(dnsHost);
        ArgumentNullException.ThrowIfNull(usernameAndPassword);
        MyPublicIp = "";
        _tcp ??= new TcpInterface(_time);
        _phase = Phase.Idle;
        _host = ConsoleText.CString(dnsHost);

        if (!_tcp.Start(0, 1))
        {
            SetCompleted(DynDnsResultCode.TcpFailedToStart, "TCP failed to start");
            return;
        }

        _phase = Phase.ConnectingToCheckIp;
        _tcp.Connect(CheckIpServer.Host, (ushort)CheckIpServer.Port, block: false);

        // https://www.dyndns.com/developers/specs/syntax.html, as RakNet writes it. The base64 text ends with its CRLF.
        var request = new StringBuilder("GET /nic/update?hostname=").Append(_host);
        if (newIpAddress is not null)
            request.Append("&myip=").Append(ConsoleText.CString(newIpAddress));
        request.Append("&wildcard=NOCHG&mx=NOCHG&backmx=NOCHG HTTP/1.0\n")
            .Append("Host: members.dyndns.org\n")
            .Append("Authorization: Basic ")
            .Append(Base64Encoder.Encode(Encoding.UTF8.GetBytes(ConsoleText.CString(usernameAndPassword))))
            .Append("User-Agent: Jenkins Software LLC - PC - 1.0\n\n");
        _getString = Encoding.UTF8.GetBytes(request.ToString());
    }

    /// <summary>Update: moves the update on; call it often while <see cref="IsRunning"/> (DynDNS.cpp:108-266).</summary>
    public void Update()
    {
        if (_phase == Phase.Idle)
            return;
        var tcp = _tcp!;

        if (tcp.HasFailedConnectionAttempt() != SystemAddress.Unassigned)
        {
            SetCompleted(DynDnsResultCode.TcpDidNotConnect, "Could not connect to DynDNS");
            return;
        }

        var serverAddress = tcp.HasCompletedConnectionAttempt();
        if (serverAddress != SystemAddress.Unassigned)
        {
            if (_phase == Phase.ConnectingToCheckIp)
            {
                _checkIpAddress = serverAddress;
                _phase = Phase.WaitingForCheckIpResponse;
                // "Needs 2 newlines! This is not documented and wasted a lot of my time" (DynDNS.cpp:126).
                tcp.Send("GET\n\n"u8, serverAddress, broadcast: false);
            }
            else
            {
                _phase = Phase.WaitingForDynDnsResponse;
                tcp.Send(_getString, serverAddress, broadcast: false);
            }
            _phaseTimeout = NowMs + (long)PhaseTimeout.TotalMilliseconds;
        }

        if (_phase == Phase.WaitingForCheckIpResponse && NowMs > _phaseTimeout)
        {
            _phase = Phase.ConnectingToDynDns;
            tcp.CloseConnection(_checkIpAddress);
            tcp.Connect(UpdateServer.Host, (ushort)UpdateServer.Port, block: false);
        }
        else if (_phase == Phase.WaitingForDynDnsResponse && NowMs > _phaseTimeout)
        {
            SetCompleted(DynDnsResultCode.DynDnsTimeout, "DynDNS did not respond");
            return;
        }

        if (tcp.Receive() is { } packet)
        {
            var text = Text(packet);
            if (_phase == Phase.WaitingForDynDnsResponse)
            {
                ReadUpdateResult(text);
                return;
            }

            // Any other packet is taken as the check-IP reply (DynDNS.cpp:209-253).
            const string currentIp = "Current IP Address: ";
            var index = text.IndexOf(currentIp, StringComparison.Ordinal);
            if (index >= 0)
            {
                MyPublicIp = AddressText(text[(index + currentIp.Length)..]);
                // Resolve the host being set; if it already has this address, DynDNS would count the update as abuse.
                var existingHost = ResolveHost(_host)?.ToString() ?? "";
                if (existingHost == MyPublicIp)
                {
                    SetCompleted(DynDnsResultCode.DnsAlreadySet, "No action needed");
                    return;
                }
            }

            tcp.CloseConnection(packet.Address);
            _phase = Phase.ConnectingToDynDns;
            tcp.Connect(UpdateServer.Host, (ushort)UpdateServer.Port, block: false);
        }

        // Any lost connection counts, whichever server it was (DynDNS.cpp:257-263).
        if (tcp.HasLostConnection() != SystemAddress.Unassigned && _phase == Phase.WaitingForDynDnsResponse)
            SetCompleted(DynDnsResultCode.ConnectionLostWithoutResponse, "Connection lost to DynDNS during GET operation");
    }

    public void Dispose()
    {
        _tcp?.Dispose();
        _tcp = null;
        _phase = Phase.Idle;
    }

    // DynDNS.cpp:144-201: the result code follows the "Connection: close" header and the white space after it.
    private void ReadUpdateResult(string text)
    {
        const string connectionClose = "Connection: close";
        var index = text.IndexOf(connectionClose, StringComparison.Ordinal);
        if (index < 0)
        {
            SetCompleted(DynDnsResultCode.ParsingFailure, "Parsing failure on returned string from DynDNS");
            return;
        }
        var result = index + connectionClose.Length;
        while (result < text.Length && text[result] is '\r' or '\n' or ' ')
            result++;
        var rest = text[result..];
        foreach (var (description, code, resultCode) in ResultTable)
        {
            if (!rest.StartsWith(code, StringComparison.Ordinal))
                continue;
            if (resultCode == DynDnsResultCode.Success)
            {
                // The address is read from the first digit on.
                var digit = rest.AsSpan().IndexOfAnyInRange('0', '9');
                if (digit >= 0)
                    MyPublicIp = AddressText(rest[digit..]);
            }
            SetCompleted(resultCode, description);
            return;
        }
        SetCompleted(DynDnsResultCode.UnknownResult, "DynDNS returned unknown result");
    }

    // SystemAddress::FromString then ToString(false), as RakNet's IPv4 build runs them on the rest of the packet: text
    // with a letter from g to z or A to Z that is not "localhost" is looked up as a host name, and an address that does
    // not resolve is UNASSIGNED_SYSTEM_ADDRESS.
    private string AddressText(string text)
    {
        if (SystemAddress.TryParse(text, SystemAddress.RakNetPortDelimiter, out var address))
            return address.ToString(false);
        return ResolveHost(text)?.ToString() ?? SystemAddress.Unassigned.ToString(false);
    }

    private void SetCompleted(DynDnsResultCode result, string description)
    {
        // Stop (DynDNS.cpp:65-71) frees the interface; the next update makes another.
        _tcp?.Dispose();
        _tcp = null;
        _phase = Phase.Idle;
        CompletedResultCode = result;
        CompletedDescription = description;
    }

    private static string Text(TcpPacket packet)
    {
        var data = packet.Data.Span;
        var end = data.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? data[..end] : data);
    }

    private static IPAddress? ResolveIPv4(string host)
    {
        try
        {
            return Array.Find(Dns.GetHostAddresses(host), a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return null;
        }
    }
}
