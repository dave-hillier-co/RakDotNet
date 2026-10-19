using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class RakNetConstantsTests
{
    [Fact]
    public void The_library_version_is_RakNet_4_081()
    {
        // RakNetVersion.h:11-15.
        Assert.Equal("4.081", RakNetConstants.RakNetVersion);
        Assert.Equal(4.081, RakNetConstants.RakNetVersionNumber);
        Assert.Equal(4081, RakNetConstants.RakNetVersionNumberInt);
        Assert.Equal("5/28/2014", RakNetConstants.RakNetDate);
    }

    [Fact]
    public void The_unassigned_network_id_and_player_index_are_all_ones()
    {
        // RakNetTypes.h:457-460: UNASSIGNED_PLAYER_INDEX and UNASSIGNED_NETWORK_ID.
        Assert.Equal(ushort.MaxValue, RakNetConstants.UnassignedPlayerIndex);
        Assert.Equal(ulong.MaxValue, RakNetConstants.UnassignedNetworkId);
    }
}
