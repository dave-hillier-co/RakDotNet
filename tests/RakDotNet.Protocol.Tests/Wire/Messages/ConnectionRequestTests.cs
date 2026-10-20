using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class ConnectionRequestTests
{
    // RakPeer.cpp:5006-5031 (client) and 3449-3456, 3519-3520 (server): ID, client GUID, RakNet::Time,
    // doSecurity as an unsigned char (0 without LIBCAT_SECURITY), then the password as the remaining bytes.
    // No offline magic: this message travels inside the reliability layer.
    private const string Header = "09" + Codec.GuidHex + Codec.TimeHex + "00";
    private const string SecureHeader = "09" + Codec.GuidHex + Codec.TimeHex + "01";

    [Fact]
    public void Request_without_a_password_encodes_as_id_guid_time_security_byte()
    {
        Assert.Equal(Header, Codec.EncodeHex(new ConnectionRequest(Codec.Guid, Codec.Time, ReadOnlyMemory<byte>.Empty)));
    }

    [Fact]
    public void Password_follows_as_raw_bytes()
    {
        var request = new ConnectionRequest(Codec.Guid, Codec.Time, "pw"u8.ToArray());

        Assert.Equal(Header + "7077", Codec.EncodeHex(request));
    }

    [Fact]
    public void Request_decodes_every_field()
    {
        var request = Codec.Decode<ConnectionRequest>(Header + "7077");

        Assert.Equal(Codec.Guid, request.ClientGuid);
        Assert.Equal(Codec.Time, request.RequestTime);
        Assert.False(request.DoSecurity);
        Assert.Equal("pw"u8.ToArray(), request.Password.ToArray());
    }

    [Fact]
    public void Request_round_trips()
    {
        var request = new ConnectionRequest(4, 5, new byte[] { 1, 2, 3 });

        Assert.True(Codec.TryDecode<ConnectionRequest>(Codec.Encode(request), out var decoded));
        Assert.Equal(request.ClientGuid, decoded.ClientGuid);
        Assert.Equal(request.RequestTime, decoded.RequestTime);
        Assert.Equal(request.Password.ToArray(), decoded.Password.ToArray());
    }

    [Fact]
    public void Request_with_security_carries_the_proof_then_no_identity_then_the_password()
    {
        // RakPeer.cpp:5010-5031: doSecurity 1, the 32-byte proof aligned, doIdentity 0, then the password.
        var proof = Enumerable.Range(0, ConnectionRequest.ProofLength).Select(i => (byte)(i + 1)).ToArray();
        var request = new ConnectionRequest(Codec.Guid, Codec.Time, "pw"u8.ToArray()) { DoSecurity = true, Proof = proof };

        Assert.Equal(SecureHeader + Convert.ToHexStringLower(proof) + "00" + "7077", Codec.EncodeHex(request));
    }

    [Fact]
    public void Request_with_an_identity_carries_it_after_the_proof()
    {
        // RakPeer.cpp:5019-5025: doIdentity 1 and the 160-byte identity proof, aligned.
        var proof = new byte[ConnectionRequest.ProofLength];
        var identity = Enumerable.Range(0, ConnectionRequest.IdentityLength).Select(i => (byte)i).ToArray();
        var request = new ConnectionRequest(Codec.Guid, Codec.Time, ReadOnlyMemory<byte>.Empty) { DoSecurity = true, Proof = proof, Identity = identity };

        Assert.Equal(SecureHeader + Convert.ToHexStringLower(proof) + "01" + Convert.ToHexStringLower(identity), Codec.EncodeHex(request));
    }

    [Fact]
    public void Request_with_security_decodes_proof_identity_and_password()
    {
        var proof = Enumerable.Range(0, ConnectionRequest.ProofLength).Select(i => (byte)(255 - i)).ToArray();
        var identity = Enumerable.Range(0, ConnectionRequest.IdentityLength).Select(i => (byte)(i * 7)).ToArray();

        var request = Codec.Decode<ConnectionRequest>(SecureHeader + Convert.ToHexStringLower(proof) + "01" + Convert.ToHexStringLower(identity) + "7077");

        Assert.True(request.DoSecurity);
        Assert.Equal(proof, request.Proof.ToArray());
        Assert.Equal(identity, request.Identity.ToArray());
        Assert.Equal("pw"u8.ToArray(), request.Password.ToArray());
    }

    [Fact]
    public void Request_with_security_cut_short_of_its_proof_fails()
    {
        Codec.AssertPrefixesShorterThanFail<ConnectionRequest>(Convert.FromHexString(SecureHeader + new string('0', 64) + "00"), 18 + 32 + 1);
    }

    [Fact]
    public void A_server_without_security_reads_everything_after_the_flag_as_the_password()
    {
        // RakPeer.cpp:3519-3520 without _using_security: the proof and identity become part of the password.
        var bytes = Convert.FromHexString(SecureHeader + "aabb");
        var reader = new BitStreamReader(bytes);

        Assert.True(ConnectionRequest.TryReadWithoutSecurity(ref reader, out var request));
        Assert.True(request.DoSecurity);
        Assert.Equal(new byte[] { 0xaa, 0xbb }, request.Password.ToArray());
        Assert.True(request.Proof.IsEmpty);
    }

    [Fact]
    public void Encoding_with_security_needs_a_whole_proof()
    {
        var request = new ConnectionRequest(1, 2, ReadOnlyMemory<byte>.Empty) { DoSecurity = true };

        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Encode(request));
    }

    [Fact]
    public void Request_shorter_than_its_header_fails()
    {
        Codec.AssertPrefixesShorterThanFail<ConnectionRequest>(Convert.FromHexString(Header + "7077"), 18);
    }

    [Fact]
    public void Request_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<ConnectionRequest>(Convert.FromHexString(Header), MessageId.ConnectionRequest);
    }
}
