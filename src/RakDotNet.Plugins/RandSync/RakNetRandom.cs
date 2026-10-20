using System.Buffers.Binary;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNetRandom: RakNet's MT19937 generator, Shawn Cokus's recode of the Mersenne Twister, with its seeding and output
/// bit for bit, so two systems seeded alike draw the same numbers whichever implementation they run.
/// </summary>
/// <remarks>
/// <para>
/// Ported from Rand.cpp. The seed is forced odd and the state filled by the Knuth generator x = 69069x, as seedMT does
/// (Rand.cpp:139-147); this is not the reference MT19937 initialisation, so its output differs from other MT19937
/// implementations for the same seed.
/// </para>
/// <para>
/// A generator that was never seeded starts from an all-zero state and returns zeros. RakNet leaves the state
/// uninitialised and, on the first draw, seeds the process-wide generator instead of this one (Rand.cpp:154-155), so
/// its output is undefined there. Not thread-safe.
/// </para>
/// </remarks>
public sealed class RakNetRandom
{
    private const int N = 624;
    private const int M = 397;
    private const uint K = 0x9908B0DFU;

    // state[N + 1] as in Rand.h; the extra word is never read.
    private readonly uint[] _state = new uint[N + 1];
    private int _next;
    private int _left = -1;

    /// <summary>SeedMT: restarts the sequence from <paramref name="seed"/>; even seeds are made odd.</summary>
    public void Seed(uint seed)
    {
        // Rand.cpp:139-147: x = seed | 1, state[0] = x, then state[i] = x *= 69069 for the other 623 words.
        var x = seed | 1U;
        _state[0] = x;
        for (var i = 1; i < N; i++)
        {
            x *= 69069U;
            _state[i] = x;
        }
        _left = 0;
    }

    /// <summary>RandomMT: the next 32-bit number.</summary>
    public uint NextUInt32()
    {
        // Rand.cpp:197-215.
        if (--_left < 0)
            return Reload();
        return Temper(_state[_next++]);
    }

    /// <summary>FrandomMT: the next number divided by 2^32, in [0, 1).</summary>
    public float NextSingle() => (float)(NextUInt32() / 4294967296.0);

    /// <summary>
    /// FillBufferMT: fills <paramref name="buffer"/> with numbers in little-endian order, as the C++ memcpy does on the
    /// platforms RakNet runs on. Like RakNet it draws one number more than whole words need, even when none of it is
    /// used (Rand.cpp:217-230).
    /// </summary>
    public void FillBuffer(Span<byte> buffer)
    {
        var offset = 0;
        while (buffer.Length - offset >= sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], NextUInt32());
            offset += sizeof(uint);
        }
        Span<byte> last = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(last, NextUInt32());
        last[..(buffer.Length - offset)].CopyTo(buffer[offset..]);
    }

    // reloadMT (Rand.cpp:158-195): regenerates the 624 words and returns the tempered first one.
    private uint Reload()
    {
        _left = N - 1;
        _next = 1;
        int p0 = 0, p2 = 2, pM = M;
        uint s0 = _state[0], s1 = _state[1];
        for (var j = N - M; j > 0; j--, s0 = s1, s1 = _state[p2++])
            _state[p0++] = _state[pM++] ^ (MixBits(s0, s1) >> 1) ^ ((s1 & 1U) != 0 ? K : 0U);
        pM = 0;
        for (var j = M - 1; j > 0; j--, s0 = s1, s1 = _state[p2++])
            _state[p0++] = _state[pM++] ^ (MixBits(s0, s1) >> 1) ^ ((s1 & 1U) != 0 ? K : 0U);
        s1 = _state[0];
        _state[p0] = _state[pM] ^ (MixBits(s0, s1) >> 1) ^ ((s1 & 1U) != 0 ? K : 0U);
        return Temper(s1);
    }

    private static uint MixBits(uint u, uint v) => (u & 0x80000000U) | (v & 0x7FFFFFFFU);

    private static uint Temper(uint y)
    {
        y ^= y >> 11;
        y ^= (y << 7) & 0x9D2C5680U;
        y ^= (y << 15) & 0xEFC60000U;
        return y ^ (y >> 18);
    }
}
