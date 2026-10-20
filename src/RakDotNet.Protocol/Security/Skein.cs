using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::Skein (crypt/hash/Skein.hpp, Skein.cpp, Skein256.cpp) with the 256-bit state, the only one the key
/// agreement and AuthenticatedEncryption use (KeyAgreementCommon BITS = 256), in key, MAC and KDF modes.
/// </summary>
/// <remarks>
/// cat's Skein differs from the published hash in ways that are on the wire, and they are kept: a crunch longer
/// than one block hashes its whole blocks straight from the message and advances the tweak by the crunch's entire
/// remaining length for each of them (Skein.cpp:194, Skein256.cpp:62), the final block is always kept back for
/// <see cref="End"/>, and output blocks continue a counter across <see cref="Generate"/> calls. The way input is
/// split into <see cref="Crunch"/> calls therefore changes the result, so callers crunch exactly as cat does.
/// </remarks>
internal sealed class Skein
{
    private const int DigestBytes = 32;
    private const int DigestWords = 4;

    // Skein.hpp:47-67 tweak word 1 fields and block types.
    private const int T1PosBlockType = 120 - 64;
    private const ulong T1MaskFirst = 1UL << (126 - 64);
    private const ulong T1MaskFinal = 1UL << (127 - 64);
    private const ulong BlockTypeKey = 0;
    private const ulong BlockTypeKdf = 16;
    private const ulong BlockTypeMessage = 48;
    private const ulong BlockTypeOutput = 63;

    // Skein.cpp:62 State0_256, the cached initial state for 256-bit output.
    private static ReadOnlySpan<ulong> InitialState256 =>
        [0x388512680e660046UL, 0x4b72d5dec5a8ff01UL, 0x281a9298ca5eb3a5UL, 0x54ca5249f46070c4UL];

    private readonly ulong[] _state = new ulong[DigestWords];
    private readonly ulong[] _tweak = new ulong[2];
    private readonly byte[] _work = new byte[DigestBytes];
    private int _usedBytes;
    private ulong _outputBlockCounter;

    /// <summary>Skein::BeginKey: starts a new key. False for any size but 256 bits.</summary>
    public bool BeginKey(int bits)
    {
        if (bits != 256)
            return false;
        InitialState256.CopyTo(_state);
        Begin(BlockTypeKey);
        return true;
    }

    /// <summary>Skein::SetKey: continues from the parent's chaining state (its key).</summary>
    public void SetKey(Skein parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        parent._state.CopyTo(_state, 0);
    }

    public void BeginMac() => Begin(BlockTypeMessage);

    public void BeginKdf() => Begin(BlockTypeKdf);

    /// <summary>Skein::Crunch (Skein.cpp:160-205).</summary>
    public void Crunch(ReadOnlySpan<byte> message)
    {
        var bytes = message.Length;
        if (_usedBytes != 0)
        {
            if (_usedBytes + bytes <= DigestBytes)
            {
                message.CopyTo(_work.AsSpan(_usedBytes));
                _usedBytes += bytes;
                return;
            }
            var copied = DigestBytes - _usedBytes;
            message[..copied].CopyTo(_work.AsSpan(_usedBytes));
            HashComputation(_work, 1, DigestBytes, _state);
            message = message[copied..];
            bytes -= copied;
        }

        if (bytes > DigestBytes)
        {
            var eatBytes = bytes - 1;
            // Skein.cpp:194 passes the remaining length as every block's byte count.
            HashComputation(message, eatBytes / DigestBytes, (uint)bytes, _state);
            eatBytes &= ~(DigestBytes - 1);
            message = message[eatBytes..];
        }

        message.CopyTo(_work);
        _usedBytes = message.Length;
    }

    /// <summary>ICryptHash::CrunchString: the bytes of <paramref name="s"/> and its terminating NUL.</summary>
    public void CrunchString(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        Span<byte> bytes = stackalloc byte[Encoding.ASCII.GetByteCount(s) + 1];
        Encoding.ASCII.GetBytes(s, bytes);
        bytes[^1] = 0;
        Crunch(bytes);
    }

    /// <summary>Skein::End: pads and hashes the final block, and restarts the output counter.</summary>
    public void End()
    {
        _work.AsSpan(_usedBytes).Clear();
        _tweak[1] |= T1MaskFinal;
        HashComputation(_work, 1, (uint)_usedBytes, _state);
        _outputBlockCounter = 0;
    }

    /// <summary>Skein::Generate without strengthening (Skein.cpp:221-295), little-endian output.</summary>
    public void Generate(Span<byte> output)
    {
        Span<ulong> finalMessage = stackalloc ulong[DigestWords];
        Span<ulong> block = stackalloc ulong[DigestWords];
        Span<byte> blockBytes = stackalloc byte[DigestBytes];
        while (output.Length > 0)
        {
            finalMessage.Clear();
            finalMessage[0] = _outputBlockCounter;
            _tweak[0] = 0;
            _tweak[1] = T1MaskFirst | T1MaskFinal | (BlockTypeOutput << T1PosBlockType);
            WordsToBytes(finalMessage, blockBytes);
            HashComputation(blockBytes, 1, 8, block);
            WordsToBytes(block, blockBytes);
            var n = Math.Min(output.Length, DigestBytes);
            blockBytes[..n].CopyTo(output);
            output = output[n..];
            _outputBlockCounter++;
        }
        CryptographicOperations.ZeroMemory(blockBytes);
        block.Clear();
    }

