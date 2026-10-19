using RakDotNet.Protocol.Wire;
using static RakDotNet.Protocol.Tests.Wire.NativeBits;

namespace RakDotNet.Protocol.Tests.Wire;

// Expected bytes are known answers printed by native RakNet 1a169895 with tools/bitstream-vectors. The language 7 tree
// is the one StringCompressor::GenerateTreeFromStrings builds from "aaaaaaaabbbbccd". Tests that register a language on
// StringCompressor.Instance live in this class only, so they never run in parallel with each other.
public class StringEncodingTests
{
    private static ReadOnlySpan<byte> SampleText => "aaaaaaaabbbbccd"u8;

    [Fact]
    public void WriteRakString_of_bytes_writes_them_unchanged()
    {
        // RakString.cpp:1218-1223: a RakString is a C string of bytes, not necessarily UTF-8.
        AssertWritten((ref BitStreamWriter w) => w.WriteRakString([0xe9, 0xff, 0x61]), 48, "800180e9ff61");

        var reader = Reader("800180e9ff61", 48);
        Assert.True(reader.TryReadRakStringBytes(out var bytes));
        Assert.Equal([0xe9, 0xff, 0x61], bytes.ToArray());
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void TryReadRakString_decodes_the_bytes_as_UTF8()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteRakString("hé"), 48, "80018068c3a9");
        var reader = Reader("80018068c3a9", 48);
        Assert.True(reader.TryReadRakString(out var text));
        Assert.Equal("hé", text);
    }

    [Fact]
    public void A_RakString_ends_at_its_first_NUL_byte()
    {
        // RakString.cpp:1220: the length is strlen, so a NUL ends the string on the wire.
        AssertWritten((ref BitStreamWriter w) => w.WriteRakString("ab\0c"u8), 40, "8001006162");
        AssertWritten((ref BitStreamWriter w) => w.WriteRakString("ab\0c"), 40, "8001006162");

        // RakString.cpp:1234-1253 keeps every byte read, and GetLength (RakString.cpp:385-388) is strlen, so a received
        // NUL ends the string; the bytes after it are still consumed.
        var reader = new BitStreamReader([0x00, 0x03, 0x61, 0x00, 0x62]);
        Assert.True(reader.TryReadRakStringBytes(out var bytes));
        Assert.Equal("a"u8.ToArray(), bytes.ToArray());
        Assert.Equal(0, reader.BitsRemaining);
        reader = new BitStreamReader([0x00, 0x03, 0x61, 0x00, 0x62]);
        Assert.True(reader.TryReadRakString(out var text));
        Assert.Equal("a", text);
    }

    [Fact]
    public void WriteCompressedString_stops_at_the_first_NUL_byte()
    {
        // StringCompressor.cpp:381-384 measure the input with strlen.
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedString("ab\0c", 256), 50, "8000000418f540");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedString("ab\0c"u8, 256), 50, "8000000418f540");
        // A null input writes a compressed 0 (StringCompressor.cpp:371-375), as an empty one does.
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedString(ReadOnlySpan<byte>.Empty, 256), 9, "f800");
    }

    [Fact]
    public void TryReadCompressedString_with_no_room_reads_nothing_and_succeeds()
    {
        // StringCompressor.cpp:477-482: the RakString overload clears the output and returns true.
        var reader = Reader("8000000418f540", 50);
        Assert.True(reader.TryReadCompressedString(0, out var text));
        Assert.Equal("", text);
        Assert.Equal(1, reader.BitPosition);
        Assert.True(reader.TryReadCompressedStringBytes(-1, out var bytes));
        Assert.Empty(bytes);
    }

    [Fact]
    public void TryReadCompressedStringBytes_keeps_bytes_that_are_not_UTF8()
    {
        Span<byte> buffer = stackalloc byte[32];
        var writer = new BitStreamWriter(buffer);
        writer.WriteCompressedString([0xe9, 0x41], 256);
        var reader = new BitStreamReader(writer.Written, writer.BitsWritten);
        Assert.True(reader.TryReadCompressedStringBytes(256, out var bytes));
        Assert.Equal([0xe9, 0x41], bytes);
    }

    [Fact]
    public void WriteRakWString_writes_a_count_then_each_UTF16_unit_big_endian()
    {
        // RakWString.cpp:285-311: WriteCasted<unsigned short>(wcslen), then each character as a u16, unaligned.
        AssertWritten((ref BitStreamWriter w) => w.WriteRakWString("hié中"), 81, "8002003400348074a71680");
        AssertWritten((ref BitStreamWriter w) => w.WriteRakWString(""), 17, "800000");

        var reader = Reader("8002003400348074a71680", 81);
        Assert.True(reader.TryReadRakWString(out var text));
        Assert.Equal("hié中", text);
        reader = Reader("800000", 17);
        Assert.True(reader.TryReadRakWString(out var empty));
        Assert.Equal("", empty);
    }

    [Fact]
    public void The_compressed_RakWString_is_the_plain_one_and_stops_at_a_NUL()
    {
        // BitStream.h:1359-1362: WriteCompressed(RakWString) is Serialize; wcslen ends the string at a NUL.
        AssertWritten((ref BitStreamWriter w) => w.WriteRakWString("hi\0x"), 49, "80010034003480");
    }

    [Fact]
    public void TryReadRakWString_fails_without_moving_when_truncated()
    {
        var reader = Reader("8002003400348074a71680", 60);
        Assert.False(reader.TryReadRakWString(out _));
        Assert.Equal(1, reader.BitPosition);
    }

    [Fact]
    public void WriteRakWString_rejects_more_than_65535_characters()
    {
        var buffer = new byte[16];
        Assert.Throws<ArgumentException>(() =>
        {
            var writer = new BitStreamWriter(buffer);
            writer.WriteRakWString(new string('a', 65536));
        });
    }

    [Fact]
    public void WriteCompressedRakString_can_write_the_language_id_first()
    {
        // RakString.cpp:1224-1232: a compressed u8 language id, then the Huffman data with a 0xFFFF limit.
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedRakString("hello", languageId: 0, writeLanguageId: true), 63, "c000000031812942");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedRakString("hello"), 58, "8000000630252840");

        var reader = Reader("c000000031812942", 63);
        Assert.True(reader.TryReadCompressedRakString(out var text, readLanguageId: true));
        Assert.Equal("hello", text);
    }

    [Fact]
    public void A_language_built_from_sample_strings_encodes_with_its_own_tree()
    {
        // StringCompressor.cpp:332-360: the tree comes from the byte frequencies of the sample.
        var compressor = new StringCompressor();
        compressor.GenerateTreeFromStrings(SampleText, 7);
        AssertWritten((ref BitStreamWriter w) => compressor.EncodeString(ref w, "abcd"u8, 256, 7), 66, "800000083ff3dd6fc0");

        var reader = Reader("800000083ff3dd6fc0", 66);
        Assert.True(compressor.TryDecodeString(ref reader, 256, out var text, 7));
        Assert.Equal("abcd"u8.ToArray(), text);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void An_unknown_language_writes_nothing_and_does_not_decode()
    {
        // StringCompressor.cpp:366-371 and 399-404.
        var compressor = new StringCompressor();
        AssertWritten((ref BitStreamWriter w) => compressor.EncodeString(ref w, "abcd"u8, 256, 9), 1, "80");
        var reader = Reader("800000083ff3dd6fc0", 66);
        Assert.False(compressor.TryDecodeString(ref reader, 256, out _, 9));
        Assert.Equal(1, reader.BitPosition);
    }

    [Fact]
    public void Generating_a_language_from_no_strings_removes_it()
    {
        // StringCompressor.cpp:335-347 deletes the old tree and returns before building a new one.
        var compressor = new StringCompressor();
        compressor.GenerateTreeFromStrings(SampleText, 7);
        compressor.GenerateTreeFromStrings([], 7);
        AssertWritten((ref BitStreamWriter w) => compressor.EncodeString(ref w, "abcd"u8, 256, 7), 1, "80");
    }

    [Fact]
    public void TryDecodeString_with_no_room_fails_as_the_char_buffer_overload_does()
    {
        // StringCompressor.cpp:405-406.
        var reader = Reader("8000000418f540", 50);
        Assert.False(StringCompressor.Instance.TryDecodeString(ref reader, 0, out _));
    }

    [Fact]
    public void The_RakString_encodings_use_the_languages_registered_on_the_shared_compressor()
    {
        // RakString::SerializeCompressed uses StringCompressor::Instance (RakString.cpp:1228-1232).
        StringCompressor.Instance.GenerateTreeFromStrings(SampleText, 7);
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedRakString("dcba", languageId: 7, writeLanguageId: true), 71, "dc00000040b7efe7fe");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedString("abcd", 256, languageId: 7), 66, "800000083ff3dd6fc0");

        var reader = Reader("dc00000040b7efe7fe", 71);
        Assert.True(reader.TryReadCompressedRakString(out var text, readLanguageId: true));
        Assert.Equal("dcba", text);
        reader = Reader("800000083ff3dd6fc0", 66);
        Assert.True(reader.TryReadCompressedString(256, out var abcd, languageId: 7));
        Assert.Equal("abcd", abcd);
    }
}
