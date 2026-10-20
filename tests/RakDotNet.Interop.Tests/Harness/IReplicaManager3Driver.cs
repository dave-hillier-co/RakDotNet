namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives a ReplicaManager3 with the replicas of Samples/ReplicaManager3 and the User of
/// Samples/ReplicatedLogin: the rm3* commands of docs/interop-protocol.md. Every command but
/// <see cref="AttachReplicaManager3Async"/> fails until one is attached.
/// </summary>
public interface IReplicaManager3Driver
{
    /// <summary>
    /// AttachPlugin of the sample's ReplicaManager3 for <paramref name="topology"/> (CLIENT, SERVER or P2P), with its own
    /// NetworkIDManager, SetAutoSerializeInterval (negative is never), and connections that group download messages when
    /// asked, as the sample's do; plus the sample's handling of send receipts.
    /// </summary>
    Task AttachReplicaManager3Async(string topology, int autoSerializeMs = 30, bool groupDownloads = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reference of a new replica of each sample class of <paramref name="kinds"/>, or User, all in one update as the
    /// sample's 'C' does; returns their NetworkIDs.
    /// </summary>
    Task<IReadOnlyList<ulong>> CreateReplicasAsync(IReadOnlyList<string> kinds, string? username = null, ulong? userGuid = null, CancellationToken cancellationToken = default);

    /// <summary>Sets a replica's variables; null leaves one as it is.</summary>
    Task SetReplicaAsync(ulong networkId, ReplicaValues values, CancellationToken cancellationToken = default);

    /// <summary>The sample's 'D': BroadcastDestructionList of the replicas this system created, then deletes them.</summary>
    Task<int> DestroyMyReplicasAsync(CancellationToken cancellationToken = default);

    /// <summary>ReplicatedLogin's DeleteUserWithGuid: BroadcastDestruction, Dereference and delete.</summary>
    Task DestroyReplicaAsync(ulong networkId, CancellationToken cancellationToken = default);

    /// <summary>The referenced replicas, the connections and the destructions received.</summary>
    Task<ReplicaManager3Info> GetReplicaManager3InfoAsync(CancellationToken cancellationToken = default);
}

/// <param name="Var1">var1Unreliable of a sample replica.</param>
/// <param name="Score">A User's score.</param>
public sealed record ReplicaValues(int? Var1 = null, int? Var2 = null, int? Var3 = null, int? Var4 = null, int? Score = null, string? Username = null);

/// <summary>A replica as rm3Info reports it: the sample variables and construction text, or a User's fields.</summary>
public sealed record ReplicaState(
    ulong NetworkId,
    string Kind,
    ulong CreatingGuid,
    int? Var1 = null,
    int? Var2 = null,
    int? Var3 = null,
    int? Var4 = null,
    string? ConstructionText = null,
    int? Score = null,
    string? Username = null,
    ulong? UserGuid = null);

/// <param name="Constructed">GetConstructedReplicas, in reference order.</param>
public sealed record ReplicaConnectionState(ulong PeerGuid, bool Validated, bool DownloadComplete, IReadOnlyList<ulong> Constructed);

/// <summary>A destruction DeserializeDestruction read: its text (empty for a User) and the deleting system.</summary>
public sealed record ReplicaDestruction(ulong NetworkId, string Kind, string Text, ulong DeletingGuid);

public sealed record ReplicaManager3Info(
    IReadOnlyList<ReplicaState> Replicas,
    IReadOnlyList<ReplicaConnectionState> Connections,
    IReadOnlyList<ReplicaDestruction> Destructions,
    bool AllDownloadsCompleted);
