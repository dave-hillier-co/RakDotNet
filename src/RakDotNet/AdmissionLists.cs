using System.Collections;
using System.Net;
using RakDotNet.Protocol.Peer;

namespace RakDotNet;

/// <summary>
/// The security exception list: address patterns a server with security lets through the offline handshake without
/// a challenge, and answers without security (RakPeer.cpp:5211-5228). Patterns are stored as given and match as
/// RakNet's do: character by character, a <c>*</c> matching everything from that character on, and an address longer
/// than 15 characters never matching. Applies to incoming connections only.
/// </summary>
/// <remarks>
/// <para>
/// Changes take effect at once, from any thread, as RakPeer's do under securityExceptionMutex.
/// </para>
/// <para>
/// As in RakNet 4.081, the list does not let a client connect without security: its ID_CONNECTION_REQUEST carries no
/// key proof, which a server with security ignores (RakPeer.cpp:3460-3465), so the attempt fails once the half-open
/// connection times out. A client that passes a public key while listed gets a reply without the handshake answer,
/// which it cannot process (RakPeer.cpp:4964-4992), so its attempt fails too.
/// </para>
/// </remarks>
public sealed class AddressPatternCollection : IReadOnlyCollection<string>
{
    private readonly RakPeer _peer;
    private readonly SecurityExceptionList _list;

    internal AddressPatternCollection(RakPeer peer, SecurityExceptionList list)
    {
        _peer = peer;
        _list = list;
    }

    public int Count => _list.Count;

    /// <summary>AddToSecurityExceptionList: appends the pattern, even when it is listed already.</summary>
    public void Add(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _peer.ThrowIfStopped();
        _list.Add(pattern);
    }

    /// <summary>
    /// RemoveFromSecurityExceptionList: removes every listed pattern that <paramref name="address"/> matches, so
    /// removing <c>10.0.0.5</c> also removes <c>10.0.0.*</c> (RakPeer.cpp:868-881). False when nothing matched.
    /// </summary>
    public bool Remove(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        _peer.ThrowIfStopped();
        return _list.Remove(address);
    }

    /// <summary>RemoveFromSecurityExceptionList(0)</summary>
    public void Clear()
    {
        _peer.ThrowIfStopped();
        _list.Clear();
    }

    /// <summary>IsInSecurityExceptionList</summary>
    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _list.Contains(address.ToString());
    }

    /// <inheritdoc cref="Contains(IPAddress)"/>
    public bool Contains(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _list.Contains(address);
    }

    /// <summary>A snapshot of the listed patterns, in list order.</summary>
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_list.Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>RakNet's ban list. Patterns use <c>*</c> per octet; expiry runs on the peer's <see cref="TimeProvider"/>.</summary>
/// <remarks>
/// Changes take effect at once, from any thread, as RakPeer's do under banListMutex (RakPeer.cpp:1861-2049). The list
/// also holds the bans the peer adds itself: an unverified sender that sends anything but a connection request is banned
/// for the connection timeout (RakPeer.cpp:5962-5974). Patterns and addresses longer than 15 characters are ignored, as
/// in RakNet, so most IPv6 addresses cannot be banned.
/// </remarks>
public sealed class BanList
{
    private readonly RakPeer _peer;
    private readonly BanTable _table;

    internal BanList(RakPeer peer, BanTable table)
    {
        _peer = peer;
        _table = table;
    }

    /// <summary>AddToBanList; <see cref="TimeSpan.Zero"/> bans forever. Banning a pattern again replaces its duration.</summary>
    public void Add(string pattern, TimeSpan duration = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        _peer.ThrowIfStopped();
        _table.Add(pattern, (long)duration.TotalMilliseconds, NowMs);
    }

    /// <summary>RemoveFromBanList; false when the pattern was not banned.</summary>
    public bool Remove(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _peer.ThrowIfStopped();
        return _table.Remove(pattern);
    }

    /// <summary>ClearBanList</summary>
    public void Clear()
    {
        _peer.ThrowIfStopped();
        _table.Clear();
    }

    /// <summary>IsBanned</summary>
    public bool IsBanned(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _table.IsBanned(address.ToString(), NowMs);
    }

    /// <inheritdoc cref="IsBanned(IPAddress)"/>
    public bool IsBanned(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _table.IsBanned(address, NowMs);
    }

    private long NowMs => _peer.NowUs / 1000;
}
