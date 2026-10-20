using System.Net;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Puts a peer behind a <see cref="NatRouter"/> and drives the NAT plugins on it: the nat* commands of
/// docs/interop-protocol.md. Both drivers implement it; every plugin command but the attach commands fails until that
/// plugin is attached. Results arrive as packets.
/// </summary>
public interface INatDriver
{
    /// <summary>
    /// natGateway: from now on the peer reaches everything through <paramref name="gateway"/>, a router's
    /// <see cref="NatRouter.GatewayEndPoint"/>; null leaves the router. A RakDotNet peer takes it only before startup.
    /// </summary>
    Task SetNatGatewayAsync(IPEndPoint? gateway, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a NatTypeDetectionClient.</summary>
    Task AttachNatTypeDetectionClientAsync(CancellationToken cancellationToken = default);

    /// <summary>DetectNATType with the connected server at <paramref name="server"/>.</summary>
    Task DetectNatTypeAsync(IPEndPoint server, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a NatTypeDetectionServer.</summary>
    Task AttachNatTypeDetectionServerAsync(CancellationToken cancellationToken = default);

    /// <summary>Startup on three more addresses; returns where S1P2, S2P3, S3P4 and S4P5 were bound.</summary>
    Task<IReadOnlyList<IPEndPoint>> StartNatTypeDetectionServerAsync(string ip2, string ip3, string ip4, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a NatPunchthroughServer.</summary>
    Task AttachNatPunchthroughServerAsync(CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a NatPunchthroughClient.</summary>
    Task AttachNatPunchthroughClientAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the fields of the client's PunchthroughConfiguration that <paramref name="settings"/> sets.</summary>
    Task ConfigureNatPunchthroughClientAsync(PunchthroughSettings settings, CancellationToken cancellationToken = default);

    /// <summary>OpenNAT to the system with <paramref name="target"/>'s GUID through the facilitator at <paramref name="facilitator"/>.</summary>
    Task<bool> OpenNatAsync(ulong target, IPEndPoint facilitator, CancellationToken cancellationToken = default);

    /// <summary>FindRouterPortStride with the facilitator at <paramref name="facilitator"/>.</summary>
    Task FindRouterPortStrideAsync(IPEndPoint facilitator, CancellationToken cancellationToken = default);
}

/// <summary>PunchthroughConfiguration fields to change; null leaves a field as it is. Durations are milliseconds.</summary>
public sealed record PunchthroughSettings(
    int? TimeBetweenPunchAttemptsInternal = null,
    int? TimeBetweenPunchAttemptsExternal = null,
    int? UdpSendsPerPortInternal = null,
    int? UdpSendsPerPortExternal = null,
    int? InternalIpWaitAfterAttempts = null,
    int? MaxPredictivePortRange = null,
    int? ExternalIpWaitBetweenPorts = null,
    int? ExternalIpWaitAfterFirstTtl = null,
    int? ExternalIpWaitAfterAllAttempts = null,
    int? MaximumNumberOfInternalIdsToCheck = null,
    bool? RetryOnFailure = null
);
