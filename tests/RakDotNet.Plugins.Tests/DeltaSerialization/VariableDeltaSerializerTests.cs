using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.DeltaSerialization;

// Expected bytes are printed by native RakNet: tools/interop-peer's deltaSerialize command runs the same Begin*, SerializeVariable
// and EndSerialize calls on one SerializationContext per command, as the ReplicaManager3 sample's Serialize does. Each
// variable is a changed bit, then the int big-endian when it changed (VariableListDeltaTracker.h:64-75).
public sealed class VariableDeltaSerializerTests : IDisposable
{
    private static readonly RakNetGuid Remote = new(7);
    private static readonly RakNetGuid Other = new(9);

    private readonly VariableDeltaSerializer _serializer = new();

    public void Dispose() => _serializer.Dispose();

    [Fact]
    public void The_sample_tick_sequence_matches_native_RakNet()
    {
        // ReplicaManager3 sample: an unreliable acked group and an identical group per Serialize.
        Assert.Equal(["80000002c0000001c0/66", "80000000c000000080/66"], Tick(5, 7, receipt: 1, 1, 2));
        Assert.Equal(["4000000200/34", "/0"], Tick(5, 8, receipt: 2, 1, 2));

        // ID_SND_RECEIPT_LOSS for the second tick's send: what it carried is sent again although unchanged.
        _serializer.OnMessageReceipt(Remote, 2, messageArrived: false);

        var context = new VariableDeltaSerializationContext();
        _serializer.OnPreSerializeTick();
        Assert.Equal("4000000200/34", Group(context, (c, s) => _serializer.BeginUnreliableAckedSerialize(c, Remote, s, 3), 5, 8));
        Assert.Equal("40000000c0/34", Group(context, (c, s) => _serializer.BeginIdenticalSerialize(c, false, s), 1, 3));
        // Reusing the context keeps its identical history, so a unique serialize on it repeats the identical bits
        // (VariableDeltaSerializer.cpp:78-109, VariableDeltaSerializer.h:159-176).
        Assert.Equal("40000000c0/34", Group(context, (c, s) => _serializer.BeginUniqueSerialize(c, Other, s), 4));

        // A first serialize to a new system writes every variable, then the bits of the tick's comparison.
        Assert.Equal(
            "80000000c0000000d000000030/100",
            Group(new VariableDeltaSerializationContext(), (c, s) => _serializer.BeginIdenticalSerialize(c, true, s), 1, 3));
    }

    [Fact]
    public void An_acknowledged_send_is_not_resent()
    {
        Assert.Equal("80000002c0000001c0/66", Acked(receipt: 1, 5, 7));

        _serializer.OnMessageReceipt(Remote, 1, messageArrived: true);

        Assert.Equal("/0", Acked(receipt: 2, 5, 7));
    }

    [Fact]
    public void A_lost_send_with_nothing_written_since_is_resent_whole()
    {
        Acked(receipt: 1, 5, 7);

        _serializer.OnMessageReceipt(Remote, 1, messageArrived: false);

        Assert.Equal("80000002c0000001c0/66", Acked(receipt: 2, 5, 7));
    }

    [Fact]
    public void An_acked_serialize_with_no_changes_writes_nothing_and_keeps_no_receipt()
    {
        Acked(receipt: 1, 5, 7);
        _serializer.OnMessageReceipt(Remote, 1, messageArrived: true);
        Acked(receipt: 2, 5, 7);

        // Nothing was stored for receipt 2, so its loss dirties nothing.
        _serializer.OnMessageReceipt(Remote, 2, messageArrived: false);

        Assert.Equal("/0", Acked(receipt: 3, 5, 7));
    }

    [Fact]
    public void Each_system_has_its_own_history_for_unique_serialization()
    {
        Assert.Equal("80000002c0000001c0/66", Unique(Remote, 5, 7));
        Assert.Equal("80000002c0000001c0/66", Unique(Other, 5, 7));
        Assert.Equal("/0", Unique(Remote, 5, 7));
        Assert.Equal("4000000200/34", Unique(Other, 5, 8));
    }

    [Fact]
    public void A_removed_system_starts_again_from_nothing()
    {
        Unique(Remote, 5, 7);

        _serializer.RemoveRemoteSystemVariableHistory(Remote);

        Assert.Equal("80000002c0000001c0/66", Unique(Remote, 5, 7));
    }

