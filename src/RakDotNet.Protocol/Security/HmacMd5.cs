using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::HMAC_MD5 (crypt/hash/HMAC_MD5.hpp, HMAC_MD5.cpp): the 8-byte MAC of AuthenticatedEncryption. A mutable
/// value type so a per-datagram MAC lives on the stack; pass it by reference.
/// </summary>
/// <remarks>
/// It is not the standard HMAC-MD5 and the BCL cannot stand in for it: the inner and outer states are keyed from
/// a fixed non-MD5 initial state (HMAC_MD5.cpp:190-193), message words are little-endian, and <see cref="End"/>
/// appends the 0x80 marker only when the length does not fit in the last block (HMAC_MD5.cpp:299-312).
/// </remarks>
internal struct HmacMd5
{
    private const int DigestBytes = 16;
    private const int WorkBytes = 64;

    private Md5State _cachedInitialState;
    private Md5State _cachedFinalState;
    private Md5State _state;
    private Block _work;
    private ulong _byteCounter;
    private int _usedBytes;

    /// <summary>HMAC_MD5::SetKey: keys from 32 bytes of the parent's output.</summary>
    public void SetKey(Skein parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _state[0] = 0x27b70a85;
        _state[1] = 0x2e1b2138;
        _state[2] = 0x4d2c6dfc;
        _state[3] = 0x53380d13;

        Span<byte> key = stackalloc byte[WorkBytes / 2];
        parent.Generate(key);
        Span<byte> work = _work;
        key.CopyTo(work);
        work[(WorkBytes / 2)..].Fill(0x36);
        HashComputation(ref _state, work, 1, ref _state);
        _cachedInitialState = _state;

        key.CopyTo(work);
        work[(WorkBytes / 2)..].Fill(0x5c);
        // HMAC_MD5.cpp:216 hashes from State, which now holds the inner state.
        HashComputation(ref _state, work, 1, ref _cachedFinalState);
        CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>HMAC_MD5::RekeyFromMD5: takes the parent's cached inner and outer states.</summary>
    public void RekeyFrom(in HmacMd5 parent)
    {
        _cachedInitialState = parent._cachedInitialState;
        _cachedFinalState = parent._cachedFinalState;
    }

    public void BeginMac()
    {
        _state = _cachedInitialState;
        _byteCounter = WorkBytes;
        _usedBytes = 0;
    }

    /// <summary>HMAC_MD5::Crunch (HMAC_MD5.cpp:238-285).</summary>
    public void Crunch(ReadOnlySpan<byte> message)
    {
        Span<byte> work = _work;
        var bytes = message.Length;
        _byteCounter += (ulong)bytes;
        if (_usedBytes != 0)
        {
            if (_usedBytes + bytes <= WorkBytes)
            {
                message.CopyTo(work[_usedBytes..]);
                _usedBytes += bytes;
                return;
            }
            var copied = WorkBytes - _usedBytes;
            message[..copied].CopyTo(work[_usedBytes..]);
            HashComputation(ref _state, work, 1, ref _state);
            message = message[copied..];
            bytes -= copied;
        }

        if (bytes > WorkBytes)
        {
            var eatBytes = bytes - 1;
            HashComputation(ref _state, message, eatBytes / WorkBytes, ref _state);
            eatBytes &= ~(WorkBytes - 1);
            message = message[eatBytes..];
        }

        message.CopyTo(work);
        _usedBytes = message.Length;
    }

    /// <summary>HMAC_MD5::End (HMAC_MD5.cpp:287-348).</summary>
    public void End()
    {
        Span<byte> work = _work;
        if (_usedBytes >= WorkBytes)
        {
            HashComputation(ref _state, work, 1, ref _state);
            work[0] = 0x80;
            _usedBytes = 1;
        }
        else if (_usedBytes > WorkBytes - 9)
        {
            work[_usedBytes++] = 0x80;
            work[_usedBytes..].Clear();
            HashComputation(ref _state, work, 1, ref _state);
            _usedBytes = 0;
        }

        work[_usedBytes..(WorkBytes - 8)].Clear();
        var bitCounter = _byteCounter << 3;
        BinaryPrimitives.WriteUInt32LittleEndian(work[(WorkBytes - 8)..], (uint)bitCounter);
        BinaryPrimitives.WriteUInt32LittleEndian(work[(WorkBytes - 4)..], (uint)(bitCounter >> 32));

        Md5State intermediate = default;
        HashComputation(ref _state, work, 1, ref intermediate);
        _state = _cachedFinalState;

        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(work[(i * 4)..], intermediate[i]);
        _byteCounter = 16;
        work[16] = 0x80;
        work[17..(WorkBytes - 8)].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(work[(WorkBytes - 8)..], 16 << 3);
        BinaryPrimitives.WriteUInt32LittleEndian(work[(WorkBytes - 4)..], 0);
        HashComputation(ref _state, work, 1, ref _state);
    }

    /// <summary>HMAC_MD5::Generate: up to the 16-byte digest, little-endian.</summary>
    public readonly void Generate(Span<byte> output)
    {
        var bytes = Math.Min(output.Length, DigestBytes);
        Span<byte> digest = stackalloc byte[DigestBytes];
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(digest[(i * 4)..], _state[i]);
        digest[..bytes].CopyTo(output);
    }

    public readonly byte[] CachedInitialStateBytes() => ToBytes(_cachedInitialState);

    public readonly byte[] CachedFinalStateBytes() => ToBytes(_cachedFinalState);

    /// <summary>Zeroes every state, as ~HMAC_MD5 does.</summary>
    public void Clear()
    {
        _cachedInitialState = default;
        _cachedFinalState = default;
        _state = default;
        ((Span<byte>)_work).Clear();
        _byteCounter = 0;
        _usedBytes = 0;
    }

    private static byte[] ToBytes(Md5State state)
    {
        var bytes = new byte[DigestBytes];
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), state[i]);
        return bytes;
    }

    // HMAC_MD5::HashComputation: the MD5 compression function over little-endian words, starting from `state`.
    private static void HashComputation(ref Md5State state, ReadOnlySpan<byte> message, int blocks, ref Md5State nextState)
    {
        uint a0 = state[0], b0 = state[1], c0 = state[2], d0 = state[3];
        Span<uint> x = stackalloc uint[16];
        while (blocks-- > 0)
        {
            for (var i = 0; i < 16; i++)
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(message[(i * 4)..]);
            uint a = a0, b = b0, c = c0, d = d0;

            a = FF(a, b, c, d, x[0], 7, 0xd76aa478); d = FF(d, a, b, c, x[1], 12, 0xe8c7b756);
            c = FF(c, d, a, b, x[2], 17, 0x242070db); b = FF(b, c, d, a, x[3], 22, 0xc1bdceee);
            a = FF(a, b, c, d, x[4], 7, 0xf57c0faf); d = FF(d, a, b, c, x[5], 12, 0x4787c62a);
            c = FF(c, d, a, b, x[6], 17, 0xa8304613); b = FF(b, c, d, a, x[7], 22, 0xfd469501);
            a = FF(a, b, c, d, x[8], 7, 0x698098d8); d = FF(d, a, b, c, x[9], 12, 0x8b44f7af);
            c = FF(c, d, a, b, x[10], 17, 0xffff5bb1); b = FF(b, c, d, a, x[11], 22, 0x895cd7be);
            a = FF(a, b, c, d, x[12], 7, 0x6b901122); d = FF(d, a, b, c, x[13], 12, 0xfd987193);
            c = FF(c, d, a, b, x[14], 17, 0xa679438e); b = FF(b, c, d, a, x[15], 22, 0x49b40821);

            a = GG(a, b, c, d, x[1], 5, 0xf61e2562); d = GG(d, a, b, c, x[6], 9, 0xc040b340);
            c = GG(c, d, a, b, x[11], 14, 0x265e5a51); b = GG(b, c, d, a, x[0], 20, 0xe9b6c7aa);
            a = GG(a, b, c, d, x[5], 5, 0xd62f105d); d = GG(d, a, b, c, x[10], 9, 0x02441453);
            c = GG(c, d, a, b, x[15], 14, 0xd8a1e681); b = GG(b, c, d, a, x[4], 20, 0xe7d3fbc8);
            a = GG(a, b, c, d, x[9], 5, 0x21e1cde6); d = GG(d, a, b, c, x[14], 9, 0xc33707d6);
            c = GG(c, d, a, b, x[3], 14, 0xf4d50d87); b = GG(b, c, d, a, x[8], 20, 0x455a14ed);
            a = GG(a, b, c, d, x[13], 5, 0xa9e3e905); d = GG(d, a, b, c, x[2], 9, 0xfcefa3f8);
            c = GG(c, d, a, b, x[7], 14, 0x676f02d9); b = GG(b, c, d, a, x[12], 20, 0x8d2a4c8a);

            a = HH(a, b, c, d, x[5], 4, 0xfffa3942); d = HH(d, a, b, c, x[8], 11, 0x8771f681);
            c = HH(c, d, a, b, x[11], 16, 0x6d9d6122); b = HH(b, c, d, a, x[14], 23, 0xfde5380c);
            a = HH(a, b, c, d, x[1], 4, 0xa4beea44); d = HH(d, a, b, c, x[4], 11, 0x4bdecfa9);
            c = HH(c, d, a, b, x[7], 16, 0xf6bb4b60); b = HH(b, c, d, a, x[10], 23, 0xbebfbc70);
            a = HH(a, b, c, d, x[13], 4, 0x289b7ec6); d = HH(d, a, b, c, x[0], 11, 0xeaa127fa);
            c = HH(c, d, a, b, x[3], 16, 0xd4ef3085); b = HH(b, c, d, a, x[6], 23, 0x04881d05);
            a = HH(a, b, c, d, x[9], 4, 0xd9d4d039); d = HH(d, a, b, c, x[12], 11, 0xe6db99e5);
            c = HH(c, d, a, b, x[15], 16, 0x1fa27cf8); b = HH(b, c, d, a, x[2], 23, 0xc4ac5665);

            a = II(a, b, c, d, x[0], 6, 0xf4292244); d = II(d, a, b, c, x[7], 10, 0x432aff97);
            c = II(c, d, a, b, x[14], 15, 0xab9423a7); b = II(b, c, d, a, x[5], 21, 0xfc93a039);
            a = II(a, b, c, d, x[12], 6, 0x655b59c3); d = II(d, a, b, c, x[3], 10, 0x8f0ccc92);
            c = II(c, d, a, b, x[10], 15, 0xffeff47d); b = II(b, c, d, a, x[1], 21, 0x85845dd1);
            a = II(a, b, c, d, x[8], 6, 0x6fa87e4f); d = II(d, a, b, c, x[15], 10, 0xfe2ce6e0);
            c = II(c, d, a, b, x[6], 15, 0xa3014314); b = II(b, c, d, a, x[13], 21, 0x4e0811a1);
            a = II(a, b, c, d, x[4], 6, 0xf7537e82); d = II(d, a, b, c, x[11], 10, 0xbd3af235);
            c = II(c, d, a, b, x[2], 15, 0x2ad7d2bb); b = II(b, c, d, a, x[9], 21, 0xeb86d391);

            a0 += a;
            b0 += b;
            c0 += c;
            d0 += d;
            message = message[WorkBytes..];
        }
        x.Clear();
        nextState[0] = a0;
        nextState[1] = b0;
        nextState[2] = c0;
        nextState[3] = d0;
    }

    private static uint Round(uint a, uint b, uint f, uint x, int s, uint ac) => BitOperations.RotateLeft(a + f + x + ac, s) + b;

    private static uint FF(uint a, uint b, uint c, uint d, uint x, int s, uint ac) => Round(a, b, (b & c) | (~b & d), x, s, ac);

    private static uint GG(uint a, uint b, uint c, uint d, uint x, int s, uint ac) => Round(a, b, (b & d) | (c & ~d), x, s, ac);

    private static uint HH(uint a, uint b, uint c, uint d, uint x, int s, uint ac) => Round(a, b, b ^ c ^ d, x, s, ac);

    private static uint II(uint a, uint b, uint c, uint d, uint x, int s, uint ac) => Round(a, b, c ^ (b | ~d), x, s, ac);

    [InlineArray(4)]
    private struct Md5State
    {
        private uint _element;
    }

    [InlineArray(WorkBytes)]
    private struct Block
    {
        private byte _element;
    }
}
