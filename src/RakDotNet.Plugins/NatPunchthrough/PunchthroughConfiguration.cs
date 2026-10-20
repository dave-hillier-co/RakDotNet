namespace RakDotNet.Plugins;

/// <summary>
/// PunchthroughConfiguration (NatPunchthroughClient.h:47-104): the timing of a <see cref="NatPunchthroughClient"/>'s
/// attempts, RakNet's defaults included. Change it only while no punchthrough is in progress.
/// </summary>
/// <remarks>
/// RakNet's estimate for the defaults: internal, (15 ms * 2 tries + 30 wait) * 5 ports; external, (50 ms * 8 sends +
/// 200 wait) * 3 ports. Durations are whole milliseconds, as RakNet's are.
/// </remarks>
public sealed class PunchthroughConfiguration
{
    /// <summary>TIME_BETWEEN_PUNCH_ATTEMPTS_INTERNAL: between sends to one internal address.</summary>
    public TimeSpan TimeBetweenPunchAttemptsInternal { get; set; } = TimeSpan.FromMilliseconds(15);

    /// <summary>TIME_BETWEEN_PUNCH_ATTEMPTS_EXTERNAL: between sends to one external port.</summary>
    public TimeSpan TimeBetweenPunchAttemptsExternal { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>UDP_SENDS_PER_PORT_INTERNAL: sends to one internal address before the next.</summary>
    public int UdpSendsPerPortInternal { get; set; } = 2;

    /// <summary>UDP_SENDS_PER_PORT_EXTERNAL: sends to one external port before the next.</summary>
    public int UdpSendsPerPortExternal { get; set; } = 8;

    /// <summary>INTERNAL_IP_WAIT_AFTER_ATTEMPTS: the wait after the last internal address before the external ports.</summary>
    public TimeSpan InternalIpWaitAfterAttempts { get; set; } = TimeSpan.FromMilliseconds(30);

    /// <summary>MAX_PREDICTIVE_PORT_RANGE: external ports tried past the reported one.</summary>
    public int MaxPredictivePortRange { get; set; } = 2;

    /// <summary>EXTERNAL_IP_WAIT_BETWEEN_PORTS: the wait after giving up on one external port.</summary>
    public TimeSpan ExternalIpWaitBetweenPorts { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>EXTERNAL_IP_WAIT_AFTER_FIRST_TTL: the wait after the TTL send before the first punch to a port.</summary>
    public TimeSpan ExternalIpWaitAfterFirstTtl { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>EXTERNAL_IP_WAIT_AFTER_ALL_ATTEMPTS: the wait after the last port before ID_NAT_PUNCHTHROUGH_FAILED.</summary>
    public TimeSpan ExternalIpWaitAfterAllAttempts { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// MAXIMUM_NUMBER_OF_INTERNAL_IDS_TO_CHECK: internal addresses tried first, at most
    /// MAXIMUM_NUMBER_OF_INTERNAL_IDS (10); 0 skips LAN connects.
    /// </summary>
    public int MaximumNumberOfInternalIdsToCheck { get; set; } = 5;

    /// <summary>retryOnFailure: after a first failure, try once more before reporting it.</summary>
    public bool RetryOnFailure { get; set; }
}
