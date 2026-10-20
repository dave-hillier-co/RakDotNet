using System.Buffers.Binary;
using System.Numerics;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::CookieJar (crypt/cookie/CookieJar.hpp, CookieJar.cpp): stateless cookies for the offline handshake, a
/// 6-round Salsa20 hash of the client's address and a 250 ms epoch under a random 512-bit key. Sans-IO: the
/// millisecond clock cat reads (Clock::msec_fast) is a parameter. Cookies are generated and verified by the same
/// peer, so the clock only has to be monotonic for that peer.
/// </summary>
internal sealed class CookieJar
{
    private const int ExpireTimeMs = 4000;
    private const int BinCount = 16;
    private const int BinTimeMs = ExpireTimeMs / BinCount;
    private const uint BinMask = BinCount - 1;
    private const int MaxAddressBytes = 48;

    private readonly uint[] _key = new uint[16];

    /// <summary>CookieJar::Initialize: draws the 512-bit key.</summary>
    public void Initialize(ISecureRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        Span<byte> bytes = stackalloc byte[64];
        random.Fill(bytes);
        for (var i = 0; i < 16; i++)
            _key[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
        bytes.Clear();
    }

    /// <summary>CookieJar::Generate(address_info, bytes).</summary>
    public uint Generate(ReadOnlySpan<byte> address, uint nowMs)
    {
        var epoch = nowMs / BinTimeMs;
        return (Hash(address, epoch) << 4) | (epoch & BinMask);
    }

    /// <summary>CookieJar::Verify(address_info, bytes, cookie).</summary>
    public bool Verify(ReadOnlySpan<byte> address, uint cookie, uint nowMs) =>
        Hash(address, ReconstructEpoch(cookie, nowMs)) << 4 == (cookie & ~BinMask);

    /// <summary>CookieJar::Hash(address_info, bytes, epoch): the address is added to key words 4 to 15.</summary>
    public uint Hash(ReadOnlySpan<byte> address, uint epoch)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(address.Length, MaxAddressBytes);
        Span<uint> x = stackalloc uint[16];
        _key.CopyTo(x);
        var y = 4;
        while (address.Length >= 4)
        {
            x[y++] += BinaryPrimitives.ReadUInt32LittleEndian(address);
            address = address[4..];
        }
        switch (address.Length)
        {
            case 3: x[y] += ((uint)address[2] << 16) | BinaryPrimitives.ReadUInt16LittleEndian(address); break;
            case 2: x[y] += BinaryPrimitives.ReadUInt16LittleEndian(address); break;
            case 1: x[y] += address[0]; break;
        }
        x[6] += epoch;
        x[10] += epoch;
        var hash = Salsa6(x);
        x.Clear();
        return hash;
    }

    private static uint ReconstructEpoch(uint cookie, uint nowMs)
    {
        var epoch = nowMs / BinTimeMs;
        var cookieBin = cookie & BinMask;
        var cookieEpoch = (epoch & ~BinMask) | cookieBin;
        if (cookieBin > (epoch & BinMask))
            cookieEpoch -= BinCount;
        return cookieEpoch;
    }

    // CookieJar::Salsa6 (CookieJar.cpp:40-80).
    private static uint Salsa6(Span<uint> x)
    {
        for (var i = 6; i > 0; i -= 2)
        {
            x[4] ^= Rol(x[0] + x[12], 7);
            x[8] ^= Rol(x[4] + x[0], 9);
            x[12] ^= Rol(x[8] + x[4], 13);
            x[0] ^= Rol(x[12] + x[8], 18);
            x[9] ^= Rol(x[5] + x[1], 7);
            x[13] ^= Rol(x[9] + x[5], 9);
            x[1] ^= Rol(x[13] + x[9], 13);
            x[5] ^= Rol(x[1] + x[13], 18);
            x[14] ^= Rol(x[10] + x[6], 7);
            x[2] ^= Rol(x[14] + x[10], 9);
            x[6] ^= Rol(x[2] + x[14], 13);
            x[10] ^= Rol(x[6] + x[2], 18);
            x[3] ^= Rol(x[15] + x[11], 7);
            x[7] ^= Rol(x[3] + x[15], 9);
            x[11] ^= Rol(x[7] + x[3], 13);
            x[15] ^= Rol(x[11] + x[7], 18);
            x[1] ^= Rol(x[0] + x[3], 7);
            x[2] ^= Rol(x[1] + x[0], 9);
            x[3] ^= Rol(x[2] + x[1], 13);
            x[0] ^= Rol(x[3] + x[2], 18);
            x[6] ^= Rol(x[5] + x[4], 7);
            x[7] ^= Rol(x[6] + x[5], 9);
            x[4] ^= Rol(x[7] + x[6], 13);
            x[5] ^= Rol(x[4] + x[7], 18);
            x[11] ^= Rol(x[10] + x[9], 7);
            x[8] ^= Rol(x[11] + x[10], 9);
            x[9] ^= Rol(x[8] + x[11], 13);
            x[10] ^= Rol(x[9] + x[8], 18);
            x[12] ^= Rol(x[15] + x[14], 7);
            x[13] ^= Rol(x[12] + x[15], 9);
            x[14] ^= Rol(x[13] + x[12], 13);
            x[15] ^= Rol(x[14] + x[13], 18);
        }
        return x[0] ^ x[5] ^ x[10] ^ x[15];
    }

    private static uint Rol(uint value, int bits) => BitOperations.RotateLeft(value, bits);
}
