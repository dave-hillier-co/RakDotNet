// Prints what native RakNet's SuperFastHash and FileList code produce, for the known-answer tests in
// tests/RakDotNet.Plugins.Tests/FileListTransfer. Build and run with build.sh.
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include "SuperFastHash.h"
#include "FileList.h"
#include "BitStream.h"
#include "StringCompressor.h"

using namespace RakNet;

static void hash(const char* name, const unsigned char* data, int length)
{
    printf("hash %s length=%d value=%08x\n", name, length, (unsigned)SuperFastHash((const char*)data, length));
}

static void dump(const char* name, BitStream& bs)
{
    printf("%s bits=%u hex=", name, (unsigned)bs.GetNumberOfBitsUsed());
    for (unsigned i = 0; i < bs.GetNumberOfBytesUsed(); i++) printf("%02x", bs.GetData()[i]);
    printf("\n");
}

int main()
{
    static unsigned char pattern[200000];
    for (int i = 0; i < (int)sizeof(pattern); i++)
        pattern[i] = (unsigned char)(i * 31 + 7);
    const unsigned char text[] = "Hello, World!";
    const unsigned char high[] = {0x80, 0xff, 0xfe, 0x81, 0xc3, 0x9f, 0xa0};

    hash("empty", text, 0);
    for (int n = 1; n <= 13; n++)
    {
        char name[32];
        sprintf(name, "text_%d", n);
        hash(name, text, n);
    }
    for (int n = 1; n <= 7; n++)
    {
        char name[32];
        sprintf(name, "high_%d", n);
        hash(name, high, n);
    }
    hash("pattern_65535", pattern, 65535);
    hash("pattern_65536", pattern, 65536);
    hash("pattern_65539", pattern, 65539);
    hash("pattern_131072", pattern, 131072);
    hash("pattern_200000", pattern, 200000);
    printf("incremental pattern_100_then_7 value=%08x\n",
        (unsigned)SuperFastHashIncremental((const char*)pattern + 100, 7, SuperFastHashIncremental((const char*)pattern, 100, 107)));

    StringCompressor::AddReference();

    // FileList::Serialize of three files: data with a hash prefix, hash only, and a name only.
    FileList list;
    unsigned char withHash[4 + 5] = {0, 0, 0, 0, 'a', 'b', 'c', 'd', 'e'};
    unsigned int h = SuperFastHash((const char*)withHash + 4, 5);
    if (BitStream::DoEndianSwap())
        BitStream::ReverseBytesInPlace((unsigned char*)&h, sizeof(h));
    memcpy(withHash, &h, 4);
    list.AddFile("dir/a.txt", "full/dir/a.txt", (const char*)withHash, 9, 5, FileListNodeContext(7, 1, 0x12345678, 3));
    list.AddFile("b.bin", "b.bin", (const char*)&h, 4, 70000, FileListNodeContext(0, 0, 0, 0));
    list.AddFile("dir/sub/c", "dir/sub/c", 0, 0, 0, FileListNodeContext(255, 0xffffffff, 0, 0));
    BitStream serialized;
    list.Serialize(&serialized);
    dump("filelist_three", serialized);
    printf("hash_abcde_bytes=%02x%02x%02x%02x\n", withHash[0], withHash[1], withHash[2], withHash[3]);

    FileList empty;
    BitStream emptySerialized;
    empty.Serialize(&emptySerialized);
    dump("filelist_empty", emptySerialized);
    return 0;
}
