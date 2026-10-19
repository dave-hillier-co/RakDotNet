using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Transport;

namespace RakDotNet;

// The I/O cycle and the dispatch step. The cycle owns the peer core, the connection maps and the receipt table; it runs
// on the background loop thread, or on the caller of Update in Manual mode, one at a time under _cycleGate. The dispatch
// step (TryRead, Update, shutdown's plugin step) runs plugins and hands out borrowed events, one caller at a time.
public sealed partial class RakPeer
{
    // Larger than any datagram, so an oversized one is read whole and then ignored rather than truncated.
    private const int ReceiveBufferSize = 64 * 1024;

    // Bounds the per-remote caches against a flood of spoofed senders.
    private const int AddressCacheCapacity = 1024;

    // Datagrams read from one socket in one cycle before the cycle moves on and schedules another.
    private const int MaxDatagramsPerCycle = 4096;

    private readonly PeerCore _core;
    private readonly Lock _cycleGate = new();
    private readonly byte[] _receiveBuffer = GC.AllocateUninitializedArray<byte>(ReceiveBufferSize);
    private readonly Dictionary<SystemAddress, IPEndPoint> _endPoints = [];
    private readonly Dictionary<SystemAddress, int> _remoteSockets = [];
    private readonly Dictionary<ulong, RakConnection> _byGuid = [];
    private readonly Dictionary<SystemAddress, RakConnection> _byAddress = [];
    private readonly Dictionary<uint, ReceiptRecord> _receipts = [];
    private readonly Dictionary<ulong, bool> _closedLocally = [];
    private readonly List<uint> _scratchReceipts = [];
    private readonly List<PendingConnect> _scratchAttempts = [];
    private readonly NetworkSimulator _simulator = new();
    private Thread? _loop;
    private ManualResetEventSlim? _wake;
    private int _wakePending;
    private volatile bool _loopStopping;
    private ITimer? _updateTimer;
    private volatile bool _receivePaused;
    private volatile int _activeSystemCount;

    private bool InDispatchOnThisThread => Volatile.Read(ref _dispatchThread) == Environment.CurrentManagedThreadId;

    /// <summary>The dispatch step of <see cref="PeerEventReader.TryRead"/>, RakPeer::Receive (RakPeer.cpp:1528-1600).</summary>
    internal bool TryReadNext(out PeerEvent e)
    {
        if (!TryEnterDispatch())
        {
            // Shutdown closes the stream before it takes the dispatch step, so a read that meets it there is a later
            // read, which returns false (docs/api-design.md section 7).
            if (_streamClosed && !InDispatchOnThisThread)
            {
                e = default;
                return false;
            }
            ThrowConcurrentDispatch();
        }
        try
        {
            ReleaseBorrowed();
            if (_streamClosed)
            {
                e = default;
                return false;
            }
            ApplyDispatchOps();

            foreach (var plugin in _plugins.Ordinary)
                plugin.OnUpdate();
            foreach (var plugin in _plugins.ReliabilityLayer)
                plugin.OnUpdate();

            while (_queue.TryDequeue(out var segment))
            {
                ResumeReceivingIfDrained();
                var generation = Volatile.Read(ref segment.Generation);
                var view = new PeerEvent(segment, generation);
                // ReceiptAbandoned is not a RakNet packet, so RakNet's plugins never see it (decision 11.4).
                if (segment.Kind != PeerEventKind.ReceiptAbandoned)
                {
                    CallConnectionCallbacks(_plugins.Ordinary, view, segment);
                    CallConnectionCallbacks(_plugins.ReliabilityLayer, view, segment);
                    if (IsConsumed(_plugins.Ordinary, view) || IsConsumed(_plugins.ReliabilityLayer, view))
                    {
                        if (!segment.IsOwned)
                            segment.TryRelease(generation);
                        continue;
                    }
                }
                _borrowed = segment;
                _borrowedGeneration = generation;
                e = view;
                return true;
            }

            e = default;
            return false;
        }
        finally
        {
            ExitDispatch();
        }
    }

