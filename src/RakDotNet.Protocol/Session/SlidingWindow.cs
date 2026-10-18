using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// CCRakNetSlidingWindow (CCRakNetSlidingWindow.cpp), the congestion manager of the default C++ build
/// (USE_SLIDING_WINDOW_CONGESTION_CONTROL, RakNetDefines.h:121-122). It numbers outgoing datagrams, decides when
/// acknowledgements go out, estimates the round trip and sizes the send window.
/// </summary>
/// <remarks>
/// Times are microseconds: CCTimeType is RakNet::TimeUS because CC_TIME_TYPE_BYTES is 8
/// (CCRakNetSlidingWindow.h:65-68). Functions that are empty in the C++ (Update, OnSendBytes, OnGotPacketPair,
/// OnDuplicateAck, OnSendNACK) are omitted.
/// </remarks>
internal sealed class SlidingWindow
{
    /// <summary>SYN, CCRakNetSlidingWindow.cpp:17-21: the most an acknowledgement is delayed once the RTT is known.</summary>
    public const long SynUs = 10_000;

    // UNSET_TIME_US, CCRakNetSlidingWindow.cpp:15.
    private const double Unset = -1;

    // GetRTOForRetransmission, CCRakNetSlidingWindow.cpp:291-317.
    private const long MaxRetransmissionTimeoutUs = 2_000_000;
    private const long AdditionalVarianceUs = 30_000;

    // MAXIMUM_MTU_INCLUDING_UDP_HEADER, which despite its name is the MTU minus the UDP header (ReliabilityLayer.cpp:361).
    private readonly int _maxDatagramPayload;

    private double _cwnd;
    private double _ssThresh;
    private double _lastRtt = Unset;
    private double _estimatedRtt = Unset;
    private double _deviationRtt = Unset;
    private long _oldestUnsentAckUs;
    private UInt24 _nextDatagramNumber;
    private UInt24 _nextCongestionControlBlock;
    private UInt24 _expectedNextSequenceNumber;
    private bool _backoffThisBlock;
    private bool _isContinuousSend;

