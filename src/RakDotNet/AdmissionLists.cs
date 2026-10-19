using System.Collections;
using System.Net;

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
public sealed class BanList
{
    private BanList() { }

    /// <summary>AddToBanList; <see cref="TimeSpan.Zero"/> bans forever.</summary>
    public void Add(string pattern, TimeSpan duration = default) => throw new NotImplementedException();

    /// <summary>RemoveFromBanList</summary>
    public bool Remove(string pattern) => throw new NotImplementedException();

    /// <summary>ClearBanList</summary>
    public void Clear() => throw new NotImplementedException();

    /// <summary>IsBanned</summary>
    public bool IsBanned(IPAddress address) => throw new NotImplementedException();

    /// <inheritdoc cref="IsBanned(IPAddress)"/>
    public bool IsBanned(string address) => throw new NotImplementedException();
}
