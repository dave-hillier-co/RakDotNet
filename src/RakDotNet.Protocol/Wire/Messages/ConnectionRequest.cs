using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_CONNECTION_REQUEST, sent reliably once the offline handshake is done: ID, client GUID, RakNet::Time,
/// doSecurity as one byte, then the connection password as the remaining bytes (RakPeer.cpp:5006-5031; parsed by
/// ParseConnectionRequestPacket at RakPeer.cpp:3447-3456 and 3519-3520). It carries no offline magic.
/// </summary>
/// <remarks>
/// <para>
/// With <see cref="DoSecurity"/> the client's 32-byte key proof follows the flag, aligned, then doIdentity as one byte
/// and, when it is 1, the 160-byte identity proof of two-way authentication; the password comes last
/// (RakPeer.cpp:5010-5025, read at 3458-3517).
/// </para>
/// <para>
/// <see cref="TryRead"/> reads the layout the flag announces, as a server with security does; a server without
/// security reads every byte after the flag as the password, which <see cref="TryReadWithoutSecurity"/> does.
/// </para>
/// </remarks>
public sealed record ConnectionRequest(ulong ClientGuid, ulong RequestTime, ReadOnlyMemory<byte> Password)
    : IWireMessage<ConnectionRequest>
{
    /// <summary>cat::EasyHandshake::PROOF_BYTES (EasyHandshake.hpp:217).</summary>
    public const int ProofLength = 32;

    /// <summary>cat::EasyHandshake::IDENTITY_BYTES (EasyHandshake.hpp:219).</summary>
    public const int IdentityLength = 160;

    /// <summary>doSecurity: the key proof, and possibly an identity, follow the flag.</summary>
    public bool DoSecurity { get; init; }

    /// <summary>The client's proof of the shared key, <see cref="ProofLength"/> bytes when <see cref="DoSecurity"/>.</summary>
    public ReadOnlyMemory<byte> Proof { get; init; }

    /// <summary>The client's identity proof (two-way authentication), <see cref="IdentityLength"/> bytes, or empty.</summary>
    public ReadOnlyMemory<byte> Identity { get; init; }

    /// <exception cref="ArgumentOutOfRangeException">With <see cref="DoSecurity"/>, the proof is not
    /// <see cref="ProofLength"/> bytes or the identity is neither empty nor <see cref="IdentityLength"/> bytes.</exception>
    public void Write(ref BitStreamWriter writer)
    {
        if (DoSecurity)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(Proof.Length, ProofLength, nameof(Proof));
            if (!Identity.IsEmpty)
                ArgumentOutOfRangeException.ThrowIfNotEqual(Identity.Length, IdentityLength, nameof(Identity));
        }
        MessageIdField.Write(ref writer, MessageId.ConnectionRequest);
        writer.WriteUInt64(ClientGuid);
        writer.WriteUInt64(RequestTime);
        writer.WriteByte(DoSecurity ? (byte)1 : (byte)0); // RakPeer.cpp:5011
        if (DoSecurity)
        {
            writer.WriteAlignedBytes(Proof.Span);
            writer.WriteByte(Identity.IsEmpty ? (byte)0 : (byte)1); // RakPeer.cpp:5019
            if (!Identity.IsEmpty)
                writer.WriteAlignedBytes(Identity.Span);
        }
        writer.WriteBytes(Password.Span);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out ConnectionRequest value)
    {
        value = null;
        var r = reader;
        if (!TryReadHeader(ref r, out var guid, out var time, out var doSecurity))
            return false;
        var proof = ReadOnlyMemory<byte>.Empty;
        var identity = ReadOnlyMemory<byte>.Empty;
        if (doSecurity == 1)
        {
            if (!r.TryReadAlignedBytes(ProofLength, out var proofBytes) || !r.TryReadByte(out var doIdentity))
                return false;
            proof = proofBytes.ToArray();
            if (doIdentity == 1)
            {
                if (!r.TryReadAlignedBytes(IdentityLength, out var identityBytes))
                    return false;
                identity = identityBytes.ToArray();
            }
        }
        if (!TryReadPassword(ref r, out var password))
            return false;
        value = new ConnectionRequest(guid, time, password) { DoSecurity = doSecurity == 1, Proof = proof, Identity = identity };
        reader = r;
        return true;
    }

    /// <summary>The request as a server without security reads it: whatever the flag, the password is every byte after it.</summary>
    public static bool TryReadWithoutSecurity(ref BitStreamReader reader, [MaybeNullWhen(false)] out ConnectionRequest value)
    {
        value = null;
        var r = reader;
        if (!TryReadHeader(ref r, out var guid, out var time, out var doSecurity) || !TryReadPassword(ref r, out var password))
            return false;
        value = new ConnectionRequest(guid, time, password) { DoSecurity = doSecurity == 1 };
        reader = r;
        return true;
    }

    private static bool TryReadHeader(ref BitStreamReader reader, out ulong guid, out ulong time, out byte doSecurity)
    {
        guid = 0;
        time = 0;
        doSecurity = 0;
        return MessageIdField.TryRead(ref reader, MessageId.ConnectionRequest)
            && reader.TryReadUInt64(out guid)
            && reader.TryReadUInt64(out time)
            && reader.TryReadByte(out doSecurity);
    }

    // RakPeer.cpp:3519-3520: the password is every byte after what was read.
    private static bool TryReadPassword(ref BitStreamReader reader, out ReadOnlyMemory<byte> password)
    {
        password = ReadOnlyMemory<byte>.Empty;
        if (!reader.TryReadAlignedBytes(reader.BitsRemaining / 8, out var bytes))
            return false;
        password = bytes.ToArray();
        return true;
    }
}
