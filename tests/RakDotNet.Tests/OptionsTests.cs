using System.Net;
using RakDotNet.Security;

namespace RakDotNet.Tests;

public class OptionsTests
{
    [Fact]
    public void Peer_options_default_to_a_RakNet_release_build()
    {
        var options = new RakPeerOptions { MaxConnections = 8 };

        Assert.Equal(0, options.MaxIncomingConnections); // RakPeer.cpp:208
        Assert.Equal(TimeSpan.FromSeconds(10), options.DefaultTimeout); // RakPeer.cpp:249
        Assert.Equal(TimeSpan.FromMilliseconds(1000), options.UnreliableTimeout); // RakPeer.cpp:238
        Assert.False(options.OccasionalPing); // RakPeer.cpp:228
        Assert.Equal(0, options.SplitMessageProgressInterval); // RakPeer.cpp:236
        Assert.False(options.LimitIpConnectionFrequency); // RakPeer.cpp:312
        Assert.False(options.AllowConnectionResponseIpMigration); // RakPeer.cpp:233
        Assert.Equal(0, options.PerConnectionOutgoingBandwidthLimit); // RakPeer.cpp:239
        Assert.Null(options.Security);
        Assert.Equal(PeerThreading.Background, options.Threading);
        Assert.Same(TimeProvider.System, options.TimeProvider);
        Assert.Equal([SocketBinding.AnyIPv4], options.Bindings);
    }

    [Fact]
    public void Connect_options_default_to_RakNet_connect_arguments()
    {
        var options = new ConnectOptions();

        // RakPeerInterface.h:139 - sendConnectionAttemptCount 12, timeBetweenSendConnectionAttemptsMS 500
        Assert.Equal(12, options.AttemptCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.AttemptInterval);
        Assert.Equal(TimeSpan.Zero, options.Timeout);
        Assert.Same(ConnectSecurity.Insecure, options.Security);
    }

    [Fact]
    public void AnyIPv4_binds_every_IPv4_interface_on_an_ephemeral_port()
    {
        Assert.Equal(new IPEndPoint(IPAddress.Any, 0), SocketBinding.AnyIPv4.LocalEndPoint);
    }

    [Fact]
    public void Port_binds_every_IPv4_interface_on_that_port()
    {
        // SocketDescriptor(port, 0)
        Assert.Equal(new IPEndPoint(IPAddress.Any, 19132), SocketBinding.Port(19132).LocalEndPoint);
    }
}
