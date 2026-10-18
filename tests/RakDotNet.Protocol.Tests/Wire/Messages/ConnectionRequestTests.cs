using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class ConnectionRequestTests
{
    // RakPeer.cpp:5006-5031 (client) and 3449-3456, 3519-3520 (server): ID, client GUID, RakNet::Time,
    // doSecurity as an unsigned char (0 without LIBCAT_SECURITY), then the password as the remaining bytes.
    // No offline magic: this message travels inside the reliability layer.
    private const string Header = "09" + Codec.GuidHex + Codec.TimeHex + "00";

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
    public void Request_with_security_reports_the_flag_and_fails()
    {
        // RakPeer.cpp:5011-5025: with doSecurity a proof and optional identity follow, which are not supported.
        var bytes = Convert.FromHexString(Header);
        bytes[17] = 1;

        Assert.False(Codec.TryDecode<ConnectionRequest>(bytes, out var request));
        Assert.True(request!.DoSecurity);
        Assert.Equal(Codec.Guid, request.ClientGuid);
    }

    [Fact]
    public void Encoding_with_security_is_not_supported()
    {
        var request = new ConnectionRequest(1, 2, ReadOnlyMemory<byte>.Empty) { DoSecurity = true };

        Assert.Throws<NotSupportedException>(() => Codec.Encode(request));
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
