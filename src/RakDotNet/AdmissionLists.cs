using System.Collections;
using System.Net;
using RakDotNet.Protocol.Peer;

namespace RakDotNet;

/// <summary>
/// The security exception list: addresses allowed to connect without security. Patterns use <c>*</c> per octet, as
/// RakNet's; they are validated on <see cref="Add"/>. Applies to incoming connections only.
/// </summary>
public sealed class AddressPatternCollection : IReadOnlyCollection<string>
{
    private AddressPatternCollection() { }

    public int Count => throw new NotImplementedException();

    /// <summary>AddToSecurityExceptionList</summary>
    public void Add(string pattern) => throw new NotImplementedException();

    /// <summary>RemoveFromSecurityExceptionList</summary>
    public bool Remove(string pattern) => throw new NotImplementedException();

    /// <summary>RemoveFromSecurityExceptionList(0)</summary>
    public void Clear() => throw new NotImplementedException();

    /// <summary>IsInSecurityExceptionList</summary>
    public bool Contains(IPAddress address) => throw new NotImplementedException();

    /// <inheritdoc cref="Contains(IPAddress)"/>
    public bool Contains(string address) => throw new NotImplementedException();

    public IEnumerator<string> GetEnumerator() => throw new NotImplementedException();

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
