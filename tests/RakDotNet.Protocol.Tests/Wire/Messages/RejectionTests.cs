using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

/// <summary>
/// The connection rejections that are ID, aligned magic, server GUID: RakPeer.cpp:4556-4558 (banned),
/// 5334-5336 (already connected), 5351-5353 (no free incoming connections), 5371-5373 (IP recently connected).
/// The client parses all of them the same way at RakPeer.cpp:5084-5091.
/// </summary>
public abstract class RejectionTests<T>
    where T : IWireMessage<T>
{
    protected abstract MessageId Id { get; }

    protected abstract T Create(ulong serverGuid);

    protected abstract ulong ServerGuidOf(T message);

    private string ExpectedHex => Convert.ToHexStringLower([(byte)Id]) + Codec.Magic + Codec.GuidHex;

    [Fact]
    public void Rejection_encodes_as_id_magic_guid()
    {
        Assert.Equal(ExpectedHex, Codec.EncodeHex(Create(Codec.Guid)));
    }

    [Fact]
    public void Rejection_decodes_the_server_guid()
    {
        Assert.Equal(Codec.Guid, ServerGuidOf(Codec.Decode<T>(ExpectedHex)));
    }

    [Fact]
    public void Rejection_round_trips()
    {
        var message = Create(ulong.MaxValue);

        Assert.True(Codec.TryDecode<T>(Codec.Encode(message), out var decoded));
        Assert.Equal(message, decoded);
    }

    [Fact]
    public void Truncated_rejection_fails()
    {
        Codec.AssertAllTruncationsFail<T>(Convert.FromHexString(ExpectedHex));
    }

    [Fact]
    public void Rejection_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<T>(Convert.FromHexString(ExpectedHex), Id);
    }

    [Fact]
    public void Rejection_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<T>(Convert.FromHexString(ExpectedHex), 1);
    }
}

public class AlreadyConnectedTests : RejectionTests<AlreadyConnected>
{
    protected override MessageId Id => MessageId.AlreadyConnected;

    protected override AlreadyConnected Create(ulong serverGuid) => new(serverGuid);

    protected override ulong ServerGuidOf(AlreadyConnected message) => message.ServerGuid;
}

public class NoFreeIncomingConnectionsTests : RejectionTests<NoFreeIncomingConnections>
{
    protected override MessageId Id => MessageId.NoFreeIncomingConnections;

    protected override NoFreeIncomingConnections Create(ulong serverGuid) => new(serverGuid);

    protected override ulong ServerGuidOf(NoFreeIncomingConnections message) => message.ServerGuid;
}

public class ConnectionBannedTests : RejectionTests<ConnectionBanned>
{
    protected override MessageId Id => MessageId.ConnectionBanned;

    protected override ConnectionBanned Create(ulong serverGuid) => new(serverGuid);

    protected override ulong ServerGuidOf(ConnectionBanned message) => message.ServerGuid;
}

public class IpRecentlyConnectedTests : RejectionTests<IpRecentlyConnected>
{
    protected override MessageId Id => MessageId.IpRecentlyConnected;

    protected override IpRecentlyConnected Create(ulong serverGuid) => new(serverGuid);

    protected override ulong ServerGuidOf(IpRecentlyConnected message) => message.ServerGuid;
}

public class IncompatibleProtocolVersionTests
{
    // RakPeer.cpp:5141-5144: ID, the server's protocol version, aligned magic, server GUID.
    private const string RejectionHex = "19" + "06" + Codec.Magic + Codec.GuidHex;

    [Fact]
    public void Rejection_encodes_as_id_protocol_magic_guid()
    {
        Assert.Equal(RejectionHex, Codec.EncodeHex(new IncompatibleProtocolVersion(6, Codec.Guid)));
    }

    [Fact]
    public void Rejection_decodes_every_field()
    {
        var rejection = Codec.Decode<IncompatibleProtocolVersion>(RejectionHex);

        Assert.Equal(6, rejection.ProtocolVersion);
        Assert.Equal(Codec.Guid, rejection.ServerGuid);
    }

    [Fact]
    public void Rejection_round_trips()
    {
        var rejection = new IncompatibleProtocolVersion(11, 77);

        Assert.True(Codec.TryDecode<IncompatibleProtocolVersion>(Codec.Encode(rejection), out var decoded));
        Assert.Equal(rejection, decoded);
    }

    [Fact]
    public void Rejection_with_trailing_bytes_fails_as_the_cpp_client_requires_exactly_26_bytes()
    {
        // RakPeer.cpp:4619-4620 only treats it as an offline message when length == 2 + 8 + 16.
        Assert.False(Codec.TryDecode<IncompatibleProtocolVersion>(Convert.FromHexString(RejectionHex + "00"), out _));
    }

    [Fact]
    public void Truncated_rejection_fails()
    {
        Codec.AssertAllTruncationsFail<IncompatibleProtocolVersion>(Convert.FromHexString(RejectionHex));
    }

    [Fact]
    public void Rejection_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<IncompatibleProtocolVersion>(
            Convert.FromHexString(RejectionHex),
            MessageId.IncompatibleProtocolVersion
        );
    }

    [Fact]
    public void Rejection_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<IncompatibleProtocolVersion>(Convert.FromHexString(RejectionHex), 2);
    }
}
