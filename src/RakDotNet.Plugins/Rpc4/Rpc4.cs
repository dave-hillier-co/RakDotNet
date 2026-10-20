using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>Who a <see cref="Rpc4"/> function was called by: Packet's guid, systemAddress and wasGeneratedLocally.</summary>
/// <param name="RakNetGuid">The calling system, or this one for a local invocation.</param>
/// <param name="Address">The calling system's address; <see cref="SystemAddress.Unassigned"/> for CallLoopback.</param>
/// <param name="IsLocal">True for a signal invoked locally, which RakNet marks as generated locally.</param>
public readonly record struct Rpc4Sender(RakNetGuid RakNetGuid, SystemAddress Address, bool IsLocal);

/// <summary>A function or slot callable through <see cref="Rpc4"/>: RPC4's <c>void (*)(BitStream*, Packet*)</c>.</summary>
/// <param name="parameters">The parameters, from their first bit; reading past them fails.</param>
/// <param name="sender">The calling system.</param>
public delegate void Rpc4Function(BitStreamReader parameters, Rpc4Sender sender);

/// <summary>A function for <see cref="Rpc4.CallBlockingAsync"/>, which writes the value returned to the caller into <paramref name="returnData"/>.</summary>
public delegate void Rpc4BlockingFunction(BitStreamReader parameters, BitStream returnData, Rpc4Sender sender);

/// <summary>
/// RPC4Plugin: remote procedure calls by name. Functions are registered under a name and called by it on a remote system;
/// slots are registered under a shared name and all run, by priority, when it is signalled; blocking functions return a
/// value to the caller.
/// </summary>
/// <remarks>
/// <para>
/// Ported from RPC4Plugin.cpp. A call is ID_RPC_PLUGIN, ID_RPC4_CALL (0), the name as a compressed RakString, a bit set
/// for a blocking call, then, when there are parameters, the parameters from the next byte boundary; a signal is
/// ID_RPC_PLUGIN, ID_RPC4_SIGNAL (2), the name and the aligned parameters; a return is ID_RPC_PLUGIN, ID_RPC4_RETURN (1)
/// and the returned bits. Every ID_RPC_PLUGIN message is consumed. A call to a name with no function is answered with
/// ID_RPC_REMOTE_ERROR, RPC_ERROR_FUNCTION_NOT_REGISTERED (0) and the name with its terminating zero, which reaches the
/// caller's application. Functions see the whole received message from the parameters on, padded to a whole byte as
/// RakNet reads it.
/// </para>
/// <para>
/// <see cref="CallBlockingAsync"/> completes when the return arrives, while the application keeps reading the event
/// stream, instead of reading the stream itself as RPC4::CallBlocking does; so messages that arrive meanwhile reach the
/// application in order, where RakNet drops those that came before a return (RPC4Plugin.cpp:603-652). It returns false on
/// the remote error for its function, which RakNet means to do but misses, because it reads the name in the error as a
/// length-prefixed RakString (RPC4Plugin.cpp:624-637); RakNet then waits until the connection ends. As in RakNet, one
/// blocking call waits at a time and any return completes it.
/// </para>
/// <para>
/// Global registration (RPC4GlobalRegistration) is not ported: register on the instance. Members may be called from any
/// thread; functions run on the dispatch step, except the local slots of <see cref="Signal"/>, which run on the caller.
/// </para>
/// </remarks>
public sealed class Rpc4 : IRakPlugin
{
    // RPC4Identifiers (RPC4Plugin.cpp:96-101).
    private const byte CallId = 0;
    private const byte ReturnId = 1;
    private const byte SignalId = 2;

    // RPCErrorCodes::RPC_ERROR_FUNCTION_NOT_REGISTERED.
    private const byte FunctionNotRegistered = 0;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Rpc4Function> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rpc4BlockingFunction> _blockingFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Slot>> _slots = new(StringComparer.Ordinal);
    private readonly SortedDictionary<byte, SortedSet<string>> _localCallbacks = [];
    private uint _nextSlotRegistrationCount;
    private volatile bool _interruptSignal;
    private IPluginHost? _host;
    private PendingCall? _pending;

