using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// Expected values are derived from CCRakNetSlidingWindow.cpp; times are microseconds.
public class SlidingWindowTests
{
    private const int Payload = 1492 - 28;

    [Fact]
    public void Acks_are_sent_immediately_while_the_round_trip_time_is_unknown()
    {
        var window = new SlidingWindow(Payload);
        Assert.True(window.OnGotPacket(new UInt24(0), nowUs: 5_000_000, out _));

        // CCRakNetSlidingWindow.cpp:93-97: no RTT sample yet, so send right away.
        Assert.True(window.ShouldSendAcks(5_000_000));
    }

    [Fact]
    public void Once_the_round_trip_time_is_known_acks_wait_for_the_syn_interval_after_the_oldest_unsent_ack()
    {
        var window = new SlidingWindow(Payload);
        window.OnAck(rttUs: 2_000, isContinuousSend: false, new UInt24(0));

        window.OnGotPacket(new UInt24(0), nowUs: 1_000_000, out _);
        // A later datagram does not move the oldest unsent ack (CCRakNetSlidingWindow.cpp:132-133).
        window.OnGotPacket(new UInt24(1), nowUs: 1_004_000, out _);

        Assert.False(window.ShouldSendAcks(1_000_000 + SlidingWindow.SynUs - 1));
        Assert.True(window.ShouldSendAcks(1_000_000 + SlidingWindow.SynUs));
    }

    [Fact]
    public void Sending_acks_restarts_the_syn_interval_at_the_next_datagram()
    {
        var window = new SlidingWindow(Payload);
        window.OnAck(rttUs: 2_000, isContinuousSend: false, new UInt24(0));
        window.OnGotPacket(new UInt24(0), nowUs: 1_000_000, out _);
        window.OnSendAck();

        window.OnGotPacket(new UInt24(1), nowUs: 2_000_000, out _);

        Assert.False(window.ShouldSendAcks(2_000_000 + SlidingWindow.SynUs - 1));
        Assert.True(window.ShouldSendAcks(2_000_000 + SlidingWindow.SynUs));
    }

    [Fact]
    public void Retransmission_timeout_is_two_seconds_until_a_round_trip_is_measured()
    {
        var window = new SlidingWindow(Payload);

        Assert.Equal(2_000_000, window.GetRtoForRetransmission());
    }

    [Fact]
    public void Retransmission_timeout_is_twice_the_estimate_plus_four_deviations_plus_30_ms()
    {
        var window = new SlidingWindow(Payload);
        window.OnAck(rttUs: 10_000, isContinuousSend: false, new UInt24(0));

        // First sample: estimate 10000, deviation 10000 (CCRakNetSlidingWindow.cpp:210-214), so 2*10000+4*10000+30000.
        Assert.Equal(90_000, window.GetRtoForRetransmission());

        window.OnAck(rttUs: 30_000, isContinuousSend: false, new UInt24(1));

        // estimate += 0.05*(30000-10000) = 11000; deviation += 0.05*(20000-10000) = 10500.
        Assert.Equal(2 * 11_000 + 4 * 10_500 + 30_000, window.GetRtoForRetransmission());
    }

    [Fact]
    public void Retransmission_timeout_is_capped_at_two_seconds()
    {
        var window = new SlidingWindow(Payload);
        window.OnAck(rttUs: 5_000_000, isContinuousSend: false, new UInt24(0));

        Assert.Equal(2_000_000, window.GetRtoForRetransmission());
    }

    [Fact]
    public void The_window_starts_at_one_datagram_payload()
    {
        var window = new SlidingWindow(Payload);

        Assert.Equal(Payload, window.GetTransmissionBandwidth(unacknowledgedBytes: 0, isContinuousSend: false));
        Assert.Equal(Payload - 100, window.GetTransmissionBandwidth(unacknowledgedBytes: 100, isContinuousSend: false));
        Assert.Equal(0, window.GetTransmissionBandwidth(unacknowledgedBytes: Payload + 1, isContinuousSend: false));
    }

    [Fact]
    public void Acks_during_a_continuous_send_grow_the_window_by_a_datagram_in_slow_start()
    {
        var window = new SlidingWindow(Payload);

        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(0));

