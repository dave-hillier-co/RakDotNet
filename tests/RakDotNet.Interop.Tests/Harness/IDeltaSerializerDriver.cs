using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>Drives a VariableDeltaSerializer of int variables: the delta* commands of docs/interop-protocol.md.</summary>
public interface IDeltaSerializerDriver
{
    /// <summary>
    /// Runs each group's Begin*Serialize, SerializeVariable of each value and EndSerialize on one SerializationContext,
    /// after OnPreSerializeTick when asked; a group with a system to send to is sent as its message ID then its bits.
    /// </summary>
    Task<IReadOnlyList<DeltaGroupResult>> SerializeDeltaAsync(IReadOnlyList<DeltaGroup> groups, bool preSerializeTick = false, CancellationToken cancellationToken = default);

    /// <summary>OnPreSerializeTick</summary>
    Task DeltaPreSerializeTickAsync(CancellationToken cancellationToken = default);

    /// <summary>OnMessageReceipt</summary>
    Task DeltaReceiptAsync(ulong target, uint receipt, bool arrived, CancellationToken cancellationToken = default);

    /// <summary>RemoveRemoteSystemVariableHistory</summary>
    Task RemoveDeltaSystemAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>DeserializeVariable <paramref name="count"/> times over the bytes: the value where it changed, else null.</summary>
    Task<IReadOnlyList<int?>> DeserializeDeltaAsync(ReadOnlyMemory<byte> data, int count, CancellationToken cancellationToken = default);
}

/// <summary>One Begin*Serialize group. <paramref name="Mode"/> is UNRELIABLE_ACKED, UNIQUE or IDENTICAL.</summary>
/// <param name="Target">The system for UNRELIABLE_ACKED and UNIQUE.</param>
/// <param name="Receipt">For UNRELIABLE_ACKED without <paramref name="SendTo"/>; with it, IncrementNextSendReceipt gives it.</param>
/// <param name="First">BeginIdenticalSerialize's first-serialize flag.</param>
/// <param name="SendTo">Sends the group, when it wrote anything, as <paramref name="MessageId"/> then its bits.</param>
/// <param name="Reliability">The send's reliability; by default UNRELIABLE_WITH_ACK_RECEIPT for UNRELIABLE_ACKED, else RELIABLE_ORDERED.</param>
public sealed record DeltaGroup(
    string Mode,
    IReadOnlyList<int> Values,
    ulong? Target = null,
    uint Receipt = 0,
    bool First = false,
    ulong? SendTo = null,
    byte MessageId = (byte)Protocol.Wire.MessageId.UserPacketEnum,
    Reliability? Reliability = null);

public sealed record DeltaGroupResult(byte[] Data, int Bits, uint Receipt, bool Sent);
