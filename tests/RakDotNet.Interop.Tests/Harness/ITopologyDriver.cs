using System.Net;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives ConnectionGraph2 and FullyConnectedMesh2 on a peer: the cg2* and fcm2* commands of
/// docs/interop-protocol.md. Both drivers implement it; every command of a plugin but its attach command fails until it
/// is attached.
/// </summary>
public interface ITopologyDriver
{
    /// <summary>AttachPlugin of a ConnectionGraph2 after SetAutoProcessNewConnections.</summary>
    Task AttachConnectionGraph2Async(bool autoProcess = true, CancellationToken cancellationToken = default);

    /// <summary>ConnectionGraph2::SetAutoProcessNewConnections</summary>
    Task SetConnectionGraph2AutoProcessAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>ConnectionGraph2::AddParticipant with the connected system's address.</summary>
    Task AddConnectionGraph2ParticipantAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>GetParticipantList, GetConnectionListForRemoteSystem and GetPingBetweenSystems for each, and GetLowestAveragePingSystem.</summary>
    Task<ConnectionGraph2Info> GetConnectionGraph2InfoAsync(CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a FullyConnectedMesh2 configured with <paramref name="options"/>.</summary>
    Task AttachFullyConnectedMesh2Async(MeshOptions options, CancellationToken cancellationToken = default);

    /// <summary>FullyConnectedMesh2::AddParticipant</summary>
    Task AddMeshParticipantAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>FullyConnectedMesh2::ResetHostCalculation</summary>
    Task ResetHostCalculationAsync(CancellationToken cancellationToken = default);

    /// <summary>The host, participants and host order.</summary>
    Task<MeshInfo> GetMeshInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>FullyConnectedMesh2::StartVerifiedJoin</summary>
    Task StartVerifiedJoinAsync(ulong client, CancellationToken cancellationToken = default);

    /// <summary>FullyConnectedMesh2::RespondOnVerifiedJoinCapable for a received ID_FCM2_VERIFIED_JOIN_CAPABLE.</summary>
    Task RespondOnVerifiedJoinCapableAsync(ReceivedPacket packet, bool accept, ReadOnlyMemory<byte> additionalData = default, CancellationToken cancellationToken = default);

    /// <summary>FullyConnectedMesh2::GetVerifiedJoinRequiredProcessingList</summary>
    Task<IReadOnlyList<VerifiedJoinRequired>> GetVerifiedJoinRequiredAsync(ulong host, CancellationToken cancellationToken = default);

    /// <summary>FullyConnectedMesh2::GetVerifiedJoinAcceptedAdditionalData for a received ID_FCM2_VERIFIED_JOIN_ACCEPTED.</summary>
    Task<VerifiedJoinAcceptedInfo> GetVerifiedJoinAcceptedAsync(ReceivedPacket packet, CancellationToken cancellationToken = default);

    /// <summary>FullyConnectedMesh2::ConnectToRemoteNewIncomingConnections for a received ID_REMOTE_NEW_INCOMING_CONNECTION.</summary>
    Task ConnectToRemoteNewIncomingConnectionsAsync(ReceivedPacket packet, CancellationToken cancellationToken = default);
}

/// <param name="AutoParticipate">SetAutoparticipateConnections</param>
/// <param name="ConnectOnNewRemote">SetConnectOnNewRemoteConnection's flag</param>
/// <param name="Password">SetConnectOnNewRemoteConnection's password</param>
/// <param name="JoinStartUserData">Bytes WriteVJSUserData writes for every system; none when empty.</param>
/// <param name="JoinCapableUserData">Bytes WriteVJCUserData writes; none when empty.</param>
public sealed record MeshOptions(
    bool AutoParticipate = true,
    bool ConnectOnNewRemote = true,
    byte[]? Password = null,
    byte[]? JoinStartUserData = null,
    byte[]? JoinCapableUserData = null);

/// <summary>A participant of ConnectionGraph2 and what it reported being connected to.</summary>
public sealed record ConnectionGraph2Participant(ulong PeerGuid, IReadOnlyList<ConnectionGraph2Connection> Connections);

/// <summary>A reported connection: GUID, the address the participant sees it at, and GetPingBetweenSystems.</summary>
public sealed record ConnectionGraph2Connection(ulong PeerGuid, IPEndPoint Address, int Ping);

public sealed record ConnectionGraph2Info(IReadOnlyList<ConnectionGraph2Participant> Participants, ulong LowestAveragePingSystem);

/// <summary>
/// GetHostSystem, IsHostSystem, GetConnectedHost (unassigned as <see cref="ulong.MaxValue"/>), IsConnectedHost,
/// GetConnectedHostAddr (null when unassigned), GetParticipantList, GetHostOrder and GetTotalConnectionCount.
/// </summary>
public sealed record MeshInfo(
    ulong HostSystem,
    bool IsHostSystem,
    ulong ConnectedHost,
    bool IsConnectedHost,
    IPEndPoint? ConnectedHostAddress,
    IReadOnlyList<ulong> Participants,
    IReadOnlyList<ulong> HostOrder,
    uint TotalConnectionCount);

/// <summary>A system to connect to for a verified join, with the host's user data bytes and their bit count.</summary>
public sealed record VerifiedJoinRequired(ulong PeerGuid, IPEndPoint Address, byte[] UserData, int UserDataBits);

public sealed record VerifiedJoinAcceptedInfo(bool ThisSystemAccepted, IReadOnlyList<ulong> SystemsAccepted, byte[] AdditionalData);
