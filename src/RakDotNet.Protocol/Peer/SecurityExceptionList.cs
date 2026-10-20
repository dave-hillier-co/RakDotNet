namespace RakDotNet.Protocol.Peer;

/// <summary>
/// RakPeer's securityExceptionList (RakPeer.cpp:842-904): address patterns a server with security lets through the
/// offline handshake without it (RakPeer.cpp:5211-5214). A pattern matches as RakString::IPAddressMatch does: character
/// by character, a <c>*</c> in the pattern matching everything from that character on, and addresses longer than 15
/// characters never matching (RakString.cpp:752-800).
/// </summary>
/// <remarks>Thread-safe, as RakPeer guards the list with securityExceptionMutex.</remarks>
internal sealed class SecurityExceptionList
{
    // RakString.cpp:756.
    private const int MaxAddressLength = 15;

    private readonly List<string> _patterns = [];
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
                return _patterns.Count;
        }
    }

    /// <summary>AddToSecurityExceptionList: appends the pattern, even when it is already listed.</summary>
    public void Add(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        lock (_gate)
            _patterns.Add(pattern);
    }

    /// <summary>
    /// RemoveFromSecurityExceptionList(ip): removes every listed pattern that matches <paramref name="address"/>, moving
    /// the last entry into each gap (RakPeer.cpp:868-881). True when any was removed.
    /// </summary>
    public bool Remove(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var removed = false;
        lock (_gate)
        {
            var i = 0;
            while (i < _patterns.Count)
            {
                if (Matches(_patterns[i], address))
                {
                    _patterns[i] = _patterns[^1];
                    _patterns.RemoveAt(_patterns.Count - 1);
                    removed = true;
                }
                else
                {
                    i++;
                }
            }
        }
        return removed;
    }

    /// <summary>RemoveFromSecurityExceptionList(0).</summary>
    public void Clear()
    {
        lock (_gate)
            _patterns.Clear();
    }

    /// <summary>IsInSecurityExceptionList.</summary>
    public bool Contains(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        lock (_gate)
            return _patterns.Exists(p => Matches(p, address));
    }

    /// <summary>The listed patterns, in list order.</summary>
    public string[] Snapshot()
    {
        lock (_gate)
            return [.. _patterns];
    }

    // RakString::IPAddressMatch(IP), with the pattern as the RakString and the C strings' terminating 0 read past each end.
    private static bool Matches(string pattern, string address)
    {
        if (address.Length is 0 or > MaxAddressLength)
            return false;
        for (var i = 0; ; i++)
        {
            var p = i < pattern.Length ? pattern[i] : '\0';
            var a = i < address.Length ? address[i] : '\0';
            if (p == a)
            {
                if (a == '\0')
                    return true;
                continue;
            }
            if (p == '\0' || a == '\0')
                return false;
            return p == '*';
        }
    }
}
