using System.Net;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// RakNet's external services pointed at servers the test runs: the emailSend, dynDns* and rackspace* commands of
/// docs/interop-protocol.md. Both drivers implement it. DynDNS and Rackspace connect to fixed hosts and ports, so each
/// request names where a host's connections go instead.
/// </summary>
public interface IServicesDriver
{
    /// <summary>EmailSender::Send with doPrintf false; null on success, otherwise RakNet's error text. Blocks until done.</summary>
    Task<string?> SendEmailAsync(EmailRequest request, CancellationToken cancellationToken = default);

    /// <summary>DynDNS::UpdateHostIPAsynch.</summary>
    Task StartDynDnsAsync(DynDnsRequest request, CancellationToken cancellationToken = default);

    /// <summary>One DynDNS::Update, then what it reports.</summary>
    Task<DynDnsStatus> UpdateDynDnsAsync(CancellationToken cancellationToken = default);

    /// <summary>A TCPInterface started with <see cref="RackspaceAuthentication.MaxConnections"/> on first use, then Rackspace::Authenticate.</summary>
    Task<IPEndPoint?> AuthenticateRackspaceAsync(RackspaceAuthentication request, CancellationToken cancellationToken = default);

    /// <summary>A Rackspace operation by its RakNet method name, with its arguments in order.</summary>
    Task RackspaceOperationAsync(string operation, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    /// <summary>One pass of Samples/RackspaceConsole's loop, then the callbacks made since the last poll.</summary>
    Task<RackspacePoll> PollRackspaceAsync(CancellationToken cancellationToken = default);
}

/// <summary>Connections the service makes to <see cref="Host"/> go to <see cref="To"/>.</summary>
public sealed record ServiceRedirect(string Host, IPEndPoint To);

public sealed record EmailAttachmentSpec(string Name, byte[] Data);

/// <summary>The arguments of EmailSender::Send; null strings are null pointers.</summary>
public sealed record EmailRequest(
    string Host,
    int Port,
    string? Sender,
    string? Recipient,
    string? SenderName,
    string? RecipientName,
    string? Subject,
    string Body,
    IReadOnlyList<EmailAttachmentSpec>? Attachments = null,
    string? Password = null);

public sealed record DynDnsRequest(string DnsHost, string? NewIp, string UsernameAndPassword, IReadOnlyList<ServiceRedirect> Redirects);

/// <summary>IsRunning, IsCompleted, and once completed the result code (DynDNS.h:26-58 as an integer), description and success; GetMyPublicIP.</summary>
public sealed record DynDnsStatus(bool Running, bool Completed, int? ResultCode, string? Description, bool? Successful, string MyPublicIp);

public sealed record RackspaceAuthentication(string Url, string User, string Key, IReadOnlyList<ServiceRedirect> Redirects, int MaxConnections = 1);

/// <summary>A result callback by name, its event type (Rackspace.h:36-54 as an integer) and EventTypeToString, and the text received.</summary>
public sealed record RackspaceResult(string Callback, int EventType, string EventName, byte[] Html);

/// <summary>OnConnectionAttemptFailure: the operation type (Rackspace.h:56-87 as an integer) and URL.</summary>
public sealed record RackspaceFailure(int OperationType, string Url);

public sealed record RackspacePoll(IReadOnlyList<RackspaceResult> Results, IReadOnlyList<RackspaceFailure> Failures);
