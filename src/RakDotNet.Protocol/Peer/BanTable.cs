namespace RakDotNet.Protocol.Peer;

/// <summary>
/// RakPeer's banList (RakPeer.cpp:1861-2049): dotted IPv4 patterns, where a <c>*</c> bans everything from that
/// character on, each banned forever or until a time in milliseconds.
/// </summary>
internal sealed class BanTable
{
    // RakPeer.cpp:1866: patterns and addresses longer than this are ignored, so IPv6 addresses are never banned.
    private const int MaxAddressLength = 15;

    private readonly List<Ban> _bans = [];

    public bool IsEmpty => _bans.Count == 0;

    /// <summary>AddToBanList: bans <paramref name="pattern"/>, or replaces its duration if it is already banned.</summary>
    /// <param name="pattern">A dotted IP address, which may use <c>*</c>.</param>
    /// <param name="durationMs">How long the ban lasts; 0 bans forever.</param>
    /// <param name="nowMs">The current time.</param>
    public void Add(string pattern, long durationMs, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!IsValid(pattern))
            return;
        var expiresMs = durationMs == 0 ? 0 : nowMs + durationMs;
        var index = _bans.FindIndex(b => b.Pattern == pattern);
        if (index >= 0)
            _bans[index] = _bans[index] with { ExpiresMs = expiresMs };
        else
            _bans.Add(new Ban(pattern, expiresMs));
    }

    /// <summary>RemoveFromBanList: lifts the ban on exactly <paramref name="pattern"/>.</summary>
    public void Remove(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var index = _bans.FindIndex(b => b.Pattern == pattern);
        if (index < 0)
            return;
        // RakPeer.cpp:1927-1929 moves the last entry into the gap.
        _bans[index] = _bans[^1];
        _bans.RemoveAt(_bans.Count - 1);
    }

    /// <summary>ClearBanList.</summary>
    public void Clear() => _bans.Clear();

    /// <summary>IsBanned: whether <paramref name="address"/> matches a ban that has not expired. Expired bans are removed.</summary>
    public bool IsBanned(string address, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsValid(address) || _bans.Count == 0)
            return false;
        var index = 0;
        while (index < _bans.Count)
        {
            var ban = _bans[index];
            // RakPeer.cpp:2001: a ban expires once its time is earlier than now.
            if (ban.ExpiresMs > 0 && ban.ExpiresMs < nowMs)
            {
                _bans[index] = _bans[^1];
                _bans.RemoveAt(_bans.Count - 1);
                continue;
            }
            if (Matches(ban.Pattern, address))
                return true;
            index++;
        }
        return false;
    }

    private static bool IsValid(string address) => address.Length is > 0 and <= MaxAddressLength;

    // The character loop of RakPeer.cpp:2013-2046, with the C strings' terminating 0 read past each end.
    private static bool Matches(string pattern, string address)
    {
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

    private readonly record struct Ban(string Pattern, long ExpiresMs);
}
