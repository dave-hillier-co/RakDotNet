using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.DeltaSerialization;

public sealed class VariableListDeltaTrackerTests : IDisposable
{
    private readonly VariableListDeltaTracker _tracker = new();

    public void Dispose() => _tracker.Dispose();

    [Fact]
    public void A_variable_is_different_the_first_time_and_whenever_its_value_changes()
    {
        _tracker.StartWrite();
        Assert.True(_tracker.WriteVar(5));
        Assert.True(_tracker.WriteVar(7));
        Assert.True(_tracker.IsPastEndOfList);

        _tracker.StartWrite();
        Assert.False(_tracker.IsPastEndOfList);
        Assert.False(_tracker.WriteVar(5));
        Assert.True(_tracker.WriteVar(8));

        _tracker.StartWrite();
        Assert.False(_tracker.WriteVar(5));
        Assert.False(_tracker.WriteVar(8));
    }

    [Fact]
    public void WriteVarToBitstream_writes_a_changed_bit_then_the_value_only_when_changed()
    {
        using var stream = new BitStream();
        _tracker.StartWrite();
        _tracker.WriteVarToBitstream(0x01020304, stream);
        _tracker.StartWrite();
        _tracker.WriteVarToBitstream(0x01020304, stream);

        // VariableListDeltaTracker.h:64-75: 1, 01020304, then 0.
        Assert.Equal(34, stream.BitsWritten);
        Assert.Equal(Convert.FromHexString("8081018200"), stream.Written.ToArray());
    }

    [Fact]
    public void The_changed_bit_array_is_written_most_significant_bit_first()
    {
        using var stream = new BitStream();
        Span<byte> changed = [0xFF, 0xFF];
        _tracker.StartWrite();
        for (var i = 0; i < 9; i++)
            _tracker.WriteVarToBitstream(i, stream, changed, i);

        _tracker.StartWrite();
        Span<byte> second = [0xFF, 0xFF];
        for (var i = 0; i < 9; i++)
            _tracker.WriteVarToBitstream(i == 1 || i == 8 ? 100 : i, stream, second, i);

        // A byte is reset when its first bit is written (VariableListDeltaTracker.h:79-99).
        Assert.Equal([0xFF, 0x80], changed.ToArray());
        Assert.Equal(0x40, second[0]);
        Assert.Equal(0x80, second[1]);
    }

    [Fact]
    public void Flagged_variables_are_different_once_even_when_unchanged()
    {
        _tracker.StartWrite();
        _tracker.WriteVar(1);
        _tracker.WriteVar(2);
        _tracker.WriteVar(3);

        _tracker.FlagDirtyFromBitArray([0b1010_0000]);

        _tracker.StartWrite();
        Assert.True(_tracker.WriteVar(1));
        Assert.False(_tracker.WriteVar(2));
        Assert.True(_tracker.WriteVar(3));
        _tracker.StartWrite();
        Assert.False(_tracker.WriteVar(1));
        Assert.False(_tracker.WriteVar(2));
        Assert.False(_tracker.WriteVar(3));
    }

    [Fact]
    public void A_change_of_serialized_size_counts_as_different()
    {
        _tracker.StartWrite();
        Assert.True(_tracker.WriteVar(Bytes("a"), static (s, v) => s.WriteBytes(v)));
        _tracker.StartWrite();
        Assert.True(_tracker.WriteVar(Bytes("ab"), static (s, v) => s.WriteBytes(v)));
        _tracker.StartWrite();
        Assert.False(_tracker.WriteVar(Bytes("ab"), static (s, v) => s.WriteBytes(v)));
    }

    [Fact]
    public void A_change_of_size_clears_the_dirty_flag_of_the_next_variable_not_its_own()
    {
        // VariableListDeltaTracker.h:41-49 increments the index before clearing isDirty.
        _tracker.StartWrite();
        _tracker.WriteVar(Bytes("a"), static (s, v) => s.WriteBytes(v));
        _tracker.WriteVar(9);
        _tracker.FlagDirtyFromBitArray([0b1100_0000]);

        _tracker.StartWrite();
        Assert.True(_tracker.WriteVar(Bytes("ab"), static (s, v) => s.WriteBytes(v)));
        Assert.False(_tracker.WriteVar(9));

        _tracker.StartWrite();
        Assert.True(_tracker.WriteVar(Bytes("ab"), static (s, v) => s.WriteBytes(v)));
    }

    [Fact]
    public void ReadVarFromBitstream_reads_the_value_only_when_flagged()
    {
        var reader = new BitStreamReader(Convert.FromHexString("8081018200"), 34);
        var value = 0;

        Assert.True(VariableListDeltaTracker.ReadVarFromBitstream(ref value, ref reader));
        Assert.Equal(0x01020304, value);
        Assert.False(VariableListDeltaTracker.ReadVarFromBitstream(ref value, ref reader));
        Assert.Equal(0x01020304, value);
        Assert.False(VariableListDeltaTracker.ReadVarFromBitstream(ref value, ref reader));
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.ASCII.GetBytes(text);
}
