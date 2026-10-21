namespace RakDotNet.Plugins;

/// <summary>DynDnsResultCode (DynDNS.h:26-58), in RakNet's order.</summary>
public enum DynDnsResultCode
{
    /// <summary>RC_SUCCESS: the update was accepted.</summary>
    Success,

    /// <summary>RC_DNS_ALREADY_SET: the host already resolves to this system's public address, so nothing was sent.</summary>
    DnsAlreadySet,

    /// <summary>RC_NO_CHANGE: DynDNS saw nothing to change, which it counts as abuse.</summary>
    NoChange,

    /// <summary>RC_NOT_DONATOR: a paid account feature.</summary>
    NotDonator,

    /// <summary>RC_NO_HOST: the host does not exist.</summary>
    NoHost,

    /// <summary>RC_BAD_AUTH: wrong user name or password.</summary>
    BadAuth,

    /// <summary>RC_NOT_YOURS: the host belongs to another account.</summary>
    NotYours,

    /// <summary>RC_ABUSE: the host is blocked.</summary>
    Abuse,

    /// <summary>RC_TCP_FAILED_TO_START</summary>
    TcpFailedToStart,

    /// <summary>RC_TCP_DID_NOT_CONNECT: a server could not be reached.</summary>
    TcpDidNotConnect,

    /// <summary>RC_UNKNOWN_RESULT: a result code RakNet does not know.</summary>
    UnknownResult,

    /// <summary>RC_PARSING_FAILURE: no "Connection: close" header in the reply.</summary>
    ParsingFailure,

    /// <summary>RC_CONNECTION_LOST_WITHOUT_RESPONSE</summary>
    ConnectionLostWithoutResponse,

    /// <summary>RC_BAD_AGENT</summary>
    BadAgent,

    /// <summary>RC_BAD_SYS</summary>
    BadSys,

    /// <summary>RC_DNS_ERROR</summary>
    DnsError,

    /// <summary>RC_NOT_FQDN</summary>
    NotFqdn,

    /// <summary>RC_NUM_HOST</summary>
    NumHost,

    /// <summary>RC_911</summary>
    Error911,

    /// <summary>RC_DYNDNS_TIMEOUT: no reply within a second.</summary>
    DynDnsTimeout,
}
