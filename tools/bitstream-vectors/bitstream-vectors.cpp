// Prints the bytes native RakNet's BitStream encodings produce, for the known-answer tests in
// tests/RakDotNet.Protocol.Tests/Wire/BitStreamEncodingsTests.cs. Every case starts with one 1 bit so the encoding is
// checked off a byte boundary. Build and run with build.sh.
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
    return 0;
}
