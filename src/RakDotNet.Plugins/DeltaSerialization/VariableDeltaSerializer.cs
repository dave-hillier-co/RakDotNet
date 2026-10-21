using System.Numerics;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// VariableDeltaSerializer::SerializationContext: the state of one group of <see cref="VariableDeltaSerializer"/> calls.
/// Create one per Serialize; it remembers the histories its first Begin calls chose.
/// </summary>
public sealed class VariableDeltaSerializationContext
{
    internal RakNetGuid RakNetGuid { get; set; }

    internal BitStream? Stream { get; set; }

    internal VariableDeltaSerializer.RemoteSystemHistory? History { get; set; }

    internal VariableDeltaSerializer.RemoteSystemHistory? IdenticalHistory { get; set; }

    internal VariableDeltaSerializer.RemoteSystemHistory? UniqueHistory { get; set; }

    internal VariableDeltaSerializer.ChangedVariables? Changed { get; set; }

    internal uint SendReceipt { get; set; }

    internal bool IsUnreliableAcked { get; set; }

    internal bool AnyVariablesWritten { get; set; }

    internal bool NewSystemSend { get; set; }
}

/// <summary>
/// VariableDeltaSerializer: writes only the variables that changed since they were last sent, per remote system or once
/// for all, and resends variables whose unreliable send was lost. Used from Replica3's Serialize.
/// </summary>
/// <remarks>
/// <para>
/// Ported from VariableDeltaSerializer.cpp. Call <see cref="BeginUnreliableAckedSerialize"/>,
/// <see cref="BeginUniqueSerialize"/> or <see cref="BeginIdenticalSerialize"/>, then
/// <see cref="SerializeVariable{T}(VariableDeltaSerializationContext, T)"/> for each variable in the same order every
/// time, then <see cref="EndSerialize"/>; the receiver calls
/// <see cref="DeserializeVariable{T}(ref BitStreamReader, ref T)"/> in the same order. Each variable is a changed bit,
/// then the value when it changed. When nothing changed the stream is reset, so the caller sends nothing.
/// </para>
/// <para>
/// Identical serialization compares once per tick (<see cref="OnPreSerializeTick"/>) and gives every later system that
/// tick the same bits. RakNet's behaviour is kept where it surprises: a context keeps the first identical or unique
/// history it was given, so a unique serialize on a context used for an identical one writes the identical bits
/// (VariableDeltaSerializer.cpp:78-109); and a first serialize to a new system writes every variable and then appends the
/// bits of the tick's comparison when one was made (VariableDeltaSerializer.cpp:98-107).
/// </para>
/// <para>
/// BeginDeserialize, EndDeserialize and AddRemoteSystemVariableHistory do nothing in RakNet and have no counterpart. Not
/// thread-safe. Dispose it to return its buffers to the pool.
/// </para>
/// </remarks>
public sealed class VariableDeltaSerializer : IDisposable
{
    private readonly List<RemoteSystemHistory> _histories = [];
    private readonly BitStream _identicalSerialization = new(64);
    private bool _didComparisonThisTick;

    /// <summary>Returns the buffers to the pool.</summary>
    public void Dispose()
    {
        _identicalSerialization.Dispose();
        foreach (var history in _histories)
            history.Tracker.Dispose();
        _histories.Clear();
    }

