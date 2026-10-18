using System.Diagnostics.CodeAnalysis;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

/// <summary>Encode/decode helpers shared by the message codec tests.</summary>
internal static class Codec
{
    // Bytes reused across tests, so that expected hex strings stay readable.
    public const string Magic = "00ffff00fefefefefdfdfdfd12345678";
    public const ulong Guid = 0x0102030405060708;
    public const string GuidHex = "0102030405060708";
    public const ulong Time = 0x1122334455667788;
    public const string TimeHex = "1122334455667788";
    public const ulong OtherTime = 0x99AABBCCDDEEFF00;
    public const string OtherTimeHex = "99aabbccddeeff00";

    public static byte[] Encode<T>(T message, int bufferSize = 2048)
        where T : IWireMessage<T>
    {
        var buffer = new byte[bufferSize];
        var writer = new BitStreamWriter(buffer);
        message.Write(ref writer);
        return writer.Written.ToArray();
    }

    public static string EncodeHex<T>(T message)
        where T : IWireMessage<T> => Convert.ToHexStringLower(Encode(message));

    public static bool TryDecode<T>(ReadOnlySpan<byte> bytes, [MaybeNullWhen(false)] out T value)
        where T : IWireMessage<T>
    {
        var reader = new BitStreamReader(bytes);
        return T.TryRead(ref reader, out value);
    }

    public static T Decode<T>(string hex)
        where T : IWireMessage<T>
    {
        Assert.True(TryDecode<T>(Convert.FromHexString(hex), out var value), $"{typeof(T).Name} failed to decode {hex}");
        return value;
    }

    /// <summary>Asserts that every prefix of <paramref name="bytes"/> shorter than <paramref name="minimumLength"/> fails.</summary>
    public static void AssertPrefixesShorterThanFail<T>(byte[] bytes, int minimumLength)
        where T : IWireMessage<T>
    {
        for (var length = 0; length < minimumLength; length++)
            Assert.False(TryDecode<T>(bytes.AsSpan(0, length), out _), $"{typeof(T).Name} decoded a {length}-byte prefix");
    }

    /// <summary>Asserts that every truncation of a complete message fails.</summary>
    public static void AssertAllTruncationsFail<T>(byte[] bytes)
        where T : IWireMessage<T> => AssertPrefixesShorterThanFail<T>(bytes, bytes.Length);

    /// <summary>Asserts that replacing the first byte with any ID not in <paramref name="acceptedIds"/> fails.</summary>
    public static void AssertOtherIdsFail<T>(byte[] bytes, params MessageId[] acceptedIds)
        where T : IWireMessage<T>
    {
        var copy = bytes.ToArray();
        for (var id = 0; id <= byte.MaxValue; id++)
        {
            if (acceptedIds.Contains((MessageId)id))
                continue;
            copy[0] = (byte)id;
            Assert.False(TryDecode<T>(copy, out _), $"{typeof(T).Name} accepted ID 0x{id:x2}");
        }
    }

    /// <summary>Asserts that corrupting any byte of the offline magic at <paramref name="offset"/> fails.</summary>
    public static void AssertCorruptMagicFails<T>(byte[] bytes, int offset)
        where T : IWireMessage<T>
    {
        for (var i = 0; i < OfflineMagic.Length; i++)
        {
            var copy = bytes.ToArray();
            copy[offset + i] ^= 0x01;
            Assert.False(TryDecode<T>(copy, out _), $"{typeof(T).Name} accepted magic corrupted at byte {i}");
        }
    }
}
