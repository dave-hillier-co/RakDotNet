using RakDotNet.Protocol.Wire;
using RakDotNet.Security;

namespace RakDotNet;

/// <summary>
/// The arguments of RakPeer::Startup and the initial runtime settings. Defaults are those of a RakNet release build,
/// so receipt-loss timing, timeout detection and admission match a native peer configured the same way.
/// </summary>
public sealed record RakPeerOptions
{
    /// <summary>maxConnections; must be positive, else <see cref="ArgumentOutOfRangeException"/>.</summary>
    public required int MaxConnections { get; init; }

    /// <summary>0 (RakPeer.cpp:208).</summary>
    public int MaxIncomingConnections { get; init; }

    /// <summary>The socket descriptors; may not be empty.</summary>
    public IReadOnlyList<SocketBinding> Bindings { get; init; } = [SocketBinding.AnyIPv4];

    public PeerThreading Threading { get; init; } = PeerThreading.Background;

    /// <summary>Every timer reads this; with <see cref="PeerThreading.Manual"/> and a fake provider, runs are deterministic.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The only I/O seam.</summary>
    public IDatagramTransportFactory Transport { get; init; } = UdpTransportFactory.Instance;

    /// <summary>A fixed GUID for tests; random by default, redrawn until neither 0 nor unassigned.</summary>
    public RakNetGuid? RakNetGuid { get; init; }

    /// <summary>GUIDs and other non-security randomness. Security randomness always comes from the system CSPRNG.</summary>
    public RandomSource Random { get; init; } = RandomSource.Shared;

    /// <summary>SetIncomingPassword; at most 255 bytes.</summary>
    public ReadOnlyMemory<byte> IncomingPassword { get; init; }

    /// <summary>SetOfflinePingResponse; at most 400 bytes (RakPeer.cpp:127).</summary>
    public ReadOnlyMemory<byte> OfflinePingResponse { get; init; }

    /// <summary>10 s, the release-build timeout (RakPeer.cpp:249).</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>1000 ms (RakPeer.cpp:238).</summary>
    public TimeSpan UnreliableTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>False (RakPeer.cpp:228).</summary>
    public bool OccasionalPing { get; init; }

    /// <summary>0, no ID_DOWNLOAD_PROGRESS (RakPeer.cpp:236).</summary>
    public int SplitMessageProgressInterval { get; init; }

    /// <summary>False (RakPeer.cpp:312).</summary>
    public bool LimitIpConnectionFrequency { get; init; }

    /// <summary>False (RakPeer.cpp:233).</summary>
    public bool AllowConnectionResponseIpMigration { get; init; }

    /// <summary>Bits per second; 0, unlimited (RakPeer.cpp:239).</summary>
    public long PerConnectionOutgoingBandwidthLimit { get; init; }

    /// <summary>
    /// InitializeSecurity; null is DisableSecurity. Fixed at start, as RakNet accepts it only while stopped
    /// (RakPeer.cpp:765-766).
    /// </summary>
    public ServerSecurity? Security { get; init; }

    /// <summary>The initial security exception list.</summary>
    public IReadOnlyList<string> SecurityExceptions { get; init; } = [];

    /// <summary>Attached during <see cref="RakPeer.Start"/>, before the loop exists; they get OnAttached then OnStartup.</summary>
    public IReadOnlyList<IRakPlugin> Plugins { get; init; } = [];

    /// <summary>ApplyNetworkSimulator at start; null is off.</summary>
    public NetworkSimulatorOptions? NetworkSimulator { get; init; }

    /// <summary>The hardening limits; <see cref="PeerLimits.Default"/> unless set.</summary>
    public PeerLimits Limits
    {
        get => field ?? PeerLimits.Default;
        init => field = value;
    }

    /// <summary>How often an idle async consumer is woken so plugin OnUpdate keeps running.</summary>
    public TimeSpan PluginUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Used by <see cref="RakPeer.DisposeAsync"/>.</summary>
    public TimeSpan ShutdownBlockDuration { get; init; } = TimeSpan.FromMilliseconds(100);
}
