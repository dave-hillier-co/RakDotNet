using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// An IPv4 or IPv6 address and port held inline, RakNet's SystemAddress. No allocation; usable as a dictionary key.
/// </summary>
/// <remarks>
/// <c>default</c> is <see cref="Unassigned"/>, as RakNet's default SystemAddress is UNASSIGNED_SYSTEM_ADDRESS.
/// Equality compares family, address bytes and port, as SystemAddress::operator== (RakNetTypes.cpp:126-155); an IPv6
/// scope id is kept for <see cref="ToIPEndPoint"/> but not compared. Ordering follows SystemAddress::operator&lt; and
/// operator&gt; (RakNetTypes.cpp:162-191); see <see cref="CompareTo"/>.
/// </remarks>
public readonly struct SystemAddress : IEquatable<SystemAddress>, IComparable<SystemAddress>, ISpanFormattable, ISpanParsable<SystemAddress>
{
    private const string UnassignedText = "UNASSIGNED_SYSTEM_ADDRESS";

    /// <summary>
    /// SystemAddress::size() (RakNetTypes.cpp:191-198): the bytes an address takes when written to a BitStream, a
    /// version byte, a u32 and a u16, in RakNet's IPv4 build.
    /// </summary>
    public const int WireSize = sizeof(uint) + sizeof(ushort) + sizeof(byte);

    /// <summary>The port delimiter RakNet's text forms default to (RakNetTypes.h, ToString and FromString).</summary>
    public const char RakNetPortDelimiter = '|';

    // SetBinaryAddress copies at most 22 characters of the address and 10 digits of the port (RakNetTypes.cpp:535-554).
    private const int MaxAddressChars = 22;
    private const int MaxPortDigits = 10;
    private const int Ipv4SocketAddressSize = 16;
    private const int Ipv6SocketAddressSize = 28;

    // Big-endian address bytes as a number; IPv4 occupies the low 32 bits.
    private readonly UInt128 _address;
    private readonly ushort _port;
    private readonly bool _isIPv6;
    private readonly uint _scopeId;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is outside 0 to 65535.</exception>
    /// <exception cref="ArgumentException"><paramref name="address"/> is neither IPv4 nor IPv6.</exception>
    public SystemAddress(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var length))
            throw new ArgumentException("The address could not be read.", nameof(address));
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                _address = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                break;
            case AddressFamily.InterNetworkV6 when length == 16:
                _address = BinaryPrimitives.ReadUInt128BigEndian(bytes);
                _isIPv6 = true;
                _scopeId = (uint)address.ScopeId;
                break;
            default:
                throw new ArgumentException($"Address family {address.AddressFamily} is not supported.", nameof(address));
        }
        _port = (ushort)port;
    }

    /// <summary>An IPv4 address from its big-endian value.</summary>
    internal SystemAddress(uint ipv4, ushort port)
    {
        _address = ipv4;
        _port = port;
    }

    private SystemAddress(UInt128 ipv6, ushort port, uint scopeId)
    {
        _address = ipv6;
        _port = port;
        _isIPv6 = true;
        _scopeId = scopeId;
    }

    /// <summary>UNASSIGNED_SYSTEM_ADDRESS: IPv4 0.0.0.0, port 0 (RakNetTypes.h:363, RakNetTypes.cpp:378-385).</summary>
    public static SystemAddress Unassigned => default;

    /// <summary><see cref="AddressFamily.InterNetwork"/> or <see cref="AddressFamily.InterNetworkV6"/>.</summary>
    public AddressFamily AddressFamily => _isIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

    public ushort Port => _port;

    /// <summary>False only for <see cref="Unassigned"/>.</summary>
    public bool IsAssigned => this != Unassigned;

    /// <summary>
    /// SystemAddress::IsLoopback (RakNetTypes.cpp:242-259): 127.0.0.1 or 0.0.0.0 for IPv4, ::1 for IPv6.
    /// </summary>
    public bool IsLoopback => _isIPv6 ? _address == UInt128.One : (uint)_address is 0x7F000001 or 0;

    /// <summary>
    /// SystemAddress::IsLANAddress (RakNetTypes.cpp:468-476): an IPv4 address whose first octet is 10 or 192, as the
    /// Windows build tests it. RakNet's other builds shift the network-order value and so test the last octet; this
    /// keeps the evident intent. False for IPv6.
    /// </summary>
    public bool IsLanAddress => !_isIPv6 && ((uint)_address >> 24) is 10 or 192;

    /// <summary>
    /// This address with <paramref name="port"/>: SystemAddress::SetPortHostOrder, or CopyPort with another address's
    /// <see cref="Port"/> (RakNetTypes.cpp:142-146, 746-750).
    /// </summary>
    public SystemAddress WithPort(ushort port) => _isIPv6 ? new SystemAddress(_address, port, _scopeId) : new SystemAddress((uint)_address, port);

    /// <summary>
    /// Copies the address out of a <see cref="SocketAddress"/> filled by <c>Socket.ReceiveFrom</c>, without allocating.
    /// </summary>
    /// <exception cref="ArgumentException">The address is neither IPv4 nor IPv6.</exception>
    public static SystemAddress FromSocketAddress(SocketAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        // The sockaddr layouts every platform shares: the port at 2, then sin_addr at 4, or sin6_flowinfo at 4,
        // sin6_addr at 8 and sin6_scope_id at 24. The family field is read by the runtime, as its encoding differs.
        var buffer = address.Buffer.Span;
        var port = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
        return address.Family switch
        {
            AddressFamily.InterNetwork => new SystemAddress(BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]), port),
            AddressFamily.InterNetworkV6 => new SystemAddress(
                BinaryPrimitives.ReadUInt128BigEndian(buffer[8..]), port, BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..])),
            _ => throw new ArgumentException($"Address family {address.Family} is not supported.", nameof(address)),
        };
    }

    /// <summary>Writes this address into a <see cref="SocketAddress"/> for <c>Socket.SendTo</c>, without allocating.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is of another address family.</exception>
    public void CopyTo(SocketAddress destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Family != AddressFamily)
            throw new ArgumentException($"The destination is {destination.Family}, not {AddressFamily}.", nameof(destination));
        var buffer = destination.Buffer.Span;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], _port);
        if (_isIPv6)
        {
            destination.Size = Ipv6SocketAddressSize;
            buffer[4..8].Clear();
            BinaryPrimitives.WriteUInt128BigEndian(buffer[8..], _address);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[24..], _scopeId);
        }
        else
        {
            destination.Size = Ipv4SocketAddressSize;
            BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], (uint)_address);
            buffer[8..Ipv4SocketAddressSize].Clear();
        }
    }

    public static implicit operator SystemAddress(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return new SystemAddress(endPoint.Address, endPoint.Port);
    }

    /// <summary>Converts to an <see cref="IPEndPoint"/> for BCL interop. Allocates.</summary>
    public IPEndPoint ToIPEndPoint()
    {
        Span<byte> bytes = stackalloc byte[16];
        TryWriteAddressBytes(bytes, out var length);
        var address = _isIPv6 ? new IPAddress(bytes[..length], _scopeId) : new IPAddress(bytes[..length]);
        return new IPEndPoint(address, _port);
    }

    /// <summary>SystemAddress::EqualsExcludingPort (RakNetTypes.cpp:126-133).</summary>
    public bool EqualsIgnoringPort(SystemAddress other) => _isIPv6 == other._isIPv6 && _address == other._address;

    /// <summary>Writes the 4 or 16 address bytes in network order.</summary>
    public bool TryWriteAddressBytes(Span<byte> destination, out int bytesWritten)
    {
        var length = _isIPv6 ? 16 : 4;
        if (destination.Length < length)
        {
            bytesWritten = 0;
            return false;
        }
        if (_isIPv6)
            BinaryPrimitives.WriteUInt128BigEndian(destination, _address);
        else
            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)_address);
        bytesWritten = length;
        return true;
    }

    public bool Equals(SystemAddress other) => _port == other._port && EqualsIgnoringPort(other);

    public override bool Equals(object? obj) => obj is SystemAddress other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_address, _port, _isIPv6);

    public static bool operator ==(SystemAddress left, SystemAddress right) => left.Equals(right);

    public static bool operator !=(SystemAddress left, SystemAddress right) => !left.Equals(right);

    /// <summary>
    /// SystemAddress::operator&lt; and operator&gt; (RakNetTypes.cpp:162-191): the port first, then the address, each
    /// compared as the host-order integer of its network-order bytes, as RakNet compares <c>sin_port</c> and
    /// <c>s_addr</c> on little-endian hosts. An IPv6 address sorts after every IPv4 address with the same port and
    /// compares its bytes in order; RakNet's IPv4-only build has no IPv6 order.
    /// </summary>
    public int CompareTo(SystemAddress other)
    {
        var order = BinaryPrimitives.ReverseEndianness(_port).CompareTo(BinaryPrimitives.ReverseEndianness(other._port));
        if (order != 0)
            return order;
        if (_isIPv6 != other._isIPv6)
            return _isIPv6 ? 1 : -1;
        return _isIPv6
            ? _address.CompareTo(other._address)
            : BinaryPrimitives.ReverseEndianness((uint)_address).CompareTo(BinaryPrimitives.ReverseEndianness((uint)other._address));
    }

    public static bool operator <(SystemAddress left, SystemAddress right) => left.CompareTo(right) < 0;

    public static bool operator >(SystemAddress left, SystemAddress right) => left.CompareTo(right) > 0;

    public static bool operator <=(SystemAddress left, SystemAddress right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SystemAddress left, SystemAddress right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// <c>a.b.c.d:port</c> as the interop protocol writes addresses, <c>[address]:port</c> for IPv6, or
    /// <c>UNASSIGNED_SYSTEM_ADDRESS</c> as SystemAddress::ToString writes it (RakNetTypes.cpp:264-267).
    /// </summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[64];
        TryFormat(buffer, out var written, default, null);
        return buffer[..written].ToString();
    }

    /// <summary>
    /// SystemAddress::ToString(writePort, portDelineator) (RakNetTypes.cpp:262-302): the dotted address, then, with
    /// <paramref name="writePort"/>, <paramref name="portDelimiter"/> and the port; <c>UNASSIGNED_SYSTEM_ADDRESS</c>
    /// for <see cref="Unassigned"/>. An IPv6 address is written as getnameinfo writes it, without brackets
    /// (RakNetTypes.cpp:319-367).
    /// </summary>
    public string ToString(bool writePort, char portDelimiter = RakNetPortDelimiter)
    {
        if (!IsAssigned)
            return UnassignedText;
        var invariant = CultureInfo.InvariantCulture;
        var address = _isIPv6 ? ToIPEndPoint().Address.ToString() : string.Create(invariant, $"{(uint)_address >> 24}.{((uint)_address >> 16) & 0xFF}.{((uint)_address >> 8) & 0xFF}.{(uint)_address & 0xFF}");
        return writePort ? string.Create(invariant, $"{address}{portDelimiter}{_port}") : address;
    }

    /// <inheritdoc cref="ToString()"/>
    /// <remarks><paramref name="format"/> and <paramref name="formatProvider"/> are ignored.</remarks>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!IsAssigned)
        {
            charsWritten = UnassignedText.TryCopyTo(destination) ? UnassignedText.Length : 0;
            return charsWritten > 0;
        }
        var invariant = CultureInfo.InvariantCulture;
        if (!_isIPv6)
        {
            var a = (uint)_address;
            return destination.TryWrite(invariant, $"{a >> 24}.{(a >> 16) & 0xFF}.{(a >> 8) & 0xFF}.{a & 0xFF}:{_port}", out charsWritten);
        }
        var endPoint = ToIPEndPoint();
        return destination.TryWrite(invariant, $"[{endPoint.Address}]:{_port}", out charsWritten);
    }

    /// <summary>Parses the text <see cref="ToString()"/> writes.</summary>
    public static SystemAddress Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc cref="Parse(string, IFormatProvider?)"/>
    public static SystemAddress Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException($"'{s}' is not a SystemAddress.");

    /// <inheritdoc cref="Parse(string, IFormatProvider?)"/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out SystemAddress result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <summary>
    /// RakNet's text form, as <c>SystemAddress(const char*)</c> reads it: <see cref="TryFromString"/> on
    /// <see cref="Unassigned"/>, so a missing port is 0.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> s, char portDelimiter, out SystemAddress result) =>
        Unassigned.TryFromString(s, out result, portDelimiter);

    /// <summary>
    /// SystemAddress::FromString as RakNet's IPv4 build runs it, SetBinaryAddress (RakNetTypes.cpp:477-590), on a copy of
    /// this address: the parts the text leaves out keep this address's values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Text with no letter from g to z or A to Z is numeric: the leading digits and dots, up to 22 characters, are the
    /// address as inet_addr reads it (octal for a leading 0, one to four parts, 255.255.255.255 when invalid); then, if
    /// at least two characters follow, the character after the address is skipped, whatever it is, and up to 10 digits
    /// after it are the port, truncated to 16 bits. So <c>1.2.3.4|80</c>, <c>1.2.3.4:80</c> and <c>1.2.3.4 80</c> all
    /// read the same, and the result is always true.
    /// </para>
    /// <para>
    /// Other text must start with <c>localhost</c> in any case, which reads as 127.0.0.1 with the port, when anything
    /// follows, read by atoi from the tenth character on: <c>localhost9000</c> has port 9000 and <c>localhost|9000</c>
    /// port 0, as in RakNet. Any other host name gives false and <see cref="Unassigned"/>, as RakNet gives when its DNS
    /// lookup fails: this layer does no I/O, so names are resolved by <c>RakPeer</c>'s host overloads instead.
    /// </para>
    /// </remarks>
    public bool TryFromString(ReadOnlySpan<char> s, out SystemAddress result, char portDelimiter = RakNetPortDelimiter)
    {
        var end = s.IndexOf('\0');
        if (end >= 0)
            s = s[..end];
        result = this;
        if (s.IndexOfAnyInRange('g', 'z') >= 0 || s.IndexOfAnyInRange('A', 'Z') >= 0)
        {
            if (s.Length < 9 || !s[..9].Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                result = Unassigned;
                return false;
            }
            var port = s.Length > 9 ? (ushort)Atoi(s[9..]) : _port;
            result = new SystemAddress(0x7F000001u, port);
            return true;
        }

        var index = 0;
        while (index < s.Length && s[index] != portDelimiter && index < MaxAddressChars && (s[index] == '.' || char.IsAsciiDigit(s[index])))
            index++;
        var addressPart = s[..index];
        var portPart = ReadOnlySpan<char>.Empty;
        if (index + 1 < s.Length)
        {
            var start = ++index;
            while (index - start < MaxPortDigits && index < s.Length && index < MaxAddressChars + MaxPortDigits && char.IsAsciiDigit(s[index]))
                index++;
            portPart = s[start..index];
        }

        var isIPv6 = _isIPv6;
        var address = _address;
        var scopeId = _scopeId;
        if (!addressPart.IsEmpty)
        {
            (isIPv6, address, scopeId) = (false, InetAddr(addressPart), 0);
        }
        var newPort = portPart.IsEmpty ? _port : (ushort)Atoi(portPart);
        result = isIPv6 ? new SystemAddress(address, newPort, scopeId) : new SystemAddress((uint)address, newPort);
        return true;
    }

    /// <summary>
    /// SystemAddress::FromStringExplicitPort (RakNetTypes.cpp:734-745): <see cref="TryFromString"/> with no delimiter,
    /// then <paramref name="port"/>; <see cref="Unassigned"/> and false when the text is not an address.
    /// </summary>
    public static bool TryFromStringExplicitPort(ReadOnlySpan<char> s, ushort port, out SystemAddress result)
    {
        if (!Unassigned.TryFromString(s, out result, '\0'))
            return false;
        result = result.WithPort(port);
        return true;
    }

    // inet_addr as BSD's inet_aton reads the digits and dots SetBinaryAddress passes it: each part decimal, or octal with
    // a leading 0; a, a.b (b 24 bits), a.b.c (c 16 bits) or a.b.c.d; INADDR_NONE, 255.255.255.255, when invalid.
    private static uint InetAddr(ReadOnlySpan<char> s)
    {
        const uint none = uint.MaxValue;
        Span<uint> parts = stackalloc uint[3];
        var partCount = 0;
        var i = 0;
        uint value;
        while (true)
        {
            if (i >= s.Length || !char.IsAsciiDigit(s[i]))
                return none;
            var radix = 10u;
            if (s[i] == '0')
            {
                radix = 8;
                i++;
            }
            value = 0;
            for (; i < s.Length && char.IsAsciiDigit(s[i]); i++)
            {
                if (radix == 8 && s[i] >= '8')
                    return none;
                value = unchecked((value * radix) + (uint)(s[i] - '0'));
            }
            if (i < s.Length && s[i] == '.')
            {
                if (partCount >= 3 || value > 0xFF)
                    return none;
                parts[partCount++] = value;
                i++;
                continue;
            }
            break;
        }
        if (i < s.Length)
            return none;
        switch (partCount)
        {
            case 1:
                if (value > 0xFFFFFF)
                    return none;
                return (parts[0] << 24) | value;
            case 2:
                if (value > 0xFFFF)
                    return none;
                return (parts[0] << 24) | (parts[1] << 16) | value;
            case 3:
                if (value > 0xFF)
                    return none;
                return (parts[0] << 24) | (parts[1] << 16) | (parts[2] << 8) | value;
            default:
                return value;
        }
    }

    // atoi: leading white space, an optional sign, then decimal digits; strtol's clamping on overflow, then the int.
    private static int Atoi(ReadOnlySpan<char> s)
    {
        var i = 0;
        while (i < s.Length && s[i] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
            i++;
        var negative = false;
        if (i < s.Length && s[i] is '+' or '-')
            negative = s[i++] == '-';
        long value = 0;
        for (; i < s.Length && char.IsAsciiDigit(s[i]); i++)
        {
            var digit = s[i] - '0';
            if (value > (long.MaxValue - digit) / 10)
            {
                value = negative ? long.MinValue : long.MaxValue;
                return unchecked((int)value);
            }
            value = (value * 10) + digit;
        }
        return unchecked((int)(negative ? -value : value));
    }

    /// <inheritdoc cref="Parse(string, IFormatProvider?)"/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out SystemAddress result)
    {
        result = Unassigned;
        if (s.SequenceEqual(UnassignedText))
            return true;
        if (!IPEndPoint.TryParse(s, out var endPoint))
            return false;
        result = endPoint;
        return true;
    }
}
