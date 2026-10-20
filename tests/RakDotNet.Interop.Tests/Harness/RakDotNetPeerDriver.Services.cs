using System.Net;
using System.Text;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// The emailSend, dynDns* and rackspace* commands on <see cref="EmailSender"/>, <see cref="DynDns"/> and
/// <see cref="Rackspace"/>, with interop-peer's results and errors (tools/interop-peer/src/ServiceCommands.cpp). A
/// redirect is applied as interop-peer's is: the host's connections go to its endpoint.
/// </summary>
public sealed partial class RakDotNetPeerDriver : IServicesDriver
{
    private const string CheckIpHost = "checkip.dyndns.org";
    private const string UpdateHost = "members.dyndns.org";

    private readonly Dictionary<string, IPEndPoint> _serviceRedirects = [];
    private DynDns? _dynDns;
    private bool _dynDnsStarted;
    private TcpInterface? _rackspaceTcp;
    private Rackspace? _rackspace;
    private RackspaceRecorder? _rackspaceRecorder;

    public Task<string?> SendEmailAsync(EmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Port is < 0 or > ushort.MaxValue)
            throw new PeerCommandException("emailSend", "field 'port' is out of range");
        var attachments = (request.Attachments ?? []).Select(a => new EmailAttachment(a.Name, a.Data)).ToList();
        return new EmailSender().SendAsync(request.Host, (ushort)request.Port, request.Sender, request.Recipient, request.SenderName,
            request.RecipientName, request.Subject, request.Body, attachments, null, request.Password, cancellationToken);
    }

    public Task StartDynDnsAsync(DynDnsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyRedirects(request.Redirects);
        _dynDns ??= new DynDns();
        _dynDns.CheckIpServer = Redirected(CheckIpHost, 80);
        _dynDns.UpdateServer = Redirected(UpdateHost, 80);
        _dynDns.UpdateHostIp(request.DnsHost, request.NewIp, request.UsernameAndPassword);
        _dynDnsStarted = true;
        return Task.CompletedTask;
    }

    public Task<DynDnsStatus> UpdateDynDnsAsync(CancellationToken cancellationToken = default)
    {
        if (_dynDns is not { } dynDns || !_dynDnsStarted)
            throw new PeerCommandException("dynDnsUpdate", "no DynDNS update; send dynDnsStart first");
        dynDns.Update();
        var completed = dynDns.IsCompleted;
        return Task.FromResult(new DynDnsStatus(
            dynDns.IsRunning,
            completed,
            completed ? (int)dynDns.CompletedResultCode : null,
            completed ? dynDns.CompletedDescription : null,
            completed ? dynDns.WasResultSuccessful : null,
            dynDns.MyPublicIp));
    }

    public Task<IPEndPoint?> AuthenticateRackspaceAsync(RackspaceAuthentication request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyRedirects(request.Redirects);
        if (_rackspace is null)
        {
            if (request.MaxConnections is < 1 or > ushort.MaxValue)
                throw new PeerCommandException("rackspaceAuthenticate", "field 'maxConnections' is out of range");
            _rackspaceTcp = new TcpInterface();
            _rackspaceTcp.Start(0, 0, (ushort)request.MaxConnections);
            _rackspace = new Rackspace { ServerEndPoint = host => Redirected(host, 443) };
            _rackspaceRecorder = new RackspaceRecorder();
            _rackspace.AddEventCallback(_rackspaceRecorder);
        }
        var address = _rackspace.Authenticate(_rackspaceTcp!, request.Url, request.User, request.Key);
        return Task.FromResult(address == SystemAddress.Unassigned ? null : address.ToIPEndPoint());
    }

    public Task RackspaceOperationAsync(string operation, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var r = _rackspace ?? throw new PeerCommandException("rackspaceOperation", "no Rackspace; send rackspaceAuthenticate first");
        string Arg(int index) => index < arguments.Count
            ? arguments[index]
            : throw new PeerCommandException("rackspaceOperation", $"operation '{operation}' needs more arguments");
        switch (operation)
        {
            case "ListServers": r.ListServers(); break;
            case "ListServersWithDetails": r.ListServersWithDetails(); break;
            case "CreateServer": r.CreateServer(Arg(0), Arg(1), Arg(2)); break;
            case "GetServerDetails": r.GetServerDetails(Arg(0)); break;
            case "UpdateServerNameOrPassword": r.UpdateServerNameOrPassword(Arg(0), Arg(1), Arg(2)); break;
            case "DeleteServer": r.DeleteServer(Arg(0)); break;
            case "ListServerAddresses": r.ListServerAddresses(Arg(0)); break;
            case "ShareServerAddress": r.ShareServerAddress(Arg(0), Arg(1)); break;
            case "DeleteServerAddress": r.DeleteServerAddress(Arg(0), Arg(1)); break;
            case "RebootServer": r.RebootServer(Arg(0), Arg(1)); break;
            case "RebuildServer": r.RebuildServer(Arg(0), Arg(1)); break;
            case "ResizeServer": r.ResizeServer(Arg(0), Arg(1)); break;
            case "ConfirmResizedServer": r.ConfirmResizedServer(Arg(0)); break;
            case "RevertResizedServer": r.RevertResizedServer(Arg(0)); break;
            case "ListFlavors": r.ListFlavors(); break;
            case "GetFlavorDetails": r.GetFlavorDetails(Arg(0)); break;
            case "ListImages": r.ListImages(); break;
            case "CreateImage": r.CreateImage(Arg(0), Arg(1)); break;
            case "GetImageDetails": r.GetImageDetails(Arg(0)); break;
            case "DeleteImage": r.DeleteImage(Arg(0)); break;
            case "ListSharedIPGroups": r.ListSharedIPGroups(); break;
            case "ListSharedIPGroupsWithDetails": r.ListSharedIPGroupsWithDetails(); break;
            case "CreateSharedIPGroup": r.CreateSharedIPGroup(Arg(0), Arg(1)); break;
            case "GetSharedIPGroupDetails": r.GetSharedIPGroupDetails(Arg(0)); break;
            case "DeleteSharedIPGroup": r.DeleteSharedIPGroup(Arg(0)); break;
            default: throw new PeerCommandException("rackspaceOperation", $"unknown operation '{operation}'");
        }
        return Task.CompletedTask;
    }

    public Task<RackspacePoll> PollRackspaceAsync(CancellationToken cancellationToken = default)
    {
        if (_rackspace is not { } rackspace)
            throw new PeerCommandException("rackspacePoll", "no Rackspace; send rackspaceAuthenticate first");
        var tcp = _rackspaceTcp!;
        while (tcp.Receive() is { } packet)
            rackspace.OnReceive(packet);
        var lost = tcp.HasLostConnection();
        if (lost != SystemAddress.Unassigned)
            rackspace.OnClosedConnection(lost);
        var recorder = _rackspaceRecorder!;
        var poll = new RackspacePoll([.. recorder.Results], [.. recorder.Failures]);
        recorder.Results.Clear();
        recorder.Failures.Clear();
        return Task.FromResult(poll);
    }

    private void ApplyRedirects(IReadOnlyList<ServiceRedirect> redirects)
    {
        foreach (var redirect in redirects)
            _serviceRedirects[redirect.Host] = redirect.To;
    }

    private DnsEndPoint Redirected(string host, int port) =>
        _serviceRedirects.TryGetValue(host, out var to) ? new DnsEndPoint(to.Address.ToString(), to.Port) : new DnsEndPoint(host, port);

    /// <summary>interop-peer's RackspaceRecorder: every result through ExecuteDefault, as Samples/RackspaceConsole prints them.</summary>
    private sealed class RackspaceRecorder : IRackspaceEventCallback
    {
        public List<RackspaceResult> Results { get; } = [];

        public List<RackspaceFailure> Failures { get; } = [];

        public void ExecuteDefault(string callbackName, RackspaceEventType eventType, string htmlAdditionalInfo) =>
            Results.Add(new RackspaceResult(callbackName, (int)eventType, Rackspace.EventTypeToString(eventType), Encoding.UTF8.GetBytes(htmlAdditionalInfo)));

        public void OnConnectionAttemptFailure(RackspaceOperationType operationType, string url) =>
            Failures.Add(new RackspaceFailure((int)operationType, url));
    }
}