    /// <summary>Zeroes the state, as ~Skein does.</summary>
    public void Clear()
    {
        Array.Clear(_state);
        Array.Clear(_tweak);
        CryptographicOperations.ZeroMemory(_work);
    }

    private void Begin(ulong blockType)
    {
        _tweak[0] = 0;
        _tweak[1] = T1MaskFirst | (blockType << T1PosBlockType);
        _usedBytes = 0;
    }

    private static void WordsToBytes(ReadOnlySpan<ulong> words, Span<byte> bytes)
    {
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[(i * 8)..], words[i]);
    }

    // Skein::HashComputation256 (Skein256.cpp): Threefish-256, 72 rounds, with cat's per-block byte count.
    private void HashComputation(ReadOnlySpan<byte> message, int blocks, uint byteCount, Span<ulong> nextState)
    {
        Span<ulong> k = stackalloc ulong[5];
        _state.AsSpan().CopyTo(k);
        var t0 = _tweak[0];
        var t1 = _tweak[1];
        do
        {
            t0 += byteCount;
            var t2 = t0 ^ t1;
            k[4] = 0x1BD11BDAA9FC1A22UL ^ k[0] ^ k[1] ^ k[2] ^ k[3];

            var m0 = BinaryPrimitives.ReadUInt64LittleEndian(message);
            var m1 = BinaryPrimitives.ReadUInt64LittleEndian(message[8..]);
            var m2 = BinaryPrimitives.ReadUInt64LittleEndian(message[16..]);
            var m3 = BinaryPrimitives.ReadUInt64LittleEndian(message[24..]);
            var x0 = k[0] + m0;
            var x1 = k[1] + m1 + t0;
            var x2 = k[2] + m2 + t1;
            var x3 = k[3] + m3;

            Span<ulong> t = [t0, t1, t2];
            for (var round = 1; round <= 18; round += 6)
            {
                Mix(ref x0, ref x1, ref x2, ref x3, 14, 16, 52, 57);
                Mix(ref x0, ref x1, ref x2, ref x3, 23, 40, 5, 37);
                Inject(ref x0, ref x1, ref x2, ref x3, k, t, round);
                Mix(ref x0, ref x1, ref x2, ref x3, 25, 33, 46, 12);
                Mix(ref x0, ref x1, ref x2, ref x3, 58, 22, 32, 32);
                Inject(ref x0, ref x1, ref x2, ref x3, k, t, round + 1);
                Mix(ref x0, ref x1, ref x2, ref x3, 14, 16, 52, 57);
                Mix(ref x0, ref x1, ref x2, ref x3, 23, 40, 5, 37);
                Inject(ref x0, ref x1, ref x2, ref x3, k, t, round + 2);
                Mix(ref x0, ref x1, ref x2, ref x3, 25, 33, 46, 12);
                Mix(ref x0, ref x1, ref x2, ref x3, 58, 22, 32, 32);
                Inject(ref x0, ref x1, ref x2, ref x3, k, t, round + 3);
                Mix(ref x0, ref x1, ref x2, ref x3, 14, 16, 52, 57);
                Mix(ref x0, ref x1, ref x2, ref x3, 23, 40, 5, 37);
                Inject(ref x0, ref x1, ref x2, ref x3, k, t, round + 4);
                Mix(ref x0, ref x1, ref x2, ref x3, 25, 33, 46, 12);
                Mix(ref x0, ref x1, ref x2, ref x3, 58, 22, 32, 32);
                Inject(ref x0, ref x1, ref x2, ref x3, k, t, round + 5);
            }

            k[0] = x0 ^ m0;
            k[1] = x1 ^ m1;
            k[2] = x2 ^ m2;
            k[3] = x3 ^ m3;
            t1 &= ~T1MaskFirst;
            message = message[DigestBytes..];
        } while (--blocks > 0);

        _tweak[0] = t0;
        _tweak[1] = t1;
        k[..DigestWords].CopyTo(nextState);
        k.Clear();
    }

    // Skein256.cpp THREEFISH(R0, R1, R2, R3): two rounds of two MIXes.
    private static void Mix(ref ulong x0, ref ulong x1, ref ulong x2, ref ulong x3, int r0, int r1, int r2, int r3)
    {
        x0 += x1; x1 = BitOperations.RotateLeft(x1, r0); x1 ^= x0;
        x2 += x3; x3 = BitOperations.RotateLeft(x3, r1); x3 ^= x2;
        x0 += x3; x3 = BitOperations.RotateLeft(x3, r2); x3 ^= x0;
        x2 += x1; x1 = BitOperations.RotateLeft(x1, r3); x1 ^= x2;
    }

    // Skein256.cpp INJECTKEY with subkey s = round: k[s..s+3 mod 5], tweaks t[s mod 3], t[s+1 mod 3], and s.
    private static void Inject(ref ulong x0, ref ulong x1, ref ulong x2, ref ulong x3, ReadOnlySpan<ulong> k, ReadOnlySpan<ulong> t, int s)
    {
        x0 += k[s % 5];
        x1 += k[(s + 1) % 5] + t[s % 3];
        x2 += k[(s + 2) % 5] + t[(s + 1) % 3];
        x3 += k[(s + 3) % 5] + (ulong)s;
    }
}
