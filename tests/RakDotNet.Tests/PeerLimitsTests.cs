using RakDotNet.Protocol.Session;

namespace RakDotNet.Tests;

public class PeerLimitsTests
{
    [Fact]
    public void The_default_limits_are_the_documented_values()
    {
        var limits = PeerLimits.Default;

        Assert.Equal(262_144, limits.MaxSplitCount);
        Assert.Equal(128 * 1024 * 1024, limits.MaxSplitMessageBytes);
        Assert.Equal(1_024, limits.MaxConcurrentSplitsPerConnection);
        Assert.Equal(TimeSpan.FromSeconds(60), limits.SplitReassemblyTimeout);
        Assert.Equal(65_536, limits.MaxOrderingBufferMessagesPerChannel);
        Assert.Equal(256 * 1024 * 1024, limits.MaxReceiveBufferBytesPerConnection);
        Assert.Equal(65_536, limits.EventQueueCapacity);
    }

    [Fact]
    public void The_default_limits_are_the_sessions_defaults()
    {
        Assert.Equal(SessionLimits.Default, PeerLimits.Default.ToSessionLimits());
    }

    [Fact]
    public void Limits_set_in_an_initializer_keep_the_defaults_for_the_rest()
    {
        var limits = new PeerLimits { MaxSplitCount = 16, SplitReassemblyTimeout = TimeSpan.FromMilliseconds(1_500) };

        var session = limits.ToSessionLimits();

        Assert.Equal(16, session.MaxSplitCount);
        Assert.Equal(1_500_000, session.SplitReassemblyTimeoutUs);
        Assert.Equal(SessionLimits.Default.MaxSplitMessageBytes, session.MaxSplitMessageBytes);
        Assert.Equal(SessionLimits.Default.MaxReceiveBufferBytes, session.MaxReceiveBufferBytes);
    }

    [Theory]
    [InlineData(nameof(PeerLimits.MaxSplitCount))]
    [InlineData(nameof(PeerLimits.MaxSplitMessageBytes))]
    [InlineData(nameof(PeerLimits.MaxConcurrentSplitsPerConnection))]
    [InlineData(nameof(PeerLimits.MaxOrderingBufferMessagesPerChannel))]
    [InlineData(nameof(PeerLimits.MaxReceiveBufferBytesPerConnection))]
    public void A_limit_below_one_is_rejected(string name)
    {
        var limits = name switch
        {
            nameof(PeerLimits.MaxSplitCount) => new PeerLimits { MaxSplitCount = 0 },
            nameof(PeerLimits.MaxSplitMessageBytes) => new PeerLimits { MaxSplitMessageBytes = 0 },
            nameof(PeerLimits.MaxConcurrentSplitsPerConnection) => new PeerLimits { MaxConcurrentSplitsPerConnection = 0 },
            nameof(PeerLimits.MaxOrderingBufferMessagesPerChannel) => new PeerLimits { MaxOrderingBufferMessagesPerChannel = 0 },
            _ => new PeerLimits { MaxReceiveBufferBytesPerConnection = 0 },
        };

        Assert.Throws<ArgumentOutOfRangeException>(limits.ToSessionLimits);
    }

    [Fact]
    public void A_reassembly_timeout_that_is_not_positive_is_rejected()
    {
        var limits = new PeerLimits { SplitReassemblyTimeout = TimeSpan.Zero };

        Assert.Throws<ArgumentOutOfRangeException>(limits.ToSessionLimits);
    }
}
