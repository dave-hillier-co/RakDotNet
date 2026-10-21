using System.Diagnostics.CodeAnalysis;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// CloudClientCallback (CloudClient.h): what <see cref="CloudClient.OnGetResponse"/> and
/// <see cref="CloudClient.OnSubscriptionNotification"/> report.
/// </summary>
public interface ICloudClientHandler
{
    /// <summary>OnGet: the rows of an ID_CLOUD_GET_RESPONSE.</summary>
    void OnGet(CloudQueryResult result) { }

    /// <summary>
    /// OnSubscriptionNotification: a subscribed key was posted (<paramref name="wasUpdated"/>) or released, in which case
    /// <paramref name="row"/> holds the value it had.
    /// </summary>
    void OnSubscriptionNotification(CloudQueryRow row, bool wasUpdated) { }
}

/// <summary>
/// CloudClient: posts data to, releases it from, and queries and subscribes to it on a <see cref="CloudServer"/> it is
/// connected to. Clients connected to different servers of one cloud see each other's data without being connected.
/// </summary>
/// <remarks>
/// <para>
/// Ported from CloudClient.cpp. Every request is HIGH_PRIORITY, RELIABLE_ORDERED on channel 0 to the server's GUID. The
/// plugin consumes nothing: ID_CLOUD_GET_RESPONSE and ID_CLOUD_SUBSCRIPTION_NOTIFICATION reach the application, which
/// passes them to <see cref="OnGetResponse"/> and <see cref="OnSubscriptionNotification"/>, or reads them with
/// <see cref="TryReadGetResponse"/> and <see cref="TryReadSubscriptionNotification"/>.
/// </para>
/// <para>
/// RakNet's CloudAllocator and the deallocate flags of its callbacks only manage memory, so they are left out; rows own
/// copies of their data.
/// </para>
/// <para>Members may be called from any thread.</para>
/// </remarks>
public sealed class CloudClient : IRakPlugin
{
    private IPluginHost? _host;

    /// <summary>SetDefaultCallbacks: the handler used when none is passed.</summary>
    public ICloudClientHandler? DefaultHandler { get; set; }

    private IPluginHost Host => _host ?? throw new InvalidOperationException("The CloudClient is not attached to a peer.");

    /// <summary>
    /// Post (CloudClient.cpp:37-50): uploads <paramref name="data"/> under <paramref name="key"/> to
    /// <paramref name="server"/>, replacing what this client posted there under the key. The data stays while this
    /// client is connected to the server; subscribers to the key are notified. A server whose upload limit this exceeds
    /// drops it silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a peer.</exception>
    public void Post(CloudKey key, ReadOnlySpan<byte> data, RakNetGuid server)
    {
        using var stream = new BitStream(64 + data.Length);
        stream.WriteByte((byte)MessageId.CloudPostRequest);
        stream.Write(key);
        stream.WriteUInt32((uint)data.Length);
        if (data.Length > 0)
            stream.WriteAlignedBytes(data);
        Send(stream, server);
    }