        Assert.True(window.IsInSlowStart);
        Assert.Equal(2 * Payload, window.GetTransmissionBandwidth(0, isContinuousSend: true));
    }

    [Fact]
    public void Acks_outside_a_continuous_send_leave_the_window_alone()
    {
        var window = new SlidingWindow(Payload);

        window.OnAck(rttUs: 1_000, isContinuousSend: false, new UInt24(0));

        Assert.Equal(Payload, window.GetTransmissionBandwidth(0, isContinuousSend: false));
    }

    [Fact]
    public void Datagram_numbers_count_up_from_zero()
    {
        var window = new SlidingWindow(Payload);

        Assert.Equal(new UInt24(0), window.TakeNextDatagramNumber());
        Assert.Equal(new UInt24(1), window.TakeNextDatagramNumber());
        Assert.Equal(new UInt24(2), window.NextDatagramNumber);
    }

    [Fact]
    public void A_received_datagram_ahead_of_the_expected_number_reports_the_skipped_count()
    {
        var window = new SlidingWindow(Payload);
        window.OnGotPacket(new UInt24(0), 0, out var first);

        Assert.True(window.OnGotPacket(new UInt24(4), 0, out var skipped));

        Assert.Equal(0u, first);
        Assert.Equal(3u, skipped);
    }

    [Fact]
    public void A_late_datagram_skips_nothing_and_does_not_move_the_expected_number()
    {
        var window = new SlidingWindow(Payload);
        window.OnGotPacket(new UInt24(5), 0, out _);

        window.OnGotPacket(new UInt24(2), 0, out var late);
        window.OnGotPacket(new UInt24(6), 0, out var next);

        Assert.Equal(0u, late);
        Assert.Equal(0u, next);
    }

    [Fact]
    public void Skipped_counts_are_capped_at_1000_and_datagrams_more_than_50000_ahead_are_rejected()
    {
        var capped = new SlidingWindow(Payload);
        Assert.True(capped.OnGotPacket(new UInt24(2000), 0, out var skipped));
        Assert.Equal(1000u, skipped);

        var rejected = new SlidingWindow(Payload);
        Assert.False(rejected.OnGotPacket(new UInt24(50_001), 0, out _));
    }

    [Fact]
    public void A_resend_during_a_continuous_send_drops_the_window_to_one_datagram_and_halves_the_threshold()
    {
        var window = WindowGrownToThreeDatagrams();

        // CCRakNetSlidingWindow.cpp:168-179.
        window.OnResend();

        Assert.Equal(Payload, window.GetTransmissionBandwidth(0, isContinuousSend: true));
        Assert.True(window.IsInSlowStart);
        // Slow start now stops at the threshold of 1.5 datagrams: cwnd = ssThresh + mtu * mtu / cwnd (line 243).
        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(0));
        Assert.Equal(2 * Payload, window.GetTransmissionBandwidth(0, isContinuousSend: true));
        Assert.False(window.IsInSlowStart);
    }

    [Fact]
    public void The_window_backs_off_once_per_congestion_control_block()
    {
        var window = WindowGrownToThreeDatagrams();
        window.OnResend();
        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(0));
        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(0));

        // The block began at datagram 0, so this resend belongs to it and changes nothing (line 168).
        window.OnResend();

        Assert.Equal(2 * Payload, window.GetTransmissionBandwidth(0, isContinuousSend: true));
    }

    [Fact]
    public void Congestion_avoidance_grows_the_window_by_a_fraction_of_a_datagram_once_per_block()
    {
        var window = WindowGrownToThreeDatagrams();
        window.OnResend();
        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(0)); // cwnd 2 datagrams, avoidance from now on
        window.TakeNextDatagramNumber();
        window.TakeNextDatagramNumber();

        // Datagram 1 is past the block that began at 0 (lines 230-237): grow by mtu * mtu / cwnd, half a datagram.
        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(1));
        Assert.Equal((int)(2.5 * Payload), window.GetTransmissionBandwidth(0, isContinuousSend: true));

        // The new block starts at datagram 2, so datagram 1 again is not new and does not grow the window.
        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(1));
        Assert.Equal((int)(2.5 * Payload), window.GetTransmissionBandwidth(0, isContinuousSend: true));
    }

    [Fact]
    public void A_resend_outside_a_continuous_send_leaves_the_window_alone()
    {
        var window = WindowGrownToThreeDatagrams();
        window.GetTransmissionBandwidth(0, isContinuousSend: false);

        window.OnResend();

        Assert.Equal(3 * Payload, window.GetTransmissionBandwidth(0, isContinuousSend: false));
    }

    [Fact]
    public void A_nak_during_a_continuous_send_ends_slow_start_without_shrinking_the_window()
    {
        var window = WindowGrownToThreeDatagrams();

        // CCRakNetSlidingWindow.cpp:191-195: ssThresh = cwnd / 2.
        window.OnNak();

        Assert.False(window.IsInSlowStart);
        Assert.Equal(3 * Payload, window.GetTransmissionBandwidth(0, isContinuousSend: true));
    }

    [Fact]
    public void A_nak_outside_a_continuous_send_changes_nothing()
    {
        var window = WindowGrownToThreeDatagrams();
        window.GetTransmissionBandwidth(0, isContinuousSend: false);

        window.OnNak();

        Assert.True(window.IsInSlowStart);
    }

    private static SlidingWindow WindowGrownToThreeDatagrams()
    {
        var window = new SlidingWindow(Payload);
        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(0));
        window.OnAck(rttUs: 1_000, isContinuousSend: true, new UInt24(0));
        Assert.Equal(3 * Payload, window.GetTransmissionBandwidth(0, isContinuousSend: true));
        return window;
    }
}
