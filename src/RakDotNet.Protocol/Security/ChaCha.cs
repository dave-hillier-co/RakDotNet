using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::ChaChaKey (crypt/symmetric/ChaCha.hpp, privatekey/ChaCha.cpp): the key XORed little-endian into twelve
/// constant words. Not the BCL's ChaCha20: cat uses 12 rounds, its own constants and a 64-bit IV.
/// </summary>
internal struct ChaChaKey
{
    // ChaCha.cpp:42 InitialState: SHA-256 and BLAKE-32 constants.
    private static ReadOnlySpan<uint> InitialState =>
    [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
        0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
    ];

    internal KeyWords Words;

    /// <summary>ChaChaKey::Set: up to 48 bytes; a trailing partial word is ignored.</summary>
    public void Set(ReadOnlySpan<byte> key)
    {
        if (key.Length > 48)
            key = key[..48];
        InitialState.CopyTo(Words);
        for (var i = 0; i < key.Length / 4; i++)
            Words[i] ^= BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
    }

    public void Clear() => ((Span<uint>)Words).Clear();

    [InlineArray(12)]
    internal struct KeyWords
    {
        private uint _element;
    }
}

/// <summary>cat::ChaChaOutput: the keystream for one IV. A mutable value type; the block counter starts at 1.</summary>
internal struct ChaChaOutput
{
    private State _state;

    public ChaChaOutput(in ChaChaKey key, ulong iv)
    {
        ((ReadOnlySpan<uint>)key.Words).CopyTo(_state);
        _state[12] = 0;
        _state[13] = 0;
        _state[14] = (uint)iv;
        _state[15] = (uint)(iv >> 32);
    }

    /// <summary>ChaChaOutput::Crypt: XORs the keystream into <paramref name="input"/>; may run in place.</summary>
    public void Crypt(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The output is shorter than the input.", nameof(output));
        Span<byte> keyStream = stackalloc byte[64];
        while (input.Length > 0)
        {
            GenerateKeyStream(keyStream);
            var n = Math.Min(64, input.Length);
            for (var i = 0; i < n; i++)
                output[i] = (byte)(input[i] ^ keyStream[i]);
            input = input[n..];
            output = output[n..];
        }
        keyStream.Clear();
    }

    // ChaChaOutput::GenerateKeyStream (ChaCha.cpp:76-103).
    private void GenerateKeyStream(Span<byte> output)
    {
        if (++_state[12] == 0)
            _state[13]++;
        State x = _state;
        for (var round = 12; round > 0; round -= 2)
        {
            QuarterRound(ref x, 0, 4, 8, 12);
            QuarterRound(ref x, 1, 5, 9, 13);
            QuarterRound(ref x, 2, 6, 10, 14);
            QuarterRound(ref x, 3, 7, 11, 15);
            QuarterRound(ref x, 0, 5, 10, 15);
            QuarterRound(ref x, 1, 6, 11, 12);
            QuarterRound(ref x, 2, 7, 8, 13);
            QuarterRound(ref x, 3, 4, 9, 14);
        }
        for (var i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(output[(i * 4)..], x[i] + _state[i]);
        ((Span<uint>)x).Clear();
    }

    private static void QuarterRound(ref State x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }

    [InlineArray(16)]
    private struct State
    {
        private uint _element;
    }
}
