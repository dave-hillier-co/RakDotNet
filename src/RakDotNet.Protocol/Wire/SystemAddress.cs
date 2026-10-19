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
/// scope id is kept for <see cref="ToIPEndPoint"/> but not compared.
/// </remarks>
public readonly struct SystemAddress : IEquatable<SystemAddress>, ISpanFormattable, ISpanParsable<SystemAddress>
{
    private const string UnassignedText = "UNASSIGNED_SYSTEM_ADDRESS";

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
    /// Copies the address out of a <see cref="SocketAddress"/> filled by <c>Socket.ReceiveFrom</c>, without allocating.
    /// </summary>
    public static SystemAddress FromSocketAddress(SocketAddress address) => throw new NotImplementedException();

    /// <summary>Writes this address into a <see cref="SocketAddress"/> for <c>Socket.SendTo</c>, without allocating.</summary>
    public void CopyTo(SocketAddress destination) => throw new NotImplementedException();

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
    /// <c>a.b.c.d:port</c> as the interop protocol writes addresses, <c>[address]:port</c> for IPv6, or
    /// <c>UNASSIGNED_SYSTEM_ADDRESS</c> as SystemAddress::ToString writes it (RakNetTypes.cpp:264-267).
    /// </summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[64];
        TryFormat(buffer, out var written, default, null);
        return buffer[..written].ToString();
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