    private void StartPlugins()
    {
        foreach (var plugin in _options.Plugins)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            _plugins.AttachAtStart(plugin);
        }
        foreach (var plugin in _plugins.Ordinary)
            plugin.OnStartup();
        foreach (var plugin in _plugins.ReliabilityLayer)
            plugin.OnStartup();
    }

    private void StartLoop()
    {
        if (_options.Threading != PeerThreading.Background)
            return;
        _wake = new ManualResetEventSlim(false);
        foreach (var transport in _transports)
            transport.SetReadySignal(Wake);
        _updateTimer = _time.CreateTimer(static state => ((RakPeer)state!).Wake(), this, UpdateInterval, UpdateInterval);
        _loop = new Thread(LoopMain) { IsBackground = true, Name = "RakDotNet I/O" };
        try
        {
            _loop.Start();
        }
        catch (OutOfMemoryException)
        {
            _updateTimer.Dispose();
            throw new RakStartupException(StartupError.FailedToCreateNetworkThread, 0, SocketError.Success);
        }
    }

    private void Wake()
    {
        var wake = _wake;
        if (wake is null || Interlocked.Exchange(ref _wakePending, 1) != 0)
            return;
        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void LoopMain()
    {
        var wake = _wake!;
        while (true)
        {
            wake.Wait();
            if (_loopStopping)
                return;
            wake.Reset();
            Interlocked.Exchange(ref _wakePending, 0);
            try
            {
                RunCycle();
            }
            catch (RakTransportException exception)
            {
                Stop(exception);
                return;
            }
#pragma warning disable CA1031 // A failure of the cycle ends the peer and faults Events.Completion with the cause.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                Stop(new RakTransportException("The I/O cycle failed.", exception));
                return;
            }
        }
    }

    private void RunCycleIfManual()
    {
        if (_options.Threading == PeerThreading.Manual)
            RunCycle();
    }

    /// <summary>
    /// One RunUpdateCycle (RakPeer.cpp:5567-6253): apply every waiting command, read the sockets, update the core,
    /// send what it framed, then turn what it produced into stream entries and published state.
    /// </summary>
    private void RunCycle()
    {
        lock (_cycleGate)
        {
            if (Volatile.Read(ref _state) == Stopped)
                return;
            var nowUs = NowUs;
            DrainCommands(nowUs);
            if (_receivePaused && _queue.Count < _eventCapacity / 2)
                _receivePaused = false;
            ReceiveDatagrams(nowUs);
            _core.Update(nowUs);
            FlushDatagrams(nowUs);
            ProcessCoreEvents(nowUs);
            PollConnections(nowUs);
            _activeSystemCount = _core.ActiveSystemCount;
            foreach (var observer in _lowLevel.Observers.Current)
                observer.OnUpdateCycle();
            foreach (var observer in _plugins.Observers)
                observer.OnUpdateCycle();
        }
    }

    private void DrainCommands(long nowUs)
    {
        while (_commands.TryDequeue(out var command))
            Apply(command, nowUs);
    }

    private void Apply(in PeerCommand command, long nowUs)
    {
        switch (command.Kind)
        {
            case CommandKind.Connect:
                ApplyConnect((PendingConnect)command.State!, nowUs);
                break;
            case CommandKind.CancelConnect:
                ApplyCancelConnect((PendingConnect)command.State!);
                break;
            case CommandKind.AbortConnect:
                ApplyAbortConnect((PendingConnect)command.State!, nowUs);
                break;
            case CommandKind.Close:
                ApplyClose(command, nowUs);
                break;
            case CommandKind.Send:
                ApplySend(command, nowUs);
                break;
            case CommandKind.Broadcast:
                ApplyBroadcast(command, nowUs);
                break;
            case CommandKind.PingUnconnected:
                var remote = command.Target.Address;
                RememberSocket(remote, (int)command.Value);
                _core.Ping(ToEndPoint(remote), command.Flag, nowUs);
                break;
            case CommandKind.SetOfflinePingResponse:
                _core.SetOfflinePingResponse((byte[])command.State!);
                break;
            case CommandKind.SetMaxIncoming:
                _core.MaxIncomingConnections = (int)command.Value;
                break;
            case CommandKind.NotifyShutdown:
                foreach (var guid in _byGuid.Keys)
                    _closedLocally[guid] = false;
                _core.NotifyAllForShutdown(command.Channel, command.Priority, nowUs);
                break;
            case CommandKind.SetConnectionTimeout:
                if (_core.TryGetRemoteSystem(command.Target.RakNetGuid.Value, out var system))
                    system.Session.TimeoutMs = command.Value;
                break;
            case CommandKind.SendRaw:
                SendRawNow(command);
                break;
            case CommandKind.SetInternalAddress:
                var address = command.Target.Address;
                _coreInternalAddresses[(int)command.Value] = address.IsAssigned ? ToEndPoint(address) : Protocol.Wire.Messages.InternalAddressList.Unassigned;
                break;
            case CommandKind.SetNetworkSimulator:
                _simulator.Configure((NetworkSimulatorOptions?)command.State);
                break;
            case CommandKind.SetIncomingPassword:
                _core.IncomingPassword = (byte[])command.State!;
                break;
            case CommandKind.SetDefaultTimeout:
                _core.SetTimeoutTime(command.Value, remote: null);
                break;
            case CommandKind.SetUnreliableTimeout:
                _core.UnreliableTimeoutMs = command.Value;
                break;
            case CommandKind.SetOccasionalPing:
                _core.OccasionalPing = command.Flag;
                break;
            case CommandKind.SetSplitMessageProgressInterval:
                _core.SplitMessageProgressInterval = (int)command.Value;
                break;
            case CommandKind.SetLimitIpConnectionFrequency:
                _core.LimitIpConnectionFrequency = command.Flag;
                break;
            case CommandKind.SetAllowConnectionResponseIpMigration:
                _core.AllowConnectionResponseIpMigration = command.Flag;
                break;
            case CommandKind.SetOutgoingBandwidthLimit:
                _core.OutgoingBandwidthLimit = (uint)command.Value;
                break;
            case CommandKind.PingConnected:
                if (_byGuid.TryGetValue(command.Target.RakNetGuid.Value, out var pinged) && pinged.System is { IsActive: true } pingedSystem)
                    _core.PingConnected(pingedSystem.Address, nowUs);
                break;
            case CommandKind.ChangeSystemAddress:
                ApplyChangeSystemAddress(command.Target.RakNetGuid, ((SystemAddressBox)command.State!).Value);
                break;
        }
    }

    // BCS_CHANGE_SYSTEM_ADDRESS (RakPeer.cpp:5646-5654).
    private void ApplyChangeSystemAddress(RakNetGuid rakNetGuid, SystemAddress address)
    {
        _core.ChangeSystemAddress(rakNetGuid.Value, ToEndPoint(address));
        if (!_byGuid.TryGetValue(rakNetGuid.Value, out var connection))
            return;
        if (_byAddress.TryGetValue(connection.Address, out var byAddress) && ReferenceEquals(byAddress, connection))
            _byAddress.Remove(connection.Address);
        connection.MoveTo(address);
        _byAddress[address] = connection;
        PublishTable();
    }

    private void ApplyConnect(PendingConnect attempt, long nowUs)
    {
        lock (_attemptsGate)
        {
            if (!_attempts.TryGetValue(attempt.Address, out var current) || !ReferenceEquals(current, attempt))
                return;
        }
        RememberSocket(attempt.Address, attempt.Options.SocketIndex);
        var result = _core.Connect(attempt.EndPoint, attempt.Password, attempt.Options.AttemptCount,
                                   (int)attempt.Options.AttemptInterval.TotalMilliseconds, (long)attempt.Options.Timeout.TotalMilliseconds, nowUs);
        if (result == ConnectionAttemptResult.ConnectionAttemptStarted)
        {
            attempt.Applied = true;
            return;
        }
        lock (_attemptsGate)
            _attempts.Remove(attempt.Address);
        var attemptResult = result switch
        {
            ConnectionAttemptResult.AlreadyConnectedToEndpoint => ConnectAttemptResult.AlreadyConnectedToEndpoint,
            ConnectionAttemptResult.ConnectionAttemptAlreadyInProgress => ConnectAttemptResult.ConnectionAttemptAlreadyInProgress,
            ConnectionAttemptResult.SecurityInitializationFailed => ConnectAttemptResult.SecurityInitializationFailed,
            ConnectionAttemptResult.CannotResolveDomainName => ConnectAttemptResult.CannotResolveDomainName,
            _ => throw new InvalidOperationException($"The core refused a validated attempt: {result}."),
        };
        attempt.Complete(new ConnectResult(attemptResult, failure: null, connection: null, remoteProtocolVersion: 0));
    }

    // RakPeer::CancelConnectionAttempt (RakPeer.cpp:1686-1710): only a queued attempt is removed. The caller already took
    // it out of the registry.
    private void ApplyCancelConnect(PendingConnect attempt)
    {
        if (attempt.Applied && _core.GetConnectionState(attempt.EndPoint) != Protocol.Peer.ConnectionState.IsPending)
        {
            // It has a table entry already, so the cancel comes too late and the attempt carries on.
            lock (_attemptsGate)
                _attempts.TryAdd(attempt.Address, attempt);
            return;
        }
        if (attempt.Applied)
            _core.CancelConnectionAttempt(attempt.EndPoint);
        attempt.Cancel(CancellationToken.None);
    }

    private void ApplyAbortConnect(PendingConnect attempt, long nowUs)
    {
        lock (_attemptsGate)
        {
            if (!_attempts.TryGetValue(attempt.Address, out var current) || !ReferenceEquals(current, attempt))
                return;
            _attempts.Remove(attempt.Address);
        }
        switch (_core.GetConnectionState(attempt.EndPoint))
        {
            case Protocol.Peer.ConnectionState.IsPending:
                _core.CancelConnectionAttempt(attempt.EndPoint);
                break;
            case Protocol.Peer.ConnectionState.IsConnecting:
                // A silent close of a system not yet connected produces no event (RakPeer.cpp:1667-1678).
                _core.CloseConnection(attempt.EndPoint, sendDisconnectionNotification: false, 0, Priority.Low, nowUs);
                break;
        }
        attempt.Cancel(attempt.Token);
    }

    private void ApplyClose(in PeerCommand command, long nowUs)
    {
        var target = command.Target;
        var connection = Find(target);
        if (connection is not null)
            _closedLocally[connection.RakNetGuid.Value] = !command.Flag;
        if (target.RakNetGuid.IsAssigned)
            _core.CloseConnection(target.RakNetGuid.Value, command.Flag, command.Channel, command.Priority, nowUs);
        else
            _core.CloseConnection(ToEndPoint(target.Address), command.Flag, command.Channel, command.Priority, nowUs);
    }

    private void ApplySend(in PeerCommand command, long nowUs)
    {
        try
        {
            var connection = Find(command.Target);
            if (connection is null)
            {
                // The target went away between the send and this cycle.
                if (command.Reliability.HasAckReceipt())
                    Abandon(command.Receipt, connection, command.Target, command.State as TrackedSend, command.Version);
                return;
            }
            SendTo(connection, command, nowUs);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(command.Payload!);
        }
    }

    private void ApplyBroadcast(in PeerCommand command, long nowUs)
    {
        try
        {
            foreach (var connection in _table.All)
            {
                if (connection.State != ConnectionState.Connected || Matches(connection, command.Target))
                    continue;
                SendTo(connection, command, nowUs);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(command.Payload!);
        }
    }

    private void SendTo(RakConnection connection, in PeerCommand command, long nowUs)
    {
        var coreReceipt = _core.Send(connection.RakNetGuid.Value, command.Payload.AsSpan(0, command.ByteLength), command.BitLength,
                                     command.Reliability, command.Priority, command.Channel, nowUs);
        if (command.Reliability.HasAckReceipt())
            _receipts[coreReceipt] = new ReceiptRecord(command.Receipt, connection, command.State as TrackedSend, command.Version);
    }

    private void ReceiveDatagrams(long nowUs)
    {
        var filter = _lowLevel.IncomingDatagramFilter;
        for (var socket = 0; socket < _transports.Length; socket++)
        {
            var transport = _transports[socket];
            for (var read = 0; ; read++)
            {
                if (_queue.Count >= _eventCapacity)
                    _receivePaused = true;
                if (_receivePaused)
                    return;
                if (read == MaxDatagramsPerCycle)
                {
                    Wake();
                    break;
                }

                int length;
                SystemAddress remote;
                try
                {
                    if (!transport.TryReceive(_receiveBuffer, out length, out remote))
                        break;
                }
                catch (SocketException exception)
                {
                    throw new RakTransportException(socket, exception.SocketErrorCode);
                }

                // RakNetSocket2.cpp:357-361: only datagrams with bytesRead > 0 reach the peer.
                if (length == 0)
                    continue;
                var datagram = _receiveBuffer.AsSpan(0, length);
                if (filter is not null && !filter(datagram, remote, socket))
                    continue;
                foreach (var observer in _lowLevel.Observers.Current)
                    observer.OnDatagramReceived(datagram, remote);
                foreach (var observer in _plugins.Observers)
                    observer.OnDatagramReceived(datagram, remote);
                RememberSocket(remote, socket);
                _core.Receive(ToEndPoint(remote), datagram, nowUs);
            }
        }
    }

    private void FlushDatagrams(long nowUs)
    {
        while (_simulator.TryDequeueDue(nowUs, out var delayed))
        {
            SendDatagram(delayed.Data, delayed.Socket, delayed.Remote, ttl: 0);
            NetworkSimulator.Release(delayed);
        }
        while (_core.TryDequeueDatagram(out var datagram))
        {
            SystemAddress remote = datagram.Remote;
            var socket = SocketOf(remote);
            if (!_simulator.PassesNow(datagram.Data, socket, remote, nowUs))
                continue;
            if (datagram.DoNotFragment)
                SendProbe(datagram.Data, socket, datagram.Remote, nowUs);
            else
                SendDatagram(datagram.Data, socket, remote, ttl: 0);
        }
    }

    // RakPeer.cpp:5768-5821: an MTU probe goes out with don't-fragment set. One too large for the path skips the rest of
    // its MTU's attempts; one whose send took more than 100 ms drops the attempt to the smallest MTU.
    private void SendProbe(byte[] probe, int socket, IPEndPoint remote, long nowUs)
    {
        SystemAddress to = remote;
        NotifySent(probe, to);
        var start = _time.GetTimestamp();
        if (!_transports[socket].TrySendDoNotFragment(probe, to, out var error) && error == SocketError.MessageSize)
            _core.ReportDatagramTooLarge(remote, nowUs);
        else if ((long)_time.GetElapsedTime(start).TotalMilliseconds > 100)
            _core.ReportProbeSendSlow(remote, nowUs);
    }

    // RakPeer's direct socket sends: SendOutOfBand, AdvertiseSystem and SendTTL bypass the reliability layer.
    private void SendRawNow(in PeerCommand command)
    {
        try
        {
            SendDatagram(command.Payload.AsSpan(0, command.ByteLength), (int)command.Value, command.Target.Address, command.Ttl);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(command.Payload!);
        }
    }

    private void SendDatagram(ReadOnlySpan<byte> datagram, int socket, SystemAddress remote, int ttl)
    {
        NotifySent(datagram, remote);
        _transports[socket].TrySend(datagram, remote, ttl);
    }

    private void NotifySent(ReadOnlySpan<byte> datagram, SystemAddress remote)
    {
        foreach (var observer in _lowLevel.Observers.Current)
            observer.OnDatagramSent(datagram, remote);
        foreach (var observer in _plugins.Observers)
            observer.OnDatagramSent(datagram, remote);
    }

    private void ProcessCoreEvents(long nowUs)
    {
        while (_core.TryDequeueEvent(out var coreEvent))
        {
            switch (coreEvent)
            {
                case UnconnectedPingReceived ping:
                    // RakPeer.cpp:4672-4678: an answered ping is queued as its ID alone, from the sender.
                    SystemAddress pinger = ping.Remote;
                    EnqueueEvent(NewSegment(ping.Packet.Span, ping.Guid, pinger, _byAddress.GetValueOrDefault(pinger)));
                    break;
                case UnconnectedPongReceived pong:
                    OnPong(pong);
                    break;
                case ConnectionAttemptFailed failed:
                    OnConnectFailed(failed);
                    break;
                case OutgoingConnectionCompleted outgoing:
                    OnConnected(outgoing.Remote, outgoing.Guid, outgoing.Data.Span, isIncoming: false, outgoing.ExternalAddress, nowUs);
                    break;
                case IncomingConnectionCompleted incoming:
                    var external = _core.TryGetRemoteSystem(incoming.Guid, out var system) ? (SystemAddress)system.MyExternalAddress : SystemAddress.Unassigned;
                    OnConnected(incoming.Remote, incoming.Guid, incoming.Data.Span, isIncoming: true, external, nowUs);
                    break;
                case MessageReceived message:
                    OnMessage(message);
                    break;
                case SendReceiptAcked acked:
                    OnReceipt(acked.Receipt, acked: true);
                    break;
                case SendReceiptLost lost:
                    OnReceipt(lost.Receipt, acked: false);
                    break;
                case SendReceiptDiscarded discarded:
                    // RakNet reports nothing for an unreliable message culled unsent; the receipt still has to end.
                    if (_receipts.Remove(discarded.Receipt, out var record))
                        Abandon(record.Receipt, record.Connection, record.Connection.RakNetGuid, record.Tracker, record.Version);
                    break;
                case AdvertiseSystemReceived or OutOfBandReceived:
                    SystemAddress sender = coreEvent.Remote;
                    EnqueueEvent(NewSegment(coreEvent.Packet.Span, coreEvent.Guid, sender, _byAddress.GetValueOrDefault(sender)));
                    break;
                case Protocol.Peer.Disconnected disconnected:
                    OnDisconnected(disconnected.Remote, disconnected.Guid, DisconnectReason.DisconnectionNotification);
                    break;
                case ConnectionLost lost:
                    OnDisconnected(lost.Remote, lost.Guid, DisconnectReason.ConnectionLost);
                    break;
            }
        }
    }

    private void OnPong(UnconnectedPongReceived pong)
    {
        SystemAddress address = pong.Remote;
        var segment = NewSegment(pong.Packet.Span, pong.Guid, address, connection: null);
        var view = new UnconnectedPongView(segment.Data, segment.ReceivedTime);
        var result = new PongResult(new RakNetGuid(pong.Guid), address, view.RoundTrip, pong.ResponseData.ToArray());
        EnqueueEvent(segment);

        List<PendingPing>? answered = null;
        lock (_pingsGate)
        {
            for (var i = _pings.Count - 1; i >= 0; i--)
            {
                if (_pings[i].Address != address)
                    continue;
                (answered ??= []).Add(_pings[i]);
                _pings.RemoveAt(i);
            }
        }
        if (answered is null)
            return;
        foreach (var ping in answered)
        {
            ping.Dispose();
            ping.Completion.TrySetResult(result);
        }
    }

    private void OnConnectFailed(ConnectionAttemptFailed failed)
    {
        SystemAddress address = failed.Remote;
        PendingConnect? attempt;
        lock (_attemptsGate)
            _attempts.Remove(address, out attempt);

        var segment = NewSegment(failed.Data.Span, failed.Guid, address, connection: null);
        EnqueueEvent(segment);
        attempt?.Complete(new ConnectResult(ConnectAttemptResult.Started, segment.ConnectFailure, connection: null, segment.RemoteProtocolVersion));
    }

    private void OnConnected(IPEndPoint remote, ulong guid, ReadOnlySpan<byte> data, bool isIncoming, SystemAddress external, long nowUs)
    {
        SystemAddress address = remote;
        if (!_byGuid.TryGetValue(guid, out var connection))
        {
            connection = new RakConnection(this, new RakNetGuid(guid), address, isIncoming, _sockets[SocketOf(address)], external, nowUs);
            if (_core.TryGetRemoteSystem(guid, out var system))
            {
                connection.System = system;
                connection.Refresh(system, ConnectionState.Connected);
            }
            _byGuid[guid] = connection;
            _byAddress[address] = connection;
            PublishTable();
        }
        if (!ExternalAddress.IsAssigned && external.IsAssigned)
            _externalAddress = new SystemAddressBox(external);

        var segment = NewSegment(data, guid, address, connection);
        EnqueueEvent(segment);

        if (isIncoming)
            return;
        PendingConnect? attempt;
        lock (_attemptsGate)
            _attempts.Remove(address, out attempt);
        attempt?.Complete(new ConnectResult(ConnectAttemptResult.Started, failure: null, connection, remoteProtocolVersion: 0));
    }

    private void OnMessage(MessageReceived message)
    {
        _byGuid.TryGetValue(message.Guid, out var connection);
        var segment = NewSegment(message.Data.Span, message.Guid, message.Remote, connection);
        segment.BitLength = message.BitLength;
        EnqueueEvent(segment);
    }

    private void OnReceipt(uint coreReceipt, bool acked)
    {
        if (!_receipts.Remove(coreReceipt, out var record))
            return;
        if (record.Tracker is { } tracker)
        {
            tracker.TryComplete(record.Version, new DeliveryOutcome(acked ? DeliveryStatus.Acked : DeliveryStatus.Lost, record.Receipt, SendStatus.Queued));
            return;
        }
        // ReliabilityLayer.cpp:2493-2494: the receipt follows the ID in host order.
        Span<byte> data = stackalloc byte[5];
        data[0] = (byte)(acked ? MessageId.SndReceiptAcked : MessageId.SndReceiptLoss);
        BinaryPrimitives.WriteUInt32LittleEndian(data[1..], record.Receipt.Value);
        EnqueueEvent(NewSegment(data, record.Connection.RakNetGuid.Value, record.Connection.Address, record.Connection));
    }

    private void OnDisconnected(IPEndPoint remote, ulong guid, DisconnectReason reason)
    {
        _byGuid.TryGetValue(guid, out var connection);
        if (connection is not null)
            AbandonReceipts(connection);
        var closedLocally = _closedLocally.Remove(guid, out var silently);

        ReadOnlySpan<byte> data = [(byte)(reason == DisconnectReason.ConnectionLost ? MessageId.ConnectionLost : MessageId.DisconnectionNotification)];
        var segment = NewSegment(data, guid, remote, connection);
        segment.ClosedLocally = closedLocally;
        // RakPeer.cpp:1667-1678: the ID_CONNECTION_LOST of a silent local close is generated locally.
        segment.IsLocal = closedLocally && silently && reason == DisconnectReason.ConnectionLost;
        EnqueueEvent(segment);
        if (connection is not null)
            EndConnection(connection);
    }

    /// <summary>Copies the core's slots into the connection objects, and notices connections that ended silently.</summary>
    private void PollConnections(long nowUs)
    {
        foreach (var connection in _table.All)
        {
            var guid = connection.RakNetGuid.Value;
            if (!_byGuid.ContainsKey(guid))
                continue;
            // The slot the connection was made on, kept so the poll costs no lookup; the core deactivates a slot when it
            // forgets the system and never reuses the object.
            if (connection.System is not { IsActive: true } system)
            {
                AbandonReceipts(connection);
                EndConnection(connection);
                continue;
            }
            connection.Refresh(system, (ConnectionState)PeerCore.GetConnectionState(system));
        }
        if (ConnectedChanged())
            PublishTable();

        lock (_attemptsGate)
        {
            if (_attempts.Count == 0)
                return;
            _scratchAttempts.Clear();
            _scratchAttempts.AddRange(_attempts.Values);
        }
        foreach (var attempt in _scratchAttempts)
        {
            if (!attempt.Applied)
                continue;
            attempt.State = _core.GetConnectionState(attempt.EndPoint) == Protocol.Peer.ConnectionState.IsPending
                ? ConnectionState.Pending
                : ConnectionState.Connecting;
        }
        _scratchAttempts.Clear();
    }

    private void AbandonReceipts(RakConnection connection)
    {
        _scratchReceipts.Clear();
        foreach (var (coreReceipt, record) in _receipts)
        {
            if (ReferenceEquals(record.Connection, connection))
                _scratchReceipts.Add(coreReceipt);
        }
        _scratchReceipts.Sort();
        foreach (var coreReceipt in _scratchReceipts)
        {
            _receipts.Remove(coreReceipt, out var record);
            Abandon(record.Receipt, connection, connection.RakNetGuid, record.Tracker, record.Version);
        }
        _scratchReceipts.Clear();
    }

    private void Abandon(SendReceipt receipt, RakConnection? connection, AddressOrGuid target, TrackedSend? tracker, short version)
    {
        if (tracker is not null)
        {
            tracker.TryComplete(version, new DeliveryOutcome(DeliveryStatus.Abandoned, receipt, SendStatus.Queued));
            return;
        }
        var segment = NewSegment([], connection?.RakNetGuid.Value ?? target.RakNetGuid.Value, connection?.Address ?? target.Address, connection);
        segment.Kind = PeerEventKind.ReceiptAbandoned;
        segment.Receipt = receipt;
        EnqueueEvent(segment);
    }

    private void EndConnection(RakConnection connection)
    {
        var guid = connection.RakNetGuid.Value;
        connection.MarkClosed();
        if (_byGuid.TryGetValue(guid, out var byGuid) && ReferenceEquals(byGuid, connection))
            _byGuid.Remove(guid);
        if (_byAddress.TryGetValue(connection.Address, out var byAddress) && ReferenceEquals(byAddress, connection))
            _byAddress.Remove(connection.Address);
        _closedLocally.Remove(guid);
        PublishTable();
    }

    /// <summary>Whether a connection entered or left Connected since the table was published.</summary>
    private bool ConnectedChanged()
    {
        var table = _table;
        var connected = 0;
        foreach (var connection in table.All)
        {
            if (connection.State == ConnectionState.Connected)
                connected++;
        }
        if (connected != table.Connected.Length)
            return true;
        foreach (var connection in table.Connected)
        {
            if (connection.State != ConnectionState.Connected)
                return true;
        }
        return false;
    }

    private void PublishTable() => _table = new ConnectionTable([.. _byGuid.Values.OrderBy(c => c.SystemIndex)]);

    private RakConnection? Find(AddressOrGuid target)
    {
        if (target.RakNetGuid.IsAssigned)
            return _byGuid.GetValueOrDefault(target.RakNetGuid.Value);
        return target.Address.IsAssigned ? _byAddress.GetValueOrDefault(target.Address) : null;
    }

    private static bool Matches(RakConnection connection, AddressOrGuid target) =>
        (target.RakNetGuid.IsAssigned && connection.RakNetGuid == target.RakNetGuid)
        || (target.Address.IsAssigned && connection.Address == target.Address);

    private EventSegment NewSegment(ReadOnlySpan<byte> data, ulong guid, SystemAddress address, RakConnection? connection)
    {
        var segment = _pool.Rent();
        segment.SetData(data);
        segment.RakNetGuid = new RakNetGuid(guid);
        segment.Address = address;
        segment.Connection = connection;
        segment.SocketIndex = SocketOf(address);
        segment.ReceivedTime = GetTime();
        Classify(segment);
        return segment;
    }

    private void EnqueueEvent(EventSegment segment)
    {
        _queue.Enqueue(segment);
        _reader.Signal(true);
    }

    /// <summary>The kind and typed fields of an entry, from its first byte (docs/api-design.md section 4).</summary>
    private static void Classify(EventSegment segment)
    {
        var data = segment.Data;
        segment.Kind = PeerEventKind.Message;
        if (data.IsEmpty)
            return;
        var id = (MessageId)data[0];
        switch (id)
        {
            case MessageId.ConnectionRequestAccepted or MessageId.NewIncomingConnection:
                segment.Kind = PeerEventKind.Connected;
                segment.IsIncoming = id == MessageId.NewIncomingConnection;
                break;
            case MessageId.DisconnectionNotification or MessageId.ConnectionLost:
                segment.Kind = PeerEventKind.Disconnected;
                segment.DisconnectReason = id == MessageId.ConnectionLost ? DisconnectReason.ConnectionLost : DisconnectReason.DisconnectionNotification;
                break;
            case MessageId.SndReceiptAcked or MessageId.SndReceiptLoss:
                segment.Kind = id == MessageId.SndReceiptAcked ? PeerEventKind.ReceiptAcked : PeerEventKind.ReceiptLost;
                if (data.Length >= 5)
                    segment.Receipt = new SendReceipt(BinaryPrimitives.ReadUInt32LittleEndian(data[1..]));
                break;
            case MessageId.UnconnectedPong:
                segment.Kind = PeerEventKind.UnconnectedPong;
                break;
            case MessageId.AdvertiseSystem:
                segment.Kind = PeerEventKind.AdvertiseSystem;
                break;
            case MessageId.OutOfBandInternal:
                segment.Kind = PeerEventKind.OutOfBand;
                break;
            case MessageId.DownloadProgress:
                segment.Kind = PeerEventKind.DownloadProgress;
                break;
            default:
                if (FailureOf(id) is { } failure)
                {
                    segment.Kind = PeerEventKind.ConnectFailed;
                    segment.ConnectFailure = failure;
                    if (failure == ConnectFailure.IncompatibleProtocolVersion && data.Length > 1)
                        segment.RemoteProtocolVersion = data[1];
                }
                break;
        }
    }

    // CallPluginCallbacks' failure IDs (RakPeer.cpp:6429-6458).
    private static ConnectFailure? FailureOf(MessageId id) => id switch
    {
        MessageId.ConnectionAttemptFailed => ConnectFailure.ConnectionAttemptFailed,
        MessageId.AlreadyConnected => ConnectFailure.AlreadyConnected,
        MessageId.NoFreeIncomingConnections => ConnectFailure.NoFreeIncomingConnections,
        MessageId.ConnectionBanned => ConnectFailure.ConnectionBanned,
        MessageId.InvalidPassword => ConnectFailure.InvalidPassword,
        MessageId.IncompatibleProtocolVersion => ConnectFailure.IncompatibleProtocolVersion,
        MessageId.IpRecentlyConnected => ConnectFailure.IpRecentlyConnected,
        MessageId.RemoteSystemRequiresPublicKey => ConnectFailure.RemoteSystemRequiresPublicKey,
        MessageId.OurSystemRequiresSecurity => ConnectFailure.OurSystemRequiresSecurity,
        MessageId.PublicKeyMismatch => ConnectFailure.PublicKeyMismatch,
        _ => null,
    };

    // CallPluginCallbacks (RakPeer.cpp:6409-6461), keyed on Data[0].
    private static void CallConnectionCallbacks(IRakPlugin[] plugins, PeerEvent view, EventSegment segment)
    {
        if (plugins.Length == 0 || segment.Length == 0)
            return;
        var id = (MessageId)segment.Data[0];
        foreach (var plugin in plugins)
        {
            switch (id)
            {
                case MessageId.DisconnectionNotification when segment.Connection is { } closed:
                    plugin.OnDisconnected(closed, DisconnectReason.DisconnectionNotification);
                    break;
                case MessageId.ConnectionLost when segment.Connection is { } lost:
                    plugin.OnDisconnected(lost, DisconnectReason.ConnectionLost);
                    break;
                case MessageId.NewIncomingConnection or MessageId.ConnectionRequestAccepted when segment.Connection is { } connected:
                    plugin.OnConnected(connected, id == MessageId.NewIncomingConnection);
                    break;
                default:
                    if (FailureOf(id) is { } failure)
                        plugin.OnConnectFailed(view, failure);
                    break;
            }
        }
    }

    private static bool IsConsumed(IRakPlugin[] plugins, PeerEvent view)
    {
        foreach (var plugin in plugins)
        {
            if (plugin.OnEvent(view) == PluginResult.Consume)
                return true;
        }
        return false;
    }

    private void InjectSegment(EventSegment segment, bool atHead)
    {
        if (InDispatchOnThisThread)
        {
            ApplyInject(segment, atHead);
            return;
        }
        QueueDispatchOp(new DispatchOp(null, null, segment, atHead));
    }

    // RakPeer::PushBackPacket (RakPeer.cpp:2826-2843).
    private void ApplyInject(EventSegment segment, bool atHead)
    {
        foreach (var plugin in _plugins.Ordinary)
            plugin.OnInjected(segment.Data, segment.BitLength, segment.Address);
        foreach (var plugin in _plugins.ReliabilityLayer)
            plugin.OnInjected(segment.Data, segment.BitLength, segment.Address);
        if (atHead)
            _queue.EnqueueAtHead(segment);
        else
            _queue.Enqueue(segment);
    }

    private void QueueDispatchOp(DispatchOp op)
    {
        _dispatchOps.Enqueue(op);
        _reader.Signal(true);
    }

    private void ApplyDispatchOps()
    {
        while (_dispatchOps.TryDequeue(out var op))
        {
            if (op.Attach is { } attach)
                _plugins.Apply(attach);
            if (op.Detach is { } detach)
                _plugins.Remove(detach);
            if (op.Inject is { } segment)
            {
                segment.ReceivedTime = GetTime();
                ApplyInject(segment, op.AtHead);
            }
        }
    }

    private void ReleaseBorrowed()
    {
        var borrowed = _borrowed;
        if (borrowed is null)
            return;
        _borrowed = null;
        if (!borrowed.IsOwned)
            borrowed.TryRelease(_borrowedGeneration);
    }

    private void ResumeReceivingIfDrained()
    {
        if (_receivePaused && _queue.Count < _eventCapacity / 2)
            Wake();
    }

    private void EnterDispatch()
    {
        if (!TryEnterDispatch())
            ThrowConcurrentDispatch();
    }

    private bool TryEnterDispatch() => Interlocked.CompareExchange(ref _dispatchThread, Environment.CurrentManagedThreadId, 0) == 0;

    private static void ThrowConcurrentDispatch() =>
        throw new InvalidOperationException("The event stream is read by one caller at a time, and not from inside a plugin hook.");

    private void ExitDispatch() => Volatile.Write(ref _dispatchThread, 0);

    /// <summary>Waits for a concurrent dispatch step to finish and takes the guard; false when this thread holds it already.</summary>
    private bool AcquireDispatch()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _dispatchThread) == thread)
            return false;
        var spin = new SpinWait();
        while (Interlocked.CompareExchange(ref _dispatchThread, thread, 0) != 0)
            spin.SpinOnce();
        return true;
    }

    /// <summary>From the start of shutdown the stream delivers nothing more (RakPeer.cpp:1041-1181).</summary>
    private void CloseStream()
    {
        _streamClosed = true;
        var acquired = AcquireDispatch();
        try
        {
            ReleaseBorrowed();
        }
        finally
        {
            if (acquired)
                ExitDispatch();
        }
        _reader.Signal(false);
    }

    /// <summary>The end of RakPeer::Shutdown: plugins' OnShutdown, sockets closed, everything pending ended.</summary>
    private void Stop(Exception? fault)
    {
        lock (_cycleGate)
        {
            if (Volatile.Read(ref _state) == Stopped)
                return;
            _stopFault = fault;
            // A full fence, so a command enqueued after this is either drained below or sees Stopped (Enqueue).
            Interlocked.Exchange(ref _state, Stopped);
        }
        _streamClosed = true;
        _loopStopping = true;
        _updateTimer?.Dispose();
        _wake?.Set();
        if (_loop is not null && !ReferenceEquals(Thread.CurrentThread, _loop))
            _loop.Join();

        var acquired = AcquireDispatch();
        try
        {
            ReleaseBorrowed();
            // RakPeer.cpp:1086-1093.
            foreach (var plugin in _plugins.Ordinary)
                plugin.OnShutdown();
            foreach (var plugin in _plugins.ReliabilityLayer)
                plugin.OnShutdown();
        }
        finally
        {
            if (acquired)
                ExitDispatch();
            EndEverything(fault);
        }
    }

    private void EndEverything(Exception? fault)
    {
        _simulator.Clear();
        foreach (var socket in _sockets)
            socket.Close();
        foreach (var transport in _transports)
            transport.Dispose();

        // RakPeer.cpp:1176-1181: undelivered packets are discarded.
        while (_queue.TryDequeue(out var segment))
            segment.TryRelease(Volatile.Read(ref segment.Generation));
        while (_dispatchOps.TryDequeue(out var op))
            op.Inject?.TryRelease(Volatile.Read(ref op.Inject.Generation));

        EndPending(fault);

        lock (_cycleGate)
        {
            foreach (var record in _receipts.Values)
            {
                if (record.Tracker is { } tracker)
                    EndTracked(tracker, record.Version, record.Receipt, fault);
            }
            _receipts.Clear();
            foreach (var connection in _byGuid.Values)
                connection.MarkClosed();
        }

        _plugins.ReleaseAll();
        _reader.Complete(fault);
        _stopped.TrySetResult();
    }

    /// <summary>
    /// Ends every command still queued and every registered attempt and ping. Run by shutdown, and again by a command
    /// that reaches the queue after shutdown drained it; safe to run concurrently.
    /// </summary>
    private void EndPending(Exception? fault)
    {
        var shutdown = fault ?? new OperationCanceledException("The peer shut down.");
        while (_commands.TryDequeue(out var command))
        {
            if (command.Payload is { } payload)
                ArrayPool<byte>.Shared.Return(payload);
            if (command.State is TrackedSend tracker)
                EndTracked(tracker, command.Version, command.Receipt, fault);
            if (command.Kind == CommandKind.Connect && command.State is PendingConnect connect)
                connect.Fail(shutdown);
        }

        PendingConnect[] attempts;
        lock (_attemptsGate)
        {
            attempts = [.. _attempts.Values];
            _attempts.Clear();
        }
        foreach (var attempt in attempts)
            attempt.Fail(shutdown);

        PendingPing[] pings;
        lock (_pingsGate)
        {
            pings = [.. _pings];
            _pings.Clear();
        }
        foreach (var ping in pings)
        {
            ping.Dispose();
            if (fault is null)
                ping.Completion.TrySetCanceled();
            else
                ping.Completion.TrySetException(fault);
        }
    }

    private static void EndTracked(TrackedSend tracker, short version, SendReceipt receipt, Exception? fault)
    {
        if (fault is null)
            tracker.TryComplete(version, new DeliveryOutcome(DeliveryStatus.PeerShutdown, receipt, SendStatus.Queued));
        else
            tracker.TryFail(version, fault);
    }

    private int SocketOf(SystemAddress address) => _remoteSockets.GetValueOrDefault(address);

    private void RememberSocket(SystemAddress address, int socket)
    {
        if (socket == 0)
        {
            if (_remoteSockets.Count > 0)
                _remoteSockets.Remove(address);
            return;
        }
        if (_remoteSockets.Count >= AddressCacheCapacity)
            _remoteSockets.Clear();
        _remoteSockets[address] = socket;
    }

    private IPEndPoint ToEndPoint(SystemAddress address)
    {
        if (_endPoints.TryGetValue(address, out var endPoint))
            return endPoint;
        if (_endPoints.Count >= AddressCacheCapacity)
            _endPoints.Clear();
        endPoint = address.ToIPEndPoint();
        _endPoints.Add(address, endPoint);
        return endPoint;
    }
}
