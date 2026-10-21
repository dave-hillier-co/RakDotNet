using System.Buffers;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>
/// What a send accepts. One parameter type covers RakNet's Send(char*), Send(BitStream*) and SendList.
/// </summary>
/// <remarks>
/// A ref struct, so sending neither boxes nor captures. Every send copies the bytes into a pooled segment before it
/// returns, so the caller's buffer is free afterwards. The bit length is kept exactly, because the encapsulated
/// message length on the wire is in bits (RakPeer.cpp:1408).
/// <para>
/// The steady-state send path allocates nothing: the send validates, takes a receipt number with
/// <see cref="Interlocked.Increment(ref uint)"/> (skipping 0), copies into a segment from the peer's pool and enqueues a
/// command struct on a preallocated MPSC ring, waking the loop only on an idle-to-busy transition or an Immediate send.
/// The cycle queues the message into the connection's preallocated priority heaps and frames datagrams into slab
/// buffers. Resend entries are pooled; reliable segments return to the pool on ACK, unreliable ones after framing.
/// </para>
/// </remarks>
public readonly ref struct OutgoingMessage
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly ReadOnlySequence<byte> _parts;
    private readonly bool _isSequence;
    private readonly int _bitLength;

    public OutgoingMessage(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitLength = checked(data.Length * 8);
    }

    /// <summary>A message whose final byte is partial, as Send(BitStream*) sends it.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bitLength"/> is negative or longer than <paramref name="data"/>.</exception>
    public OutgoingMessage(ReadOnlySpan<byte> data, int bitLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitLength, data.Length * 8);
        _data = data[..((bitLength + 7) / 8)];
        _bitLength = bitLength;
    }

    /// <summary>SendList: the parts are concatenated into the outgoing segment without an intermediate copy.</summary>
    public OutgoingMessage(in ReadOnlySequence<byte> parts)
    {
        _parts = parts;
        _isSequence = true;
        _bitLength = checked((int)parts.Length * 8);
    }

    public static implicit operator OutgoingMessage(ReadOnlySpan<byte> data) => new(data);

    public static implicit operator OutgoingMessage(byte[] data) => new(data);

    /// <summary><see cref="BitStreamWriter.Written"/> with <see cref="BitStreamWriter.BitsWritten"/>.</summary>
    public static implicit operator OutgoingMessage(BitStreamWriter writer) => new(writer.Written, writer.BitsWritten);

    /// <summary><see cref="BitStream.Written"/> with <see cref="BitStream.BitsWritten"/>.</summary>
    public static implicit operator OutgoingMessage(BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new(stream.Written, stream.BitsWritten);
    }

    public int BitLength => _bitLength;

    /// <summary>The bytes the message occupies, its last byte counted when partial (GetNumberOfBytesUsed).</summary>
    public int ByteLength => (_bitLength + 7) / 8;

    /// <summary>
    /// Copies the message into <paramref name="destination"/>, which holds at least <see cref="ByteLength"/> bytes: how a
    /// plugin host other than <see cref="RakPeer"/> reads what it is asked to send.
    /// </summary>
    public void CopyTo(Span<byte> destination)
    {
        if (_isSequence)
            _parts.CopyTo(destination);
        else
            _data.CopyTo(destination);
    }
}