    /// <summary>
    /// BeginUnreliableAckedSerialize: variables for <paramref name="rakNetGuid"/> sent UNRELIABLE_WITH_ACK_RECEIPT with
    /// <paramref name="sendReceipt"/> (from <see cref="RakPeer.ReserveReceipt"/>), which
    /// <see cref="OnMessageReceipt"/> later reports.
    /// </summary>
    public void BeginUnreliableAckedSerialize(VariableDeltaSerializationContext context, RakNetGuid rakNetGuid, BitStream stream, uint sendReceipt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stream);
        if (!rakNetGuid.IsAssigned)
            throw new ArgumentException("The system must be assigned.", nameof(rakNetGuid));
        // VariableDeltaSerializer.cpp:32-46.
        context.AnyVariablesWritten = false;
        context.RakNetGuid = rakNetGuid;
        context.Stream = stream;
        context.UniqueHistory ??= StartVariableHistoryWrite(rakNetGuid);
        context.History = context.UniqueHistory;
        context.SendReceipt = sendReceipt;
        context.Changed = new ChangedVariables();
        context.NewSystemSend = false;
        context.IsUnreliableAcked = true;
    }

    /// <summary>BeginUniqueSerialize: variables whose values may differ per system, compared with what <paramref name="rakNetGuid"/> was last sent.</summary>
    public void BeginUniqueSerialize(VariableDeltaSerializationContext context, RakNetGuid rakNetGuid, BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stream);
        if (!rakNetGuid.IsAssigned)
            throw new ArgumentException("The system must be assigned.", nameof(rakNetGuid));
        // VariableDeltaSerializer.cpp:48-61.
        context.AnyVariablesWritten = false;
        context.RakNetGuid = rakNetGuid;
        context.Stream = stream;
        context.UniqueHistory ??= StartVariableHistoryWrite(rakNetGuid);
        context.History = context.UniqueHistory;
        context.NewSystemSend = false;
        context.IsUnreliableAcked = false;
    }

    /// <summary>
    /// BeginIdenticalSerialize: variables sent with the same values to every system. Pass
    /// <paramref name="isFirstSerializeToThisSystem"/> for the initial download, which writes every variable.
    /// </summary>
    public void BeginIdenticalSerialize(VariableDeltaSerializationContext context, bool isFirstSerializeToThisSystem, BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stream);
        // VariableDeltaSerializer.cpp:64-75.
        context.AnyVariablesWritten = false;
        context.RakNetGuid = RakNetGuid.Unassigned;
        context.Stream = stream;
        context.IsUnreliableAcked = false;
        context.IdenticalHistory ??= StartVariableHistoryWrite(RakNetGuid.Unassigned);
        context.History = context.IdenticalHistory;
        context.NewSystemSend = isFirstSerializeToThisSystem;
    }

    /// <summary>SerializeVariable: writes <paramref name="value"/> if it changed, after a changed bit.</summary>
    public void SerializeVariable<T>(VariableDeltaSerializationContext context, T value)
        where T : unmanaged, INumber<T>
    {
        var tracker = Tracker(context);
        tracker.Load(value);
        SerializeLoaded(context, tracker);
    }

    /// <inheritdoc cref="SerializeVariable{T}(VariableDeltaSerializationContext, T)"/>
    public void SerializeVariable(VariableDeltaSerializationContext context, bool value)
    {
        var tracker = Tracker(context);
        tracker.Load(value);
        SerializeLoaded(context, tracker);
    }

    /// <inheritdoc cref="SerializeVariable{T}(VariableDeltaSerializationContext, T)"/>
    public void SerializeVariable<T>(VariableDeltaSerializationContext context, T value, VariableWriter<T> write)
    {
        var tracker = Tracker(context);
        tracker.Load(value, write);
        SerializeLoaded(context, tracker);
    }

    /// <summary>EndSerialize: finishes the group; resets the stream when nothing was written.</summary>
    public void EndSerialize(VariableDeltaSerializationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stream = context.Stream ?? throw new InvalidOperationException("No Begin*Serialize call started this context.");
        // VariableDeltaSerializer.cpp:77-118.
        if (context.IsUnreliableAcked)
        {
            if (!context.AnyVariablesWritten)
            {
                stream.Reset();
                return;
            }
            StoreChangedVariables(context.History!, context.Changed!, context.SendReceipt);
            return;
        }
        if (context.IdenticalHistory is not null)
        {
            if (!_didComparisonThisTick)
            {
                _didComparisonThisTick = true;
                _identicalSerialization.Reset();
                if (!context.AnyVariablesWritten)
                {
                    stream.Reset();
                    return;
                }
                var written = stream.CreateReader();
                _identicalSerialization.Write(ref written);
            }
            else
            {
                var identical = _identicalSerialization.CreateReader();
                stream.Write(ref identical);
            }
        }
        else if (!context.AnyVariablesWritten)
        {
            stream.Reset();
        }
    }

    /// <summary>DeserializeVariable: reads a variable written by SerializeVariable; true when it changed.</summary>
    public static bool DeserializeVariable<T>(ref BitStreamReader reader, ref T value)
        where T : unmanaged, INumber<T> => VariableListDeltaTracker.ReadVarFromBitstream(ref value, ref reader);

    /// <inheritdoc cref="DeserializeVariable{T}(ref BitStreamReader, ref T)"/>
    public static bool DeserializeVariable(ref BitStreamReader reader, ref bool value) =>
        VariableListDeltaTracker.ReadVarFromBitstream(ref value, ref reader);

    /// <inheritdoc cref="DeserializeVariable{T}(ref BitStreamReader, ref T)"/>
    public static bool DeserializeVariable<T>(ref BitStreamReader reader, ref T value, VariableReader<T> read) =>
        VariableListDeltaTracker.ReadVarFromBitstream(ref value, ref reader, read);

    /// <summary>
    /// RemoveRemoteSystemVariableHistory: forgets what <paramref name="rakNetGuid"/> was sent, for a lost connection or a
    /// destroyed replica.
    /// </summary>
    public void RemoveRemoteSystemVariableHistory(RakNetGuid rakNetGuid)
    {
        var index = _histories.FindIndex(h => h.RakNetGuid == rakNetGuid);
        if (index < 0)
            return;
        // RemoveAtIndexFast.
        _histories[index].Tracker.Dispose();
        _histories[index] = _histories[^1];
        _histories.RemoveAt(_histories.Count - 1);
    }

    /// <summary>OnPreSerializeTick: call once per tick before the identical serializations of the tick.</summary>
    public void OnPreSerializeTick() => _didComparisonThisTick = false;

    /// <summary>
    /// OnMessageReceipt: on ID_SND_RECEIPT_ACKED forgets what the send carried; on ID_SND_RECEIPT_LOSS flags it to be
    /// sent again at the next serialize to <paramref name="rakNetGuid"/>.
    /// </summary>
    public void OnMessageReceipt(RakNetGuid rakNetGuid, uint receiptId, bool messageArrived)
    {
        // VariableDeltaSerializer.cpp:24-30, 145-182.
        var history = _histories.Find(h => h.RakNetGuid == rakNetGuid);
        if (history is null || !history.Updates.Remove(receiptId, out var changed))
            return;
        if (!messageArrived)
            history.Tracker.FlagDirtyFromBitArray(changed.Bits);
    }

    private static VariableListDeltaTracker Tracker(VariableDeltaSerializationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.History ?? throw new InvalidOperationException("No Begin*Serialize call started this context.")).Tracker;
    }

    // SerializeVariable (VariableDeltaSerializer.h:159-196) on the value the tracker has loaded.
    private void SerializeLoaded(VariableDeltaSerializationContext context, VariableListDeltaTracker tracker)
    {
        var stream = context.Stream!;
        if (context.NewSystemSend)
        {
            if (!tracker.IsPastEndOfList)
            {
                // Sent to another system before: written whole, without being remembered.
                stream.WriteBit(true);
                tracker.CopyLoadedTo(stream);
            }
            else
            {
                tracker.WriteLoadedToBitstream(stream);
            }
            context.AnyVariablesWritten = true;
        }
        else if (context.IsUnreliableAcked)
        {
            var changed = context.Changed!;
            if (changed.WriteIndex >= VariableListDeltaTracker.MaxTrackedVariables)
                throw new InvalidOperationException($"At most {VariableListDeltaTracker.MaxTrackedVariables} variables fit one unreliable acked serialize.");
            context.AnyVariablesWritten |= tracker.WriteLoadedToBitstream(stream, changed.Bits, changed.WriteIndex++);
        }
        else if (context.IdenticalHistory is not null)
        {
            // Compared once per tick; later systems get the stored bits at EndSerialize.
            if (!_didComparisonThisTick)
                context.AnyVariablesWritten |= tracker.WriteLoadedToBitstream(stream);
        }
        else
        {
            context.AnyVariablesWritten |= tracker.WriteLoadedToBitstream(stream);
        }
    }

    // StartVariableHistoryWrite (VariableDeltaSerializer.cpp:233-252).
    private RemoteSystemHistory StartVariableHistoryWrite(RakNetGuid rakNetGuid)
    {
        var history = _histories.Find(h => h.RakNetGuid == rakNetGuid);
        if (history is null)
        {
            history = new RemoteSystemHistory(rakNetGuid);
            _histories.Add(history);
        }
        history.Tracker.StartWrite();
        return history;
    }

    // StoreChangedVariablesList: a second send with the same receipt is dropped, as OrderedList::Insert refuses a
    // duplicate (DS_OrderedList.h:174-187).
    private static void StoreChangedVariables(RemoteSystemHistory history, ChangedVariables changed, uint sendReceipt) =>
        history.Updates.TryAdd(sendReceipt, changed);

    // RemoteSystemVariableHistory: the last values sent to one system, and the variables each unreliable send carried.
    internal sealed class RemoteSystemHistory(RakNetGuid rakNetGuid)
    {
        public RakNetGuid RakNetGuid { get; } = rakNetGuid;

        public VariableListDeltaTracker Tracker { get; } = new();

        public SortedList<uint, ChangedVariables> Updates { get; } = [];
    }

    // ChangedVariablesList: one bit per variable written, set when it changed.
    internal sealed class ChangedVariables
    {
        public byte[] Bits { get; } = new byte[VariableListDeltaTracker.MaxTrackedVariables / 8];

        public int WriteIndex { get; set; }
    }
}
