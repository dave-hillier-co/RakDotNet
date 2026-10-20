// Prints what native RakNet's SuperFastHash, FileList and CloudCommon code produce, for the known-answer tests in
// tests/RakDotNet.Plugins.Tests/FileListTransfer and tests/RakDotNet.Plugins.Tests/Cloud. Build and run with build.sh.
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include "SuperFastHash.h"
#include "FileList.h"
#include "BitStream.h"
#include "StringCompressor.h"
#include "CloudCommon.h"

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

static CloudQueryRow row(const char* primaryKey, uint32_t secondaryKey, const char* data, uint32_t length, const char* server,
                         const char* client, uint64_t serverGuid, uint64_t clientGuid)
{
    CloudQueryRow r;
    r.key = CloudKey(primaryKey, secondaryKey);
    r.data = (unsigned char*)data;
    r.length = length;
    r.serverSystemAddress.FromString(server);
    r.clientSystemAddress.FromString(client);
    r.serverGUID = RakNetGUID(serverGuid);
    r.clientGUID = RakNetGUID(clientGuid);
    return r;
}

// CloudCommon.cpp's Serialize functions, as CloudClient and CloudServer write them.
static void cloud()
{
    BitStream key;
    CloudKey("ApplicationName", 1).Serialize(true, &key);
    dump("cloud_key", key);

    BitStream emptyKey;
    CloudKey("", 0x01020304).Serialize(true, &emptyKey);
    dump("cloud_key_empty", emptyKey);

    CloudQuery all;
    all.keys.Push(CloudKey("ApplicationName", 1), _FILE_AND_LINE_);
    all.keys.Push(CloudKey("ApplicationName", 2), _FILE_AND_LINE_);
    BitStream allBs;
    all.Serialize(true, &allBs);
    dump("cloud_query_all", allBs);

    CloudQuery paged;
    paged.keys.Push(CloudKey("k", 0x01020304), _FILE_AND_LINE_);
    paged.startingRowIndex = 3;
    paged.maxRowsToReturn = 7;
    paged.subscribeToResults = true;
    BitStream pagedBs;
    paged.Serialize(true, &pagedBs);
    dump("cloud_query_paged", pagedBs);

    CloudQuery maxOnly;
    maxOnly.keys.Push(CloudKey("k", 9), _FILE_AND_LINE_);
    maxOnly.maxRowsToReturn = 2;
    BitStream maxOnlyBs;
    maxOnly.Serialize(true, &maxOnlyBs);
    dump("cloud_query_max_only", maxOnlyBs);

    const char ds[] = "DS1C1S1";
    CloudQueryRow withData = row("ApplicationName", 1, ds, sizeof(ds), "10.0.0.1|60002", "127.0.0.1|61000", 0x1122334455667788ULL, 0x99aabbccddeeff00ULL);
    BitStream rowBs;
    withData.Serialize(true, &rowBs, 0);
    dump("cloud_row", rowBs);

    CloudQueryRow placeholder = row("ApplicationName", 2, 0, 0, "0.0.0.0|0", "0.0.0.0|0", 5, 6);
    placeholder.serverSystemAddress = UNASSIGNED_SYSTEM_ADDRESS;
    placeholder.clientSystemAddress = UNASSIGNED_SYSTEM_ADDRESS;
    BitStream placeholderBs;
    placeholder.Serialize(true, &placeholderBs, 0);
    dump("cloud_row_unassigned", placeholderBs);

    // ID_CLOUD_SUBSCRIPTION_NOTIFICATION as CloudServer::NotifyClientSubscribersOfDataChange writes it.
    BitStream notification;
    notification.Write((MessageID)121);
    notification.Write(true);
    withData.Serialize(true, &notification, 0);
    dump("cloud_notification", notification);

    CloudQueryResult result;
    result.cloudQuery = all;
    result.subscribeToResults = false;
    result.rowsReturned.Push(&withData, _FILE_AND_LINE_);
    result.rowsReturned.Push(&placeholder, _FILE_AND_LINE_);
    BitStream resultBs;
    resultBs.Write((MessageID)118);
    result.Serialize(true, &resultBs, 0);
    dump("cloud_get_response", resultBs);
    result.rowsReturned.Clear(true, _FILE_AND_LINE_);
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

    cloud();
    return 0;
}
