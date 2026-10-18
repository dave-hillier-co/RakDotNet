using System.Net;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class ConnectionRequestAcceptedTests
{
    // RakPeer.cpp:3543-3551: ID, the client's address, SystemIndex (unsigned short, RakNetTypes.h:93),
    // MAXIMUM_NUMBER_OF_INTERNAL_IDS (10, RakNetDefines.h:76) addresses from ipList, the echoed request time,
    // the server's time. Unused ipList entries are UNASSIGNED_SYSTEM_ADDRESS, which is 0.0.0.0:0
    // (RakNetTypes.cpp:378-385, RakPeer.cpp:232) and serialises as 04 ffffffff 0000.
    private const string Unassigned = "04ffffffff0000";
    private const string Loopback19132 = "0480fffffe4abc";
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 19132);

    private static string ExpectedHex(IEnumerable<string> addresses) =>
        "10" + Loopback19132 + "0003" + string.Concat(addresses) + Codec.TimeHex + Codec.OtherTimeHex;

    [Fact]
    public void Accepted_encodes_with_ten_internal_addresses()
    {
        var accepted = new ConnectionRequestAccepted(
            Client,
            3,
            InternalAddressList.CreateUnassigned(),
            Codec.Time,
            Codec.OtherTime
        );

        var bytes = Codec.Encode(accepted);

        Assert.Equal(ExpectedHex(Enumerable.Repeat(Unassigned, 10)), Convert.ToHexStringLower(bytes));
        Assert.Equal(96, bytes.Length);
    }

    [Fact]
    public void Accepted_decodes_every_field()
    {
        var addresses = Enumerable.Repeat(Unassigned, 10).ToArray();
        addresses[0] = "043f57feebffff"; // 192.168.1.20:65535
        var accepted = Codec.Decode<ConnectionRequestAccepted>(ExpectedHex(addresses));

        Assert.Equal(Client, accepted.ClientAddress);
        Assert.Equal(3, accepted.SystemIndex);
        Assert.Equal(10, accepted.InternalAddresses.Count);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 65535), accepted.InternalAddresses[0]);
        Assert.All(accepted.InternalAddresses.Skip(1), a => Assert.Equal(InternalAddressList.Unassigned, a));
        Assert.Equal(Codec.Time, accepted.RequestTime);
        Assert.Equal(Codec.OtherTime, accepted.ReplyTime);
    }

    [Fact]
    public void Accepted_round_trips()
    {
        var accepted = new ConnectionRequestAccepted(
            new IPEndPoint(IPAddress.Parse("10.1.2.3"), 1234),
            65534,
            InternalAddressList.Pad([new IPEndPoint(IPAddress.Parse("10.1.2.4"), 1234)]),
            1,
            2
        );

        Assert.True(Codec.TryDecode<ConnectionRequestAccepted>(Codec.Encode(accepted), out var decoded));
        Assert.Equal(accepted.ClientAddress, decoded.ClientAddress);
        Assert.Equal(accepted.SystemIndex, decoded.SystemIndex);
        Assert.Equal(accepted.InternalAddresses, decoded.InternalAddresses);
        Assert.Equal(accepted.RequestTime, decoded.RequestTime);
        Assert.Equal(accepted.ReplyTime, decoded.ReplyTime);
    }

    [Fact]
    public void Internal_address_count_is_a_parameter_for_other_protocol_profiles()
    {
        // MAXIMUM_NUMBER_OF_INTERNAL_IDS is #ifndef-guarded (RakNetDefines.h:75), so builds may change it.
        var accepted = new ConnectionRequestAccepted(Client, 0, InternalAddressList.CreateUnassigned(20), 1, 2);
        var bytes = Codec.Encode(accepted);
        var reader = new BitStreamReader(bytes);

        Assert.Equal(1 + 7 + 2 + 20 * 7 + 16, bytes.Length);
        Assert.True(ConnectionRequestAccepted.TryRead(ref reader, out var decoded, internalAddressCount: 20));
        Assert.Equal(20, decoded.InternalAddresses.Count);
    }

    [Fact]
    public void Message_with_fewer_addresses_than_expected_fails()
    {
        var bytes = Codec.Encode(new ConnectionRequestAccepted(Client, 0, InternalAddressList.CreateUnassigned(), 1, 2));
        var reader = new BitStreamReader(bytes);

        Assert.False(ConnectionRequestAccepted.TryRead(ref reader, out _, internalAddressCount: 11));
    }

    [Fact]
    public void Truncated_accepted_fails()
    {
        Codec.AssertAllTruncationsFail<ConnectionRequestAccepted>(Convert.FromHexString(ExpectedHex(Enumerable.Repeat(Unassigned, 10))));
    }

    [Fact]
    public void Accepted_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<ConnectionRequestAccepted>(
            Convert.FromHexString(ExpectedHex(Enumerable.Repeat(Unassigned, 10))),
            MessageId.ConnectionRequestAccepted
        );
    }

    [Fact]
    public void Accepted_with_an_ipv6_internal_address_fails()
    {
        var bytes = Convert.FromHexString(ExpectedHex(Enumerable.Repeat(Unassigned, 10)));
        bytes[10 + 7 * 4] = 6;

        Assert.False(Codec.TryDecode<ConnectionRequestAccepted>(bytes, out _));
    }
}