    /// <summary>
    /// RegisterFunction: makes <paramref name="function"/> callable as <paramref name="uniqueId"/>. False when the name is
    /// taken. RakNet deprecates it in favour of <see cref="RegisterSlot"/>.
    /// </summary>
    public bool RegisterFunction(string uniqueId, Rpc4Function function)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        ArgumentNullException.ThrowIfNull(function);
        lock (_gate)
            return _functions.TryAdd(uniqueId, function);
    }

    /// <summary>
    /// RegisterSlot: adds <paramref name="function"/> to the slots signalled as <paramref name="sharedIdentifier"/>. Slots
    /// run highest <paramref name="callPriority"/> first, and in registration order within a priority.
    /// </summary>
    public void RegisterSlot(string sharedIdentifier, Rpc4Function function, int callPriority)
    {
        ArgumentNullException.ThrowIfNull(sharedIdentifier);
        ArgumentNullException.ThrowIfNull(function);
        lock (_gate)
        {
            // LocalSlotObjectComp (RPC4Plugin.cpp:102-116).
            var slot = new Slot(_nextSlotRegistrationCount++, callPriority, function);
            if (!_slots.TryGetValue(sharedIdentifier, out var slots))
                _slots.Add(sharedIdentifier, slots = []);
            var index = slots.FindIndex(s => s.CallPriority < callPriority);
            slots.Insert(index < 0 ? slots.Count : index, slot);
        }
    }

    /// <summary>RegisterBlockingFunction: as <see cref="RegisterFunction"/>, for <see cref="CallBlockingAsync"/>.</summary>
    public bool RegisterBlockingFunction(string uniqueId, Rpc4BlockingFunction function)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        ArgumentNullException.ThrowIfNull(function);
        lock (_gate)
            return _blockingFunctions.TryAdd(uniqueId, function);
    }

    /// <summary>
    /// RegisterLocalCallback: whenever a message starting with <paramref name="messageId"/> arrives, runs the function
    /// registered as <paramref name="uniqueId"/> with the whole message; functions for one ID run in name order and the
    /// message continues to the application. RakNet deprecates it in favour of slots.
    /// </summary>
    public void RegisterLocalCallback(string uniqueId, byte messageId)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        lock (_gate)
        {
            if (!_localCallbacks.TryGetValue(messageId, out var names))
                _localCallbacks.Add(messageId, names = new SortedSet<string>(Utf8Order.Instance));
            names.Add(uniqueId);
        }
    }

    /// <summary>UnregisterFunction: false when the name has no function.</summary>
    public bool UnregisterFunction(string uniqueId)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        lock (_gate)
            return _functions.Remove(uniqueId);
    }

    /// <summary>UnregisterBlockingFunction: false when the name has no blocking function.</summary>
    public bool UnregisterBlockingFunction(string uniqueId)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        lock (_gate)
            return _blockingFunctions.Remove(uniqueId);
    }

    /// <summary>
    /// UnregisterLocalCallback: removes the pairing. True only when it removed the last name for
    /// <paramref name="messageId"/>, as RakNet returns (RPC4Plugin.cpp:460-484).
    /// </summary>
    public bool UnregisterLocalCallback(string uniqueId, byte messageId)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        lock (_gate)
        {
            if (!_localCallbacks.TryGetValue(messageId, out var names) || !names.Remove(uniqueId) || names.Count > 0)
                return false;
            _localCallbacks.Remove(messageId);
            return true;
        }
    }

    /// <summary>UnregisterSlot: removes every slot for <paramref name="sharedIdentifier"/>; false when there were none.</summary>
    public bool UnregisterSlot(string sharedIdentifier)
    {
        ArgumentNullException.ThrowIfNull(sharedIdentifier);
        lock (_gate)
            return _slots.Remove(sharedIdentifier);
    }

    /// <summary>
    /// CallLoopback: calls this system's function <paramref name="uniqueId"/> through the event stream, from this system's
    /// GUID and <see cref="SystemAddress.Unassigned"/>. For an unregistered name the remote error is queued instead.
    /// RakNet deprecates it in favour of <see cref="Signal"/> to this system's GUID.
    /// </summary>
    public void CallLoopback(string uniqueId, BitStream? parameters)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        var host = RequireHost();
        bool registered;
        lock (_gate)
            registered = _functions.ContainsKey(uniqueId);
        // RPC4Plugin.cpp:498-560.
        if (!registered)
        {
            host.Inject(RemoteError(Encoding.UTF8.GetBytes(uniqueId)), SystemAddress.Unassigned, host.RakNetGuid);
            return;
        }
        using var message = BuildMessage(CallId, uniqueId, isBlocking: false, parameters);
        host.Inject(message.Written, SystemAddress.Unassigned, host.RakNetGuid);
    }

    /// <summary>
    /// Call: calls the function <paramref name="uniqueId"/> on <paramref name="target"/>, or with
    /// <paramref name="broadcast"/> on every connected system but <paramref name="target"/>. RakNet deprecates it in favour
    /// of <see cref="Signal"/>.
    /// </summary>
    public void Call(string uniqueId, BitStream? parameters, Priority priority, Reliability reliability, byte orderingChannel, AddressOrGuid target, bool broadcast)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        var host = RequireHost();
        using var message = BuildMessage(CallId, uniqueId, isBlocking: false, parameters);
        SendUnified(host, message, priority, reliability, orderingChannel, target, broadcast);
    }

    /// <summary>
    /// CallBlocking: calls the blocking function <paramref name="uniqueId"/> on <paramref name="target"/> and completes with
    /// true once it returns, with what it wrote in <paramref name="returnData"/>. Completes with false at once when
    /// <paramref name="target"/> is not connected, and later when it disconnects or answers that the function is not
    /// registered. The application must keep reading <see cref="RakPeer.Events"/> meanwhile.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another blocking call is waiting, or the plugin is not attached to a peer.</exception>
    public Task<bool> CallBlockingAsync(string uniqueId, BitStream? parameters, Priority priority, Reliability reliability, byte orderingChannel,
                                        AddressOrGuid target, BitStream returnData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        ArgumentNullException.ThrowIfNull(returnData);
        var host = RequireHost();
        var peer = host.Peer ?? throw new InvalidOperationException("A blocking call needs a RakPeer host.");
        cancellationToken.ThrowIfCancellationRequested();
        // RPC4Plugin.cpp:576-597.
        if (peer.GetConnectionState(target) != ConnectionState.Connected)
            return Task.FromResult(false);
        using var message = BuildMessage(CallId, uniqueId, isBlocking: true, parameters);
        var pending = new PendingCall(uniqueId, target, returnData);
        lock (_gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException("A blocking call is already waiting for its return.");
            _pending = pending;
        }
        returnData.Reset();
        if (cancellationToken.CanBeCanceled)
        {
            pending.Cancellation = cancellationToken.Register(() =>
            {
                if (TryTakePending(pending))
                    pending.Completion.TrySetCanceled(cancellationToken);
            });
        }
        host.Send(message, target, reliability, priority, orderingChannel);
        return pending.Completion.Task;
    }

    /// <summary>
    /// Signal: runs the slots <paramref name="sharedIdentifier"/> on <paramref name="target"/>, or with
    /// <paramref name="broadcast"/> on every connected system but <paramref name="target"/>, and with
    /// <paramref name="invokeLocal"/> also this system's slots, at once on the calling thread.
    /// </summary>
    public void Signal(string sharedIdentifier, BitStream? parameters, Priority priority, Reliability reliability, byte orderingChannel,
                       AddressOrGuid target, bool broadcast, bool invokeLocal)
    {
        ArgumentNullException.ThrowIfNull(sharedIdentifier);
        var host = RequireHost();
        // RPC4Plugin.cpp:654-704.
        using (var message = BuildMessage(SignalId, sharedIdentifier, isBlocking: null, parameters))
            SendUnified(host, message, priority, reliability, orderingChannel, target, broadcast);
        if (!invokeLocal)
            return;
        var address = host.Peer is { } peer ? peer.InternalAddresses[0] : SystemAddress.Unassigned;
        var sender = new Rpc4Sender(host.RakNetGuid, address, IsLocal: true);
        if (parameters is null)
            InvokeSignal(sharedIdentifier, default, sender);
        else
            InvokeSignal(sharedIdentifier, parameters.CreateReader(), sender);
    }

    /// <summary>InterruptSignal: called from a slot, stops the remaining slots of the signal being run.</summary>
    public void InterruptSignal() => _interruptSignal = true;

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached()
    {
        _host = null;
        FailPending();
    }

    void IRakPlugin.OnShutdown() => FailPending();

    // RPC4Plugin.cpp:612-623: the blocking call ends when its target's connection does.
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        PendingCall? failed = null;
        lock (_gate)
        {
            if (_pending is { } pending && ((pending.Target.RakNetGuid.IsAssigned && pending.Target.RakNetGuid == connection.RakNetGuid)
                                             || (pending.Target.Address.IsAssigned && pending.Target.Address == connection.Address)))
            {
                failed = pending;
                _pending = null;
            }
        }
        failed?.Complete(false);
    }

    /// <summary>OnReceive (RPC4Plugin.cpp:763-867).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        var sender = new Rpc4Sender(e.RakNetGuid, e.Address, e.IsLocal);
        if (data[0] == (byte)MessageId.RpcPlugin)
        {
            // RakNet reads data[1] of a one-byte message past its end; here it is dropped.
            if (data.Length >= 2)
                OnRpcMessage(data, sender);
            return PluginResult.Consume;
        }
        if (data[0] == (byte)MessageId.RpcRemoteError)
            OnRemoteError(data);
        RunLocalCallbacks(data, sender);
        return PluginResult.Continue;
    }

    private void OnRpcMessage(ReadOnlySpan<byte> data, Rpc4Sender sender)
    {
        // BitStream bsIn(packet->data, packet->length, false): whole bytes.
        var reader = new BitStreamReader(data, data.Length * 8);
        reader.IgnoreBits(16);
        switch (data[1])
        {
            case CallId:
                OnCall(ref reader, sender);
                break;
            case SignalId:
            {
                var name = ReadName(ref reader);
                reader.AlignToByte();
                InvokeSignal(name, reader, sender);
                break;
            }
            default:
                // RakNet takes any other sub-ID as ID_RPC4_RETURN (RPC4Plugin.cpp:833-839).
                OnReturn(ref reader);
                break;
        }
    }

    private void OnCall(ref BitStreamReader reader, Rpc4Sender sender)
    {
        var nameBytes = ReadNameBytes(ref reader);
        var name = Encoding.UTF8.GetString(nameBytes);
        reader.TryReadBit(out var isBlocking);
        Rpc4Function? function = null;
        Rpc4BlockingFunction? blockingFunction = null;
        lock (_gate)
        {
            if (isBlocking)
                _blockingFunctions.TryGetValue(name, out blockingFunction);
            else
                _functions.TryGetValue(name, out function);
        }
        if (function is null && blockingFunction is null)
        {
            SendToSender(RemoteError(nameBytes), sender, Priority.High);
            return;
        }
        reader.AlignToByte();
        if (function is not null)
        {
            function(reader, sender);
            return;
        }
        using var returnData = new BitStream();
        blockingFunction!(reader, returnData, sender);
        using var message = new BitStream();
        message.WriteByte((byte)MessageId.RpcPlugin);
        message.WriteByte(ReturnId);
        var returned = returnData.CreateReader();
        message.Write(ref returned);
        SendToSender(message, sender, Priority.Immediate);
    }

    private void OnReturn(ref BitStreamReader reader)
    {
        PendingCall? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }
        if (pending is null)
            return;
        pending.ReturnData.Write(ref reader);
        pending.Complete(true);
    }

    // CallBlocking's check of ID_RPC_REMOTE_ERROR (RPC4Plugin.cpp:624-642), reading the name as it was written.
    private void OnRemoteError(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[1] != FunctionNotRegistered)
            return;
        var nameBytes = data[2..];
        var terminator = nameBytes.IndexOf((byte)0);
        if (terminator >= 0)
            nameBytes = nameBytes[..terminator];
        var name = Encoding.UTF8.GetString(nameBytes);
        PendingCall? failed = null;
        lock (_gate)
        {
            if (_pending is { } pending && pending.Name == name)
            {
                failed = pending;
                _pending = null;
            }
        }
        failed?.Complete(false);
    }

    // RPC4Plugin.cpp:844-864: each named function for the message's ID, with the whole message.
    private void RunLocalCallbacks(ReadOnlySpan<byte> data, Rpc4Sender sender)
    {
        List<Rpc4Function>? functions = null;
        lock (_gate)
        {
            if (!_localCallbacks.TryGetValue(data[0], out var names))
                return;
            foreach (var name in names)
            {
                if (_functions.TryGetValue(name, out var function))
                    (functions ??= []).Add(function);
            }
        }
        if (functions is null)
            return;
        foreach (var function in functions)
            function(new BitStreamReader(data, data.Length * 8), sender);
    }

    // InvokeSignal (RPC4Plugin.cpp:705-740): each slot reads the parameters from the start.
    private void InvokeSignal(string sharedIdentifier, BitStreamReader parameters, Rpc4Sender sender)
    {
        Slot[] slots;
        lock (_gate)
        {
            if (!_slots.TryGetValue(sharedIdentifier, out var registered))
                return;
            slots = [.. registered];
        }
        _interruptSignal = false;
        foreach (var slot in slots)
        {
            slot.Function(parameters, sender);
            if (_interruptSignal)
                break;
        }
    }

    // RPC4Plugin.cpp:785, 819: replies go to packet->systemAddress.
    private void SendToSender(OutgoingMessage message, Rpc4Sender sender, Priority priority)
    {
        if (_host is { } host && sender.Address.IsAssigned)
            host.Send(message, sender.Address, Reliability.ReliableOrdered, priority, 0);
    }

    private void SendToSender(BitStream message, Rpc4Sender sender, Priority priority) => SendToSender((OutgoingMessage)message, sender, priority);

    private void SendToSender(byte[] message, Rpc4Sender sender, Priority priority) => SendToSender((OutgoingMessage)message, sender, priority);

    // SendUnified: RakPeer::Send sends nothing to an undefined target unless broadcasting (RakPeer.cpp:1311-1315).
    private static void SendUnified(IPluginHost host, BitStream message, Priority priority, Reliability reliability, byte orderingChannel, AddressOrGuid target, bool broadcast)
    {
        if (broadcast)
            host.Broadcast(message, reliability, priority, orderingChannel, except: target);
        else if (!target.IsUndefined)
            host.Send(message, target, reliability, priority, orderingChannel);
    }

    // ID_RPC_PLUGIN, the sub-ID, the compressed name, for a call the blocking bit, then the parameters aligned.
    private static BitStream BuildMessage(byte subId, string name, bool? isBlocking, BitStream? parameters)
    {
        var message = new BitStream();
        var hint = 64 + (name.Length * 8);
        while (true)
        {
            var writer = message.BeginWrite(hint);
            try
            {
                writer.WriteByte((byte)MessageId.RpcPlugin);
                writer.WriteByte(subId);
                writer.WriteCompressedRakString(name);
                if (isBlocking is { } blocking)
                    writer.WriteBit(blocking);
            }
            catch (InvalidOperationException) when (hint < 1 << 24)
            {
                // The name compressed to more than the hint: start again with more room.
                message.Reset();
                hint *= 4;
                continue;
            }
            message.EndWrite(writer);
            break;
        }
        if (parameters is not null)
        {
            message.AlignToByte();
            var reader = parameters.CreateReader();
            message.Write(ref reader);
        }
        return message;
    }

    // ID_RPC_REMOTE_ERROR, RPC_ERROR_FUNCTION_NOT_REGISTERED, the name and a terminating zero.
    private static byte[] RemoteError(ReadOnlySpan<byte> name)
    {
        var error = new byte[name.Length + 3];
        error[0] = (byte)MessageId.RpcRemoteError;
        error[1] = FunctionNotRegistered;
        name.CopyTo(error.AsSpan(2));
        return error;
    }

    // RakString::DeserializeCompressed: an unreadable name is the empty string.
    private static byte[] ReadNameBytes(ref BitStreamReader reader) =>
        reader.TryReadCompressedRakStringBytes(out var name) ? name : [];

    private static string ReadName(ref BitStreamReader reader) => Encoding.UTF8.GetString(ReadNameBytes(ref reader));

    private IPluginHost RequireHost() => _host ?? throw new InvalidOperationException("The RPC4 plugin is not attached to a peer.");

    private bool TryTakePending(PendingCall pending)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending))
                return false;
            _pending = null;
            return true;
        }
    }

    private void FailPending()
    {
        PendingCall? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }
        pending?.Complete(false);
    }

    private sealed record Slot(uint RegistrationCount, int CallPriority, Rpc4Function Function);

    private sealed class PendingCall(string name, AddressOrGuid target, BitStream returnData)
    {
        public string Name { get; } = name;

        public AddressOrGuid Target { get; } = target;

        public BitStream ReturnData { get; } = returnData;

        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Cancellation { get; set; }

        public void Complete(bool returned)
        {
            Cancellation.Dispose();
            Completion.TrySetResult(returned);
        }
    }

    // RakString's operator<, strcmp on the UTF-8 bytes: code point order.
    private sealed class Utf8Order : IComparer<string>
    {
        public static readonly Utf8Order Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            var left = x.EnumerateRunes();
            var right = y.EnumerateRunes();
            while (true)
            {
                var hasLeft = left.MoveNext();
                var hasRight = right.MoveNext();
                if (!hasLeft || !hasRight)
                    return hasLeft.CompareTo(hasRight);
                var comparison = left.Current.Value.CompareTo(right.Current.Value);
                if (comparison != 0)
                    return comparison;
            }
        }
    }
}