    /// <summary>
    /// Release (CloudClient.cpp:51-62): removes data this client posted under <paramref name="keys"/>; subscribers get a
    /// notification holding the last value.
    /// </summary>
    /// <exception cref="ArgumentException">More than 65534 keys.</exception>
    public void Release(ReadOnlySpan<CloudKey> keys, RakNetGuid server)
    {
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudReleaseRequest);
        WriteKeys(stream, keys);
        Send(stream, server);
    }

    /// <summary>
    /// Get (CloudClient.cpp:63-71): queries the rows of every client for the query's keys. The rows arrive in
    /// ID_CLOUD_GET_RESPONSE; with <see cref="CloudQuery.SubscribeToResults"/> later changes arrive as
    /// ID_CLOUD_SUBSCRIPTION_NOTIFICATION. Always true, as RakNet's.
    /// </summary>
    public bool Get(CloudQuery query, RakNetGuid server) => Get(query, ReadOnlySpan<RakNetGuid>.Empty, server);

    /// <summary>
    /// Get (CloudClient.cpp:72-85): as <see cref="Get(CloudQuery, RakNetGuid)"/>, only for the rows posted by
    /// <paramref name="specificSystems"/>, and subscribing only to their changes. Subscribing again to a key replaces the
    /// systems subscribed to.
    /// </summary>
    /// <exception cref="ArgumentException">More than 65534 systems.</exception>
    public bool Get(CloudQuery query, ReadOnlySpan<RakNetGuid> specificSystems, RakNetGuid server)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfTooMany(specificSystems.Length, nameof(specificSystems));
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudGetRequest);
        stream.Write(query);
        stream.WriteUInt16((ushort)specificSystems.Length);
        foreach (var system in specificSystems)
            stream.WriteRakNetGuid(system);
        Send(stream, server);
        return true;
    }

    /// <summary>
    /// Get (CloudClient.cpp:86-108) naming the systems by rows: each as a set bit and its client GUID, or when that is
    /// unassigned a clear bit and its client address.
    /// </summary>
    /// <remarks>
    /// This is RakNet's encoding, but CloudServer reads plain GUIDs for the systems (CloudServer.cpp:541-547), so a
    /// server misreads this request; <see cref="Get(CloudQuery, ReadOnlySpan{RakNetGuid}, RakNetGuid)"/> is the form
    /// servers understand.
    /// </remarks>
    /// <exception cref="ArgumentException">More than 65534 rows.</exception>
    public bool Get(CloudQuery query, IReadOnlyList<CloudQueryRow> specificSystems, RakNetGuid server)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(specificSystems);
        ThrowIfTooMany(specificSystems.Count, nameof(specificSystems));
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudGetRequest);
        stream.Write(query);
        WriteRowSystems(stream, specificSystems);
        Send(stream, server);
        return true;
    }

    /// <summary>
    /// Unsubscribe (CloudClient.cpp:109-121): stops the notifications for <paramref name="keys"/>, whichever systems
    /// were subscribed to.
    /// </summary>
    /// <exception cref="ArgumentException">More than 65534 keys.</exception>
    public void Unsubscribe(ReadOnlySpan<CloudKey> keys, RakNetGuid server) => Unsubscribe(keys, ReadOnlySpan<RakNetGuid>.Empty, server);

    /// <summary>
    /// Unsubscribe (CloudClient.cpp:122-139): stops the notifications for <paramref name="keys"/> from
    /// <paramref name="specificSystems"/>. A server ignores this for a key subscribed to from every system.
    /// </summary>
    /// <exception cref="ArgumentException">More than 65534 keys or systems.</exception>
    public void Unsubscribe(ReadOnlySpan<CloudKey> keys, ReadOnlySpan<RakNetGuid> specificSystems, RakNetGuid server)
    {
        ThrowIfTooMany(specificSystems.Length, nameof(specificSystems));
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudUnsubscribeRequest);
        WriteKeys(stream, keys);
        stream.WriteUInt16((ushort)specificSystems.Length);
        foreach (var system in specificSystems)
            stream.WriteRakNetGuid(system);
        Send(stream, server);
    }

    /// <summary>
    /// Unsubscribe (CloudClient.cpp:140-166) naming the systems by rows, in the encoding of
    /// <see cref="Get(CloudQuery, IReadOnlyList{CloudQueryRow}, RakNetGuid)"/>, which servers misread as it does.
    /// </summary>
    /// <exception cref="ArgumentException">More than 65534 keys or rows.</exception>
    public void Unsubscribe(ReadOnlySpan<CloudKey> keys, IReadOnlyList<CloudQueryRow> specificSystems, RakNetGuid server)
    {
        ArgumentNullException.ThrowIfNull(specificSystems);
        ThrowIfTooMany(specificSystems.Count, nameof(specificSystems));
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudUnsubscribeRequest);
        WriteKeys(stream, keys);
        WriteRowSystems(stream, specificSystems);
        Send(stream, server);
    }

    /// <summary>
    /// OnGetReponse (CloudClient.cpp:173-196): reads an ID_CLOUD_GET_RESPONSE and passes its rows to
    /// <paramref name="handler"/>, or to <see cref="DefaultHandler"/>. False when the message is not a well-formed get
    /// response.
    /// </summary>
    /// <exception cref="InvalidOperationException">No handler is passed and <see cref="DefaultHandler"/> is null.</exception>
    public bool OnGetResponse(ReadOnlySpan<byte> packet, ICloudClientHandler? handler = null)
    {
        var target = handler ?? DefaultHandler ?? throw new InvalidOperationException("No handler was passed and DefaultHandler is not set.");
        if (!TryReadGetResponse(packet, out var result))
            return false;
        target.OnGet(result);
        return true;
    }

    /// <summary>
    /// OnSubscriptionNotification (CloudClient.cpp:206-226): reads an ID_CLOUD_SUBSCRIPTION_NOTIFICATION and passes it
    /// to <paramref name="handler"/>, or to <see cref="DefaultHandler"/>. False when the message is not a well-formed
    /// notification.
    /// </summary>
    /// <exception cref="InvalidOperationException">No handler is passed and <see cref="DefaultHandler"/> is null.</exception>
    public bool OnSubscriptionNotification(ReadOnlySpan<byte> packet, ICloudClientHandler? handler = null)
    {
        var target = handler ?? DefaultHandler ?? throw new InvalidOperationException("No handler was passed and DefaultHandler is not set.");
        if (!TryReadSubscriptionNotification(packet, out var wasUpdated, out var row))
            return false;
        target.OnSubscriptionNotification(row, wasUpdated);
        return true;
    }

    /// <summary>
    /// OnGetReponse into a result (CloudClient.cpp:197-205): the message ID, then <see cref="CloudQueryResult"/>. False
    /// for another message ID or a malformed message.
    /// </summary>
    public static bool TryReadGetResponse(ReadOnlySpan<byte> packet, [NotNullWhen(true)] out CloudQueryResult? result)
    {
        result = null;
        if (packet.IsEmpty || packet[0] != (byte)MessageId.CloudGetResponse)
            return false;
        var reader = new BitStreamReader(packet[1..]);
        return CloudQueryResult.TryRead(ref reader, out result);
    }

    /// <summary>
    /// OnSubscriptionNotification into a row (CloudClient.cpp:227-238): the message ID, whether the key was posted
    /// rather than released, then the row. False for another message ID or a malformed message.
    /// </summary>
    public static bool TryReadSubscriptionNotification(ReadOnlySpan<byte> packet, out bool wasUpdated, [NotNullWhen(true)] out CloudQueryRow? row)
    {
        wasUpdated = false;
        row = null;
        if (packet.IsEmpty || packet[0] != (byte)MessageId.CloudSubscriptionNotification)
            return false;
        var reader = new BitStreamReader(packet[1..]);
        return reader.TryReadBool(out wasUpdated) && CloudQueryRow.TryRead(ref reader, out row);
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    private void Send(BitStream stream, RakNetGuid server) =>
        Host.Send(stream, server, Reliability.ReliableOrdered, Priority.High, 0);

    private static void WriteKeys(BitStream stream, ReadOnlySpan<CloudKey> keys)
    {
        // RakAssert(keys.Size() < (uint16_t)-1), then WriteCasted<uint16_t> (CloudClient.cpp:55-56).
        ThrowIfTooMany(keys.Length, nameof(keys));
        stream.WriteUInt16((ushort)keys.Length);
        foreach (var key in keys)
            stream.Write(key);
    }

    private static void WriteRowSystems(BitStream stream, IReadOnlyList<CloudQueryRow> rows)
    {
        stream.WriteUInt16((ushort)rows.Count);
        foreach (var row in rows)
        {
            if (row.ClientGuid.IsAssigned)
            {
                stream.WriteBool(true);
                stream.WriteRakNetGuid(row.ClientGuid);
            }
            else
            {
                stream.WriteBool(false);
                stream.WriteAddress(row.ClientSystemAddress);
            }
        }
    }

    private static void ThrowIfTooMany(int count, string name)
    {
        if (count >= ushort.MaxValue)
            throw new ArgumentException("At most 65534 entries fit the u16 count RakNet writes.", name);
    }
}
