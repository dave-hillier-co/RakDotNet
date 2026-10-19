using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

public class AddressOrGuidTests
{
    private static readonly SystemAddress Address = new(IPAddress.Loopback, 19132);

    [Fact]
    public void Default_is_undefined_with_neither_a_guid_nor_an_address()
    {
        var target = default(AddressOrGuid);

        Assert.True(target.IsUndefined);
        Assert.Equal(RakNetGuid.Unassigned, target.RakNetGuid);
        Assert.Equal(SystemAddress.Unassigned, target.Address);
    }

    [Fact]
    public void A_guid_target_holds_only_the_guid()
    {
        AddressOrGuid target = new RakNetGuid(42);

        Assert.False(target.IsUndefined);
        Assert.Equal(new RakNetGuid(42), target.RakNetGuid);
        Assert.Equal(SystemAddress.Unassigned, target.Address);
    }

    [Fact]
    public void An_address_target_holds_only_the_address()
    {
        AddressOrGuid target = Address;

        Assert.False(target.IsUndefined);
        Assert.Equal(Address, target.Address);
        Assert.Equal(RakNetGuid.Unassigned, target.RakNetGuid);
    }

    [Fact]
    public void An_IPEndPoint_converts_to_an_address_target()
    {
        AddressOrGuid target = new IPEndPoint(IPAddress.Loopback, 19132);

        Assert.Equal(Address, target.Address);
    }

    [Fact]
    public void Unassigned_values_make_an_undefined_target()
    {
        // RakNetTypes.h:378 - IsUndefined when both parts are unassigned
        Assert.True(((AddressOrGuid)RakNetGuid.Unassigned).IsUndefined);
        Assert.True(((AddressOrGuid)SystemAddress.Unassigned).IsUndefined);
        Assert.Equal(default, (AddressOrGuid)RakNetGuid.Unassigned);
    }

    [Fact]
    public void Targets_are_equal_when_they_hold_the_same_value()
    {
        Assert.Equal((AddressOrGuid)new RakNetGuid(7), (AddressOrGuid)new RakNetGuid(7));
        Assert.Equal((AddressOrGuid)Address, (AddressOrGuid)Address);
        Assert.NotEqual((AddressOrGuid)new RakNetGuid(7), (AddressOrGuid)new RakNetGuid(8));
    }
}
