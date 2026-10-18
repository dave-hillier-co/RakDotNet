using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

// ID_INVALID_PASSWORD, sent reliably over the new connection: ID then the server GUID, no magic (RakPeer.cpp:3527-3529).
public class InvalidPasswordTests
{
    private const string ExpectedHex = "18" + Codec.GuidHex;

    [Fact]
    public void Invalid_password_encodes_as_id_and_guid()
    {
        Assert.Equal(ExpectedHex, Codec.EncodeHex(new InvalidPassword(Codec.Guid)));
    }

    [Fact]
    public void Invalid_password_decodes_the_server_guid()
    {
        Assert.Equal(Codec.Guid, Codec.Decode<InvalidPassword>(ExpectedHex).ServerGuid);
    }

    [Fact]
    public void Truncated_invalid_password_fails()
    {
        Codec.AssertAllTruncationsFail<InvalidPassword>(Convert.FromHexString(ExpectedHex));
    }

    [Fact]
    public void Invalid_password_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<InvalidPassword>(Convert.FromHexString(ExpectedHex), MessageId.InvalidPassword);
    }
}
