// Prints the bytes native RakNet's BitStream, string, StringTable and SystemAddress code produces, for the known-answer
// tests in tests/RakDotNet.Protocol.Tests/Wire. Every BitStream case starts with one 1 bit so the encoding is checked
// off a byte boundary. Build and run with build.sh.
#include <stdio.h>
#include <string.h>
#include <math.h>
#define private public
#define protected public
#include "DS_HuffmanEncodingTree.h"
#undef private
#undef protected
#include "BitStream.h"
#include "RakString.h"
#include "StringCompressor.h"
#include "RakWString.h"
#include "StringTable.h"
#include "RakNetTypes.h"

using namespace RakNet;

static void dump(const char* name, BitStream& bs)
{
    printf("%s bits=%u hex=", name, (unsigned)bs.GetNumberOfBitsUsed());
    for (unsigned i = 0; i < bs.GetNumberOfBytesUsed(); i++) printf("%02x", bs.GetData()[i]);
    printf("\n");
}

#define CASE(name, body) { BitStream bs; bs.Write1(); body; dump(name, bs); }

extern unsigned int englishCharacterFrequencies[256];

int main()
{
    // Compressed integers, each after one leading 1 bit.
    CASE("cu8_0", bs.WriteCompressed((uint8_t)0));
    CASE("cu8_5", bs.WriteCompressed((uint8_t)5));
    CASE("cu8_ab", bs.WriteCompressed((uint8_t)0xab));
    CASE("ci8_m1", bs.WriteCompressed((int8_t)-1));
    CASE("cu16_0", bs.WriteCompressed((uint16_t)0));
    CASE("cu16_1", bs.WriteCompressed((uint16_t)1));
    CASE("cu16_0100", bs.WriteCompressed((uint16_t)0x0100));
    CASE("cu16_0500", bs.WriteCompressed((uint16_t)0x0500));
    CASE("cu16_1234", bs.WriteCompressed((uint16_t)0x1234));
    CASE("ci16_m1", bs.WriteCompressed((int16_t)-1));
    CASE("cu32_0", bs.WriteCompressed((uint32_t)0));
    CASE("cu32_1", bs.WriteCompressed((uint32_t)1));
    CASE("cu32_01000000", bs.WriteCompressed((uint32_t)0x01000000));
    CASE("cu32_12000000", bs.WriteCompressed((uint32_t)0x12000000));
    CASE("cu32_12345678", bs.WriteCompressed((uint32_t)0x12345678));
    CASE("ci32_m1", bs.WriteCompressed((int32_t)-1));
    CASE("ci32_100", bs.WriteCompressed((int32_t)100));
    CASE("cu64_0", bs.WriteCompressed((uint64_t)0));
    CASE("cu64_0300000000000000", bs.WriteCompressed((uint64_t)0x0300000000000000ULL));
    CASE("cu64_123456789abcdef0", bs.WriteCompressed((uint64_t)0x123456789abcdef0ULL));
    // Integer ranges.
    CASE("r16_500_0_1000", bs.WriteBitsFromIntegerRange((uint16_t)500, (uint16_t)0, (uint16_t)1000));
    CASE("r16_7_5_20", bs.WriteBitsFromIntegerRange((uint16_t)7, (uint16_t)5, (uint16_t)20));
    CASE("r32_m3_m10_10", bs.WriteBitsFromIntegerRange((int32_t)-3, (int32_t)-10, (int32_t)10));
    CASE("r8_3_0_3", bs.WriteBitsFromIntegerRange((uint8_t)3, (uint8_t)0, (uint8_t)3));
    CASE("r32_12345_0_fffff", bs.WriteBitsFromIntegerRange((uint32_t)0x12345, (uint32_t)0, (uint32_t)0xfffff));
    CASE("r16_out_2000_0_1000", bs.WriteBitsFromIntegerRange((uint16_t)2000, (uint16_t)0, (uint16_t)1000, true));
    CASE("r16_in_500_0_1000", bs.WriteBitsFromIntegerRange((uint16_t)500, (uint16_t)0, (uint16_t)1000, true));
    // Float16.
    CASE("f16_0.5_m1_1", bs.WriteFloat16(0.5f, -1.0f, 1.0f));
    CASE("f16_123.4_0_1000", bs.WriteFloat16(123.4f, 0.0f, 1000.0f));
    CASE("f16_m1_m1_1", bs.WriteFloat16(-1.0f, -1.0f, 1.0f));
    CASE("f16_1_m1_1", bs.WriteFloat16(1.0f, -1.0f, 1.0f));
    // Vectors and rotations.
    CASE("normvec_0.6_m0.8_0", bs.WriteNormVector(0.6f, -0.8f, 0.0f));
    CASE("vec_3_m4_12", bs.WriteVector(3.0f, -4.0f, 12.0f));
    CASE("vec_0_0_0", bs.WriteVector(0.0f, 0.0f, 0.0f));
    CASE("quat_0.7071_0_0.7071_0", bs.WriteNormQuat(0.70710678f, 0.0f, 0.70710678f, 0.0f));
    CASE("quat_m0.5_0.5_m0.5_0.5", bs.WriteNormQuat(-0.5f, 0.5f, -0.5f, 0.5f));
    CASE("orth_identity", bs.WriteOrthMatrix(1.0f,0.0f,0.0f, 0.0f,1.0f,0.0f, 0.0f,0.0f,1.0f));
    CASE("orth_rotz90", bs.WriteOrthMatrix(0.0f,-1.0f,0.0f, 1.0f,0.0f,0.0f, 0.0f,0.0f,1.0f));
    // Delta and casts.
    CASE("delta_5_5", bs.WriteDelta((uint32_t)5, (uint32_t)5));
    CASE("delta_6_5", bs.WriteDelta((uint32_t)6, (uint32_t)5));
    CASE("cast_u8_53", bs.WriteCasted<uint8_t>((int)53));
    CASE("cast_i16_m2", bs.WriteCasted<int16_t>((int)-2));
    // Strings.
    CASE("rakstring_hello", RakString::Serialize("hello", &bs));
    CASE("rakstring_empty", RakString::Serialize("", &bs));
    StringCompressor::AddReference();
    CASE("huff_hello_world", StringCompressor::Instance()->EncodeString("Hello, World!", 256, &bs));
    CASE("huff_empty", StringCompressor::Instance()->EncodeString("", 256, &bs));
    CASE("huff_abcdef_max4", StringCompressor::Instance()->EncodeString("abcdef", 4, &bs));
    CASE("rakstring_compressed_hello", RakString("hello").SerializeCompressed(&bs, 0, false));
    // Compressed floats and doubles (values between -1 and 1, clamped).
    CASE("cf_0.5", bs.WriteCompressed(0.5f));
    CASE("cf_m1", bs.WriteCompressed(-1.0f));
    CASE("cf_1", bs.WriteCompressed(1.0f));
    CASE("cf_1.005", bs.WriteCompressed(1.005f));
    CASE("cf_m0.3", bs.WriteCompressed(-0.3f));
    CASE("cd_0.5", bs.WriteCompressed(0.5));
    CASE("cd_m1", bs.WriteCompressed(-1.0));
    CASE("cd_1", bs.WriteCompressed(1.0));
    CASE("cd_m0.3", bs.WriteCompressed(-0.3));
    CASE("cbool_true", bs.WriteCompressed(true));
    // Deltas.
    CASE("delta1_u32_7", bs.WriteDelta((uint32_t)7));
    CASE("delta_bool_true_false", bs.WriteDelta(true, false));
    CASE("delta_bool_false_false", bs.WriteDelta(false, false));
    CASE("delta1_bool_false", bs.WriteDelta(false));
    CASE("delta_f_1.5_1", bs.WriteDelta(1.5f, 1.0f));
    CASE("delta_d_1.5_1.5", bs.WriteDelta(1.5, 1.5));
    CASE("delta_d_m2_1.5", bs.WriteDelta(-2.0, 1.5));
    { SystemAddress a("10.1.2.3", 1234), b("10.1.2.3", 1235);
      CASE("delta_sa_changed", bs.WriteDelta(a, b));
      CASE("delta_sa_same", bs.WriteDelta(a, a)); }
    { RakNetGUID g(0x0102030405060708ULL), h(0x0102030405060709ULL);
      CASE("delta_guid_changed", bs.WriteDelta(g, h));
      CASE("delta_guid_same", bs.WriteDelta(g, g)); }
    { uint24_t u(0x123456), v(0x123457);
      CASE("delta_u24_changed", bs.WriteDelta(u, v));
      CASE("delta_u24_same", bs.WriteDelta(u, u)); }
    CASE("cdelta_u32_5_5", bs.WriteCompressedDelta((uint32_t)5, (uint32_t)5));
    CASE("cdelta_u32_6_5", bs.WriteCompressedDelta((uint32_t)6, (uint32_t)5));
    CASE("cdelta1_u32_6", bs.WriteCompressedDelta((uint32_t)6));
    CASE("cdelta_bool_true_true", bs.WriteCompressedDelta(true, true));
    CASE("cdelta1_bool_true", bs.WriteCompressedDelta(true));
    CASE("cdelta_f_0.5_0.25", bs.WriteCompressedDelta(0.5f, 0.25f));
    CASE("cdelta_d_m0.3_0", bs.WriteCompressedDelta(-0.3, 0.0));
    { SystemAddress a("10.1.2.3", 1234);
      CASE("compressed_sa", bs.WriteCompressed(a)); }
    { RakNetGUID g(0x0102030405060708ULL);
      CASE("write_guid", bs.Write(g));
      CASE("compressed_guid", bs.WriteCompressed(g)); }
    { uint24_t u(0x123456);
      CASE("compressed_u24", bs.WriteCompressed(u)); }
    // Ranges with an explicit bit count.
    CASE("rbits_u16_5_0_1000_12", bs.WriteBitsFromIntegerRange((uint16_t)5, (uint16_t)0, (uint16_t)1000, 12));
    CASE("rbits_u32_300_0_1000_9", bs.WriteBitsFromIntegerRange((uint32_t)300, (uint32_t)0, (uint32_t)1000, 9));
    CASE("rbits_u16_out_5000_12", bs.WriteBitsFromIntegerRange((uint16_t)5000, (uint16_t)0, (uint16_t)1000, 12, true));
    // Left-aligned bits.
    { unsigned char in[2] = {0xab, 0xe0};
      CASE("bits_left_11", bs.WriteBits(in, 11, false)); }
    { unsigned char in[1] = {0xff};
      CASE("bits_left_3_junk", bs.WriteBits(in, 3, false); bs.Write0(); bs.Write0()); }
    { unsigned char in[1] = {0x05};
      CASE("bits_right_3", bs.WriteBits(in, 3, true)); }
    { BitStream src; src.Write((uint8_t)0x5a); src.Write((uint8_t)0xc3); src.Write1(); src.Write0(); src.Write1();
      unsigned char out[3] = {0, 0, 0};
      src.IgnoreBits(1);
      src.ReadBits(out, 10, false);
      printf("readbits_left_10 hex=%02x%02x readOffset=%u\n", out[0], out[1], (unsigned)src.GetReadOffset());
      src.SetReadOffset(1);
      memset(out, 0, sizeof(out));
      src.ReadBits(out, 10, true);
      printf("readbits_right_10 hex=%02x%02x\n", out[0], out[1]); }
    // Copying bits between streams, off the read and write boundaries.
    { BitStream src; src.Write((uint8_t)0x5a); src.Write((uint8_t)0xc3); src.Write1();
      src.IgnoreBits(3);
      CASE("copy_bits_10", bs.Write(&src, 10));
      printf("copy_bits_10 srcReadOffset=%u\n", (unsigned)src.GetReadOffset()); }
    { BitStream src; src.Write((uint8_t)0x5a); src.Write((uint8_t)0xc3); src.Write1();
      src.IgnoreBits(3);
      CASE("copy_bits_rest", bs.Write(&src)); }
    // Aligned bytes with a length.
    { const char in[] = "abcdef";
      CASE("aligned_safe_6_max4", bs.WriteAlignedBytesSafe(in, 6, 4));
      CASE("aligned_safe_6_max10", bs.WriteAlignedBytesSafe(in, 6, 10));
      CASE("aligned_safe_0", bs.WriteAlignedBytesSafe(in, 0, 10)); }
    // Write offset and padding.
    { BitStream p; p.Write((uint32_t)0x11223344); p.SetWriteOffset(8); p.Write((uint8_t)0xaa); p.SetWriteOffset(32);
      dump("set_write_offset_patch", p); }
    { BitStream p; p.Write1(); p.PadWithZeroToByteLength(4); dump("pad_1bit_to_4", p); }
    { BitStream p; p.Write((uint32_t)0x11223344); p.Write((uint8_t)0x55); p.PadWithZeroToByteLength(4); dump("pad_5_to_4", p); }
    // Raw-byte strings: a RakString of non-UTF-8 bytes, and C strings stop at the first NUL.
    { const char raw[] = {(char)0xe9, (char)0xff, 'a', 0};
      CASE("rakstring_raw_e9ff61", RakString::Serialize(raw, &bs)); }
    { const char nul[] = {'a', 'b', 0, 'c', 0};
      CASE("rakstring_embedded_nul", RakString::Serialize(nul, &bs));
      CASE("huff_embedded_nul", StringCompressor::Instance()->EncodeString(nul, 256, &bs)); }
    CASE("huff_null", StringCompressor::Instance()->EncodeString((const char*)0, 256, &bs));
    // Wide strings: a u16 count then each character as a big-endian u16, unaligned.
    CASE("rakwstring_hi", RakWString::Serialize(L"hi\x00e9\x4e2d", &bs));
    CASE("rakwstring_empty", RakWString::Serialize(L"", &bs));
    CASE("rakwstring_compressed_hi", bs.WriteCompressed(RakWString(L"hi")));
    // Language ids and custom trees.
    CASE("rakstring_compressed_lang0_written", RakString("hello").SerializeCompressed(&bs, 0, true));
    { unsigned char sample[] = "aaaaaaaabbbbccd";
      StringCompressor::Instance()->GenerateTreeFromStrings(sample, (unsigned)strlen((const char*)sample), 7);
      CASE("huff_lang7_abcd", StringCompressor::Instance()->EncodeString("abcd", 256, &bs, 7));
      CASE("rakstring_compressed_lang7_written", RakString("dcba").SerializeCompressed(&bs, 7, true));
      CASE("huff_lang9_missing", StringCompressor::Instance()->EncodeString("abcd", 256, &bs, 9)); }
    // String table: registered strings are a 1 bit then the index in sorted order; others a 0 bit then Huffman.
    { StringTable::AddReference();
      StringTable::Instance()->AddString("zebra", true);
      StringTable::Instance()->AddString("apple", true);
      StringTable::Instance()->AddString("mango", true);
      CASE("stringtable_apple", StringTable::Instance()->EncodeString("apple", 256, &bs));
      CASE("stringtable_mango", StringTable::Instance()->EncodeString("mango", 256, &bs));
      CASE("stringtable_zebra", StringTable::Instance()->EncodeString("zebra", 256, &bs));
      CASE("stringtable_kiwi", StringTable::Instance()->EncodeString("kiwi", 256, &bs));
      char out[16];
      BitStream in; in.Write1(); in.Write((unsigned char)2);
      bool ok = StringTable::Instance()->DecodeString(out, 4, &in);
      printf("stringtable_decode_2_max4 ok=%d out=%s\n", ok ? 1 : 0, out);
      BitStream bad; bad.Write1(); bad.Write((unsigned char)3);
      ok = StringTable::Instance()->DecodeString(out, 16, &bad);
      printf("stringtable_decode_3 ok=%d\n", ok ? 1 : 0); }
    // SystemAddress text forms (RAKNET_SUPPORT_IPV6 is 0: SetBinaryAddress and ToString_Old).
    { const char* inputs[] = {"1.2.3.4|5678", "1.2.3.4:5678", "1.2.3.4", "localhost", "LocalHost9000", "localhost|9000",
                              "1.2", "010.0.0.1", "300.1.1.1", "|80", "1.2.3.4|99999", "1.2.3.4 80", ""};
      for (unsigned i = 0; i < sizeof(inputs) / sizeof(inputs[0]); i++)
      {
          SystemAddress a("9.9.9.9", 4321);
          bool ok = a.FromString(inputs[i]);
          printf("sa_fromstring \"%s\" ok=%d -> %s\n", inputs[i], ok ? 1 : 0, a.ToString(true));
      }
      SystemAddress a("1.2.3.4", 99);
      bool ok = a.FromStringExplicitPort("5.6.7.8:12", 77);
      printf("sa_explicitport ok=%d -> %s\n", ok ? 1 : 0, a.ToString(true));
      printf("sa_tostring_noport %s\n", a.ToString(false));
      printf("sa_tostring_colon %s\n", a.ToString(true, ':'));
      printf("sa_tostring_unassigned %s\n", UNASSIGNED_SYSTEM_ADDRESS.ToString(false));
      SystemAddress lan10("10.0.0.1", 1), lan192("192.168.0.1", 1), wan("8.8.8.8", 1), lastTen("8.8.8.10", 1);
      printf("sa_islan 10.0.0.1=%d 192.168.0.1=%d 8.8.8.8=%d 8.8.8.10=%d\n", lan10.IsLANAddress(), lan192.IsLANAddress(), wan.IsLANAddress(), lastTen.IsLANAddress()); }
    { const char* ordered[] = {"1.2.3.4|1", "1.2.3.4|256", "4.3.2.1|256", "1.2.3.5|256", "2.0.0.0|256", "1.2.3.4|2"};
      for (unsigned i = 0; i < 6; i++)
          for (unsigned j = 0; j < 6; j++)
          {
              SystemAddress a(ordered[i]), b(ordered[j]);
              printf("sa_order %s %s lt=%d gt=%d\n", ordered[i], ordered[j], a < b ? 1 : 0, a > b ? 1 : 0);
          } }

    // The default Huffman code for every byte, root to leaf.
    HuffmanEncodingTree tree;
    tree.GenerateFromFrequencyTable(englishCharacterFrequencies);
    for (int i = 0; i < 256; i++)
    {
        printf("huffcode %d ", i);
        BitStream code(tree.encodingTable[i].encoding, (tree.encodingTable[i].bitLength + 7) / 8, false);
        for (int b = 0; b < tree.encodingTable[i].bitLength; b++) printf("%d", code.ReadBit() ? 1 : 0);
        printf("\n");
    }
    // A table whose merged weights pass 2^32: the unsigned sums wrap (DS_HuffmanEncodingTree.cpp:109).
    {
        static unsigned int overflow[256];
        for (int i = 0; i < 256; i++) overflow[i] = 1;
        overflow[0] = 0x80000000u;
        overflow[1] = 0x80000000u;
        overflow[2] = 0xC0000000u;
        HuffmanEncodingTree wrapped;
        wrapped.GenerateFromFrequencyTable(overflow);
        unsigned char in[] = {2, 0, 1, 3};
        CASE("huff_overflow_encode_2013", wrapped.EncodeArray(in, 4, &bs));
    }
    return 0;
}