    [Fact]
    public void Identical_serialization_compares_once_per_tick_and_copies_the_result()
    {
        _serializer.OnPreSerializeTick();
        Assert.Equal("80000000c000000080/66", Identical(first: true, 1, 2));
        _serializer.OnPreSerializeTick();
        Assert.Equal("40000000c0/34", Identical(first: false, 1, 3));

        // The second system this tick gets the first one's bits, whatever values it is passed.
        Assert.Equal("40000000c0/34", Identical(first: false, 9, 9));
    }

    [Fact]
    public void Variables_read_back_as_written_and_unchanged_ones_keep_their_value()
    {
        var reader = new BitStreamReader(Convert.FromHexString("80000002c0000001e0"));
        int a = 0, b = 0, c = 42;

        Assert.True(VariableDeltaSerializer.DeserializeVariable(ref reader, ref a));
        Assert.True(VariableDeltaSerializer.DeserializeVariable(ref reader, ref b));
        Assert.False(VariableDeltaSerializer.DeserializeVariable(ref reader, ref c));

        Assert.Equal((5, 7, 42), (a, b, c));
    }

    [Fact]
    public void A_truncated_variable_reads_as_unchanged()
    {
        var reader = new BitStreamReader(Convert.FromHexString("8000"));
        var value = 3;

        Assert.False(VariableDeltaSerializer.DeserializeVariable(ref reader, ref value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void Booleans_and_custom_encodings_are_compared_by_their_serialized_bits()
    {
        var context = new VariableDeltaSerializationContext();
        using var stream = new BitStream();
        _serializer.BeginUniqueSerialize(context, Remote, stream);
        _serializer.SerializeVariable(context, true);
        _serializer.SerializeVariable(context, "ab", static (s, value) => s.WriteBytes(System.Text.Encoding.ASCII.GetBytes(value)));
        _serializer.EndSerialize(context);

        // 1, 1 (the bool); 1, 'a' 'b'.
        var reader = stream.CreateReader();
        var flag = false;
        string? text = null;
        Assert.True(VariableDeltaSerializer.DeserializeVariable(ref reader, ref flag));
        Assert.True(VariableDeltaSerializer.DeserializeVariable(ref reader, ref text, static (ref BitStreamReader r, out string? value) =>
        {
            Span<byte> bytes = stackalloc byte[2];
            value = r.TryReadBytes(bytes) ? System.Text.Encoding.ASCII.GetString(bytes) : null;
            return value is not null;
        }));
        Assert.True(flag);
        Assert.Equal("ab", text);
    }

    private string[] Tick(int unreliable1, int unreliable2, uint receipt, int reliable1, int reliable2)
    {
        var context = new VariableDeltaSerializationContext();
        _serializer.OnPreSerializeTick();
        return
        [
            Group(context, (c, s) => _serializer.BeginUnreliableAckedSerialize(c, Remote, s, receipt), unreliable1, unreliable2),
            Group(context, (c, s) => _serializer.BeginIdenticalSerialize(c, receipt == 1, s), reliable1, reliable2),
        ];
    }

    private string Acked(uint receipt, params int[] values) =>
        Group(new VariableDeltaSerializationContext(), (c, s) => _serializer.BeginUnreliableAckedSerialize(c, Remote, s, receipt), values);

    private string Unique(RakNetGuid system, params int[] values) =>
        Group(new VariableDeltaSerializationContext(), (c, s) => _serializer.BeginUniqueSerialize(c, system, s), values);

    private string Identical(bool first, params int[] values) =>
        Group(new VariableDeltaSerializationContext(), (c, s) => _serializer.BeginIdenticalSerialize(c, first, s), values);

    // One Begin, the variables and EndSerialize into a new stream, as "hex/bits".
    private string Group(VariableDeltaSerializationContext context, Action<VariableDeltaSerializationContext, BitStream> begin, params int[] values)
    {
        using var stream = new BitStream();
        begin(context, stream);
        foreach (var value in values)
            _serializer.SerializeVariable(context, value);
        _serializer.EndSerialize(context);
        return $"{Convert.ToHexStringLower(stream.Written)}/{stream.BitsWritten}";
    }
}