    /// <summary>Init, CCRakNetSlidingWindow.cpp:43-58.</summary>
    /// <param name="maxDatagramPayload">The MTU minus the 28-byte IP and UDP header.</param>
    public SlidingWindow(int maxDatagramPayload)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDatagramPayload);
        _maxDatagramPayload = maxDatagramPayload;
        _cwnd = maxDatagramPayload;
    }

    /// <summary>GetMTU (CCRakNetSlidingWindow.cpp:322-325): the largest datagram, in bytes.</summary>
    public int MaxDatagramPayload => _maxDatagramPayload;

    /// <summary>GetNextDatagramSequenceNumber.</summary>
    public UInt24 NextDatagramNumber => _nextDatagramNumber;

    /// <summary>IsInSlowStart (CCRakNetSlidingWindow.cpp:378-381). Sent as needsBAndAs on every data datagram.</summary>
    public bool IsInSlowStart => _cwnd <= _ssThresh || _ssThresh == 0;

    /// <summary>The most recent round-trip sample in microseconds, or 0 before the first (GetRTT).</summary>
    public double LastRttUs => _lastRtt == Unset ? 0 : _lastRtt;

    /// <summary>GetAndIncrementNextDatagramSequenceNumber.</summary>
    public UInt24 TakeNextDatagramNumber()
    {
        var number = _nextDatagramNumber;
        _nextDatagramNumber++;
        return number;
    }

    /// <summary>GetRetransmissionBandwidth (CCRakNetSlidingWindow.cpp:66-73).</summary>
    public static int GetRetransmissionBandwidth(int unacknowledgedBytes) => unacknowledgedBytes;

    /// <summary>GetTransmissionBandwidth (CCRakNetSlidingWindow.cpp:75-86): the bytes of new data that may be sent.</summary>
    public int GetTransmissionBandwidth(int unacknowledgedBytes, bool isContinuousSend)
    {
        _isContinuousSend = isContinuousSend;
        return unacknowledgedBytes <= _cwnd ? (int)(_cwnd - unacknowledgedBytes) : 0;
    }

    /// <summary>
    /// ShouldSendACKs (CCRakNetSlidingWindow.cpp:88-101): immediately while no round trip has been measured,
    /// otherwise once the oldest unsent acknowledgement is <see cref="SynUs"/> old.
    /// </summary>
    public bool ShouldSendAcks(long nowUs)
    {
        // GetSenderRTOForACK is unset exactly when lastRtt is (CCRakNetSlidingWindow.cpp:369-374).
        if (_lastRtt == Unset)
            return true;
        return nowUs >= _oldestUnsentAckUs + SynUs;
    }

    /// <summary>
    /// OnGotPacket (CCRakNetSlidingWindow.cpp:127-160). Returns false when the datagram is so far ahead it is treated
    /// as garbage; otherwise <paramref name="skippedMessageCount"/> is how many datagram numbers were jumped over.
    /// </summary>
    public bool OnGotPacket(UInt24 datagramNumber, long nowUs, out uint skippedMessageCount)
    {
        if (_oldestUnsentAckUs == 0)
            _oldestUnsentAckUs = nowUs;

        if (datagramNumber == _expectedNextSequenceNumber)
        {
            skippedMessageCount = 0;
            _expectedNextSequenceNumber = datagramNumber + 1u;
        }
        else if (UInt24.GreaterThan(datagramNumber, _expectedNextSequenceNumber))
        {
            skippedMessageCount = (datagramNumber - _expectedNextSequenceNumber).Value;
            // Sanity check, CCRakNetSlidingWindow.cpp:145-151.
            if (skippedMessageCount > 1000)
            {
                if (skippedMessageCount > 50_000)
                    return false;
                skippedMessageCount = 1000;
            }
            _expectedNextSequenceNumber = datagramNumber + 1u;
        }
        else
        {
            skippedMessageCount = 0;
        }
        return true;
    }

    /// <summary>OnResend (CCRakNetSlidingWindow.cpp:162-183): back off once per block during a continuous send.</summary>
    public void OnResend()
    {
        if (_isContinuousSend && !_backoffThisBlock && _cwnd > _maxDatagramPayload * 2)
        {
            _ssThresh = _cwnd / 2;
            if (_ssThresh < _maxDatagramPayload)
                _ssThresh = _maxDatagramPayload;
            _cwnd = _maxDatagramPayload;
            _nextCongestionControlBlock = _nextDatagramNumber;
            _backoffThisBlock = true;
        }
    }

    /// <summary>OnNAK (CCRakNetSlidingWindow.cpp:185-198).</summary>
    public void OnNak()
    {
        if (_isContinuousSend && !_backoffThisBlock)
            _ssThresh = _cwnd / 2;
    }

    /// <summary>OnAck (CCRakNetSlidingWindow.cpp:200-250): take an RTT sample and grow the window.</summary>
    public void OnAck(long rttUs, bool isContinuousSend, UInt24 datagramNumber)
    {
        _lastRtt = rttUs;
        if (_estimatedRtt == Unset)
        {
            _estimatedRtt = rttUs;
            _deviationRtt = rttUs;
        }
        else
        {
            const double d = .05;
            var difference = rttUs - _estimatedRtt;
            _estimatedRtt += d * difference;
            _deviationRtt += d * (Math.Abs(difference) - _deviationRtt);
        }

        _isContinuousSend = isContinuousSend;
        if (!isContinuousSend)
            return;

        var isNewCongestionControlPeriod = UInt24.GreaterThan(datagramNumber, _nextCongestionControlBlock);
        if (isNewCongestionControlPeriod)
        {
            _backoffThisBlock = false;
            _nextCongestionControlBlock = _nextDatagramNumber;
        }

        if (IsInSlowStart)
        {
            _cwnd += _maxDatagramPayload;
            if (_cwnd > _ssThresh && _ssThresh != 0)
                _cwnd = _ssThresh + (double)_maxDatagramPayload * _maxDatagramPayload / _cwnd;
        }
        else if (isNewCongestionControlPeriod)
        {
            _cwnd += (double)_maxDatagramPayload * _maxDatagramPayload / _cwnd;
        }
    }

    /// <summary>OnSendAck (CCRakNetSlidingWindow.cpp:268-274).</summary>
    public void OnSendAck() => _oldestUnsentAckUs = 0;

    /// <summary>GetRTOForRetransmission (CCRakNetSlidingWindow.cpp:291-317), in microseconds.</summary>
    public long GetRtoForRetransmission()
    {
        if (_estimatedRtt == Unset)
            return MaxRetransmissionTimeoutUs;
        const double u = 2.0;
        const double q = 4.0;
        var threshold = (long)(u * _estimatedRtt + q * _deviationRtt) + AdditionalVarianceUs;
        return threshold > MaxRetransmissionTimeoutUs ? MaxRetransmissionTimeoutUs : threshold;
    }
}
