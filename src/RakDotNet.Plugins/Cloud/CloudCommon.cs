using System.Diagnostics.CodeAnalysis;
using System.Text;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins;

/// <summary>
/// CloudKey (CloudCommon.h): identifies data posted to or queried from a <see cref="CloudServer"/>. The primary key is
/// a major category such as the application's name, the secondary key a subcategory.
/// </summary>
/// <remarks>
/// The primary key is a RakString: bytes up to the first NUL, UTF-8 when built from a <see cref="string"/>. Keys compare
/// as CloudKeyComp does (CloudCommon.cpp:19-30): the primary keys' bytes as strcmp compares them, then the secondary
/// keys.
/// </remarks>
public readonly struct CloudKey : IEquatable<CloudKey>, IComparable<CloudKey>, IWireMessage<CloudKey>
{
    // Smallest encoding: an empty RakString's u16 length and the u32 secondary key.
    internal const int MinimumBits = (sizeof(ushort) + sizeof(uint)) * 8;

    private readonly byte[]? _primaryKey;

    /// <exception cref="ArgumentException">The primary key is more than 65535 bytes of UTF-8.</exception>
    public CloudKey(string primaryKey, uint secondaryKey)
        : this(Encoding.UTF8.GetBytes(primaryKey ?? throw new ArgumentNullException(nameof(primaryKey))), secondaryKey)
    {
    }

    /// <exception cref="ArgumentException">The primary key is more than 65535 bytes.</exception>
    public CloudKey(ReadOnlySpan<byte> primaryKey, uint secondaryKey)
    {
        var nul = primaryKey.IndexOf((byte)0);
        if (nul >= 0)
            primaryKey = primaryKey[..nul];
        if (primaryKey.Length > ushort.MaxValue)
            throw new ArgumentException("A RakString holds at most 65535 bytes.", nameof(primaryKey));
        _primaryKey = primaryKey.ToArray();
        SecondaryKey = secondaryKey;
    }

    /// <summary>primaryKey, decoded as UTF-8.</summary>
    public string PrimaryKey => Encoding.UTF8.GetString(PrimaryKeyBytes);

    /// <summary>primaryKey as the RakString holds it.</summary>
    public ReadOnlySpan<byte> PrimaryKeyBytes => _primaryKey;

    /// <summary>secondaryKey</summary>
    public uint SecondaryKey { get; }

    /// <summary>CloudKey::Serialize (CloudCommon.cpp:48-52): the primary key as a RakString, then the secondary key.</summary>
    public void Write(ref BitStreamWriter writer)
    {
        writer.WriteRakString(PrimaryKeyBytes);
        writer.WriteUInt32(SecondaryKey);
    }

    /// <summary>CloudKey::Serialize reading; false when the stream is too short.</summary>
    public static bool TryRead(ref BitStreamReader reader, out CloudKey value)
    {
        value = default;
        var r = reader;
        if (!r.TryReadRakStringBytes(out var primaryKey) || !r.TryReadUInt32(out var secondaryKey))
            return false;
        value = new CloudKey(primaryKey, secondaryKey);
        reader = r;
        return true;
    }

    public int CompareTo(CloudKey other)
    {
        var primary = PrimaryKeyBytes.SequenceCompareTo(other.PrimaryKeyBytes);
        return primary != 0 ? Math.Sign(primary) : SecondaryKey.CompareTo(other.SecondaryKey);
    }

    public bool Equals(CloudKey other) => SecondaryKey == other.SecondaryKey && PrimaryKeyBytes.SequenceEqual(other.PrimaryKeyBytes);

    public override bool Equals(object? obj) => obj is CloudKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(PrimaryKeyBytes);
        hash.Add(SecondaryKey);
        return hash.ToHashCode();
    }

    public override string ToString() => $"{PrimaryKey},{SecondaryKey}";

    public static bool operator ==(CloudKey left, CloudKey right) => left.Equals(right);

    public static bool operator !=(CloudKey left, CloudKey right) => !left.Equals(right);

    public static bool operator <(CloudKey left, CloudKey right) => left.CompareTo(right) < 0;

    public static bool operator >(CloudKey left, CloudKey right) => left.CompareTo(right) > 0;

    public static bool operator <=(CloudKey left, CloudKey right) => left.CompareTo(right) <= 0;

    public static bool operator >=(CloudKey left, CloudKey right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// CloudQuery (CloudCommon.h): the keys to query and how many of the rows to return, passed to
/// <see cref="CloudClient.Get(CloudQuery, RakNetGuid)"/>.
/// </summary>
/// <remarks>
/// A class to fill in, as RakNet's struct is: <see cref="ICloudServerQueryFilter.OnGetRequest"/> receives the query a
/// client sent and may change it, as RakNet's filter receives it by reference.
/// </remarks>
public sealed class CloudQuery : IWireMessage<CloudQuery>
{
    /// <summary>
    /// keys: at least one. Each key is queried in order over the posts of every client and the rows are concatenated.
    /// </summary>
    public List<CloudKey> Keys { get; } = [];

    /// <summary>startingRowIndex: the rows to skip. Has no effect unless <see cref="MaxRowsToReturn"/> is above 0.</summary>
    public uint StartingRowIndex { get; set; }

    /// <summary>maxRowsToReturn: 0 for no limit.</summary>
    public uint MaxRowsToReturn { get; set; }

    /// <summary>
    /// subscribeToResults: also get ID_CLOUD_SUBSCRIPTION_NOTIFICATION when a queried key is posted or released, until
    /// <see cref="CloudClient.Unsubscribe(ReadOnlySpan{CloudKey}, RakNetGuid)"/>.
    /// </summary>
    public bool SubscribeToResults { get; set; }

    /// <summary>
    /// CloudQuery::Serialize (CloudCommon.cpp:53-85): a bit each for a zero starting row and a zero row limit, the
    /// subscription bit, each value that is not zero, a u16 key count and the keys.
    /// </summary>
    /// <exception cref="InvalidOperationException">The query has 65535 keys or more.</exception>
    public void Write(ref BitStreamWriter writer)
    {
        // RakAssert(keys.Size()<(uint16_t)-1) (CloudCommon.cpp:66).
        if (Keys.Count >= ushort.MaxValue)
            throw new InvalidOperationException("A CloudQuery holds at most 65534 keys.");
        writer.WriteBool(StartingRowIndex == 0);
        writer.WriteBool(MaxRowsToReturn == 0);
        writer.WriteBool(SubscribeToResults);
        if (StartingRowIndex != 0)
            writer.WriteUInt32(StartingRowIndex);
        if (MaxRowsToReturn != 0)
            writer.WriteUInt32(MaxRowsToReturn);
        writer.WriteUInt16((ushort)Keys.Count);
        foreach (var key in Keys)
            key.Write(ref writer);
    }

    /// <summary>CloudQuery::Serialize reading; false when the stream is too short for what it declares.</summary>
    public static bool TryRead(ref BitStreamReader reader, [NotNullWhen(true)] out CloudQuery? value)
    {
        value = null;
        var r = reader;
        if (!r.TryReadBool(out var startingRowIndexIsZero) || !r.TryReadBool(out var maxRowsToReturnIsZero) || !r.TryReadBool(out var subscribe))
            return false;
        uint startingRowIndex = 0;
        uint maxRowsToReturn = 0;
        if (!startingRowIndexIsZero && !r.TryReadUInt32(out startingRowIndex))
            return false;
        if (!maxRowsToReturnIsZero && !r.TryReadUInt32(out maxRowsToReturn))
            return false;
        if (!r.TryReadUInt16(out var keyCount) || keyCount > r.BitsRemaining / CloudKey.MinimumBits)
            return false;
        var query = new CloudQuery { StartingRowIndex = startingRowIndex, MaxRowsToReturn = maxRowsToReturn, SubscribeToResults = subscribe };
        query.Keys.Capacity = keyCount;
        for (var i = 0; i < keyCount; i++)
        {
            if (!CloudKey.TryRead(ref r, out var key))
                return false;
            query.Keys.Add(key);
        }
        value = query;
        reader = r;
        return true;
    }

    /// <summary>A copy, as RakNet copies the struct.</summary>
    internal CloudQuery Clone()
    {
        var copy = new CloudQuery { StartingRowIndex = StartingRowIndex, MaxRowsToReturn = MaxRowsToReturn, SubscribeToResults = SubscribeToResults };
        copy.Keys.AddRange(Keys);
        return copy;
    }
}

/// <summary>
/// CloudQueryRow (CloudCommon.h): data one client posted under a key, with the server holding it and the client that
/// posted it.
/// </summary>
/// <param name="Key">key</param>
/// <param name="Data">data and length</param>
/// <param name="ServerSystemAddress">serverSystemAddress: the server holding the data, which the client is connected to</param>
/// <param name="ClientSystemAddress">clientSystemAddress: the client that posted the data, as its server sees it</param>
/// <param name="ServerGuid">serverGUID</param>
/// <param name="ClientGuid">clientGUID</param>
public sealed record CloudQueryRow(
    CloudKey Key,
    ReadOnlyMemory<byte> Data,
    SystemAddress ServerSystemAddress,
    SystemAddress ClientSystemAddress,
    RakNetGuid ServerGuid,
    RakNetGuid ClientGuid) : IWireMessage<CloudQueryRow>
{
    // Smallest encoding: a key, two IPv4 addresses, two GUIDs and the u32 length.
    internal const int MinimumBits = CloudKey.MinimumBits + 2 * SystemAddress.WireSize * 8 + 2 * sizeof(ulong) * 8 + sizeof(uint) * 8;

    /// <summary>
    /// CloudQueryRow::Serialize (CloudCommon.cpp:86-115): the key, the server's then the client's address, the server's
    /// then the client's GUID, the u32 length, then the data aligned to a byte.
    /// </summary>
    public void Write(ref BitStreamWriter writer)
    {
        Key.Write(ref writer);
        writer.WriteAddress(ServerSystemAddress);
        writer.WriteAddress(ClientSystemAddress);
        writer.WriteRakNetGuid(ServerGuid);
        writer.WriteRakNetGuid(ClientGuid);
        writer.WriteUInt32((uint)Data.Length);
        writer.WriteAlignedBytes(Data.Span);
    }

    /// <summary>
    /// CloudQueryRow::Serialize reading, copying the data. False when the stream is too short, where RakNet keeps a row
    /// of uninitialised bytes.
    /// </summary>
    public static bool TryRead(ref BitStreamReader reader, [NotNullWhen(true)] out CloudQueryRow? value)
    {
        value = null;
        var r = reader;
        if (!CloudKey.TryRead(ref r, out var key) || !r.TryReadSystemAddress(out var server) || !r.TryReadSystemAddress(out var client)
            || !r.TryReadRakNetGuid(out var serverGuid) || !r.TryReadRakNetGuid(out var clientGuid) || !r.TryReadUInt32(out var length))
            return false;
        ReadOnlyMemory<byte> data = ReadOnlyMemory<byte>.Empty;
        if (length > 0)
        {
            if (length > int.MaxValue || !r.TryReadAlignedBytes((int)length, out var bytes))
                return false;
            data = bytes.ToArray();
        }
        value = new CloudQueryRow(key, data, server, client, serverGuid, clientGuid);
        reader = r;
        return true;
    }
}

/// <summary>
/// CloudQueryResult (CloudCommon.h): the rows a <see cref="CloudServer"/> returned for a query, in
/// ID_CLOUD_GET_RESPONSE.
/// </summary>
/// <remarks>
/// RakNet's resultKeyIndices is never filled in or sent, so it is left out; rows of every key are concatenated in the
/// order of <see cref="CloudQuery.Keys"/>, followed by rows from other servers.
/// </remarks>
public sealed class CloudQueryResult : IWireMessage<CloudQueryResult>
{
    public CloudQueryResult(CloudQuery query, IReadOnlyList<CloudQueryRow> rows, bool subscribeToResults)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(rows);
        Query = query;
        Rows = rows;
        SubscribeToResults = subscribeToResults;
    }

    /// <summary>cloudQuery: the query as the client sent it.</summary>
    public CloudQuery Query { get; }

    /// <summary>rowsReturned</summary>
    public IReadOnlyList<CloudQueryRow> Rows { get; }

    /// <summary>subscribeToResults, sent again after the query.</summary>
    public bool SubscribeToResults { get; }

    /// <summary>
    /// CloudQueryResult::Serialize (CloudCommon.cpp:116-167): the query, the subscription bit, a u32 row count and the
    /// rows.
    /// </summary>
    public void Write(ref BitStreamWriter writer)
    {
        Query.Write(ref writer);
        writer.WriteBool(SubscribeToResults);
        writer.WriteUInt32((uint)Rows.Count);
        foreach (var row in Rows)
            row.Write(ref writer);
    }

    /// <summary>
    /// CloudQueryResult::Serialize reading. False when the stream is too short, or declares more rows than it can hold,
    /// where RakNet counts rows with a u16 and loops forever past 65535.
    /// </summary>
    public static bool TryRead(ref BitStreamReader reader, [NotNullWhen(true)] out CloudQueryResult? value)
    {
        value = null;
        var r = reader;
        if (!CloudQuery.TryRead(ref r, out var query) || !r.TryReadBool(out var subscribe) || !TryReadRows(ref r, out var rows))
            return false;
        value = new CloudQueryResult(query, rows, subscribe);
        reader = r;
        return true;
    }

    /// <summary>SerializeNumRows then SerializeCloudQueryRows reading (CloudCommon.cpp:121-160).</summary>
    internal static bool TryReadRows(ref BitStreamReader reader, [NotNullWhen(true)] out List<CloudQueryRow>? rows)
    {
        rows = null;
        if (!reader.TryReadUInt32(out var count) || count > (uint)(reader.BitsRemaining / CloudQueryRow.MinimumBits))
            return false;
        var list = new List<CloudQueryRow>((int)count);
        for (var i = 0u; i < count; i++)
        {
            if (!CloudQueryRow.TryRead(ref reader, out var row))
                return false;
            list.Add(row);
        }
        rows = list;
        return true;
    }
}
