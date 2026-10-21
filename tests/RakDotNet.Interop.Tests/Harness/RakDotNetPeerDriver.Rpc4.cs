using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// The rpc4* commands on an <see cref="Rpc4"/>, with interop-peer's results and errors. The registered functions record
/// their invocations from the pump's dispatch step, so the record is locked.
/// </summary>
public sealed partial class RakDotNetPeerDriver : IRpc4Driver
{
    private static readonly TimeSpan BlockingCallTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _rpc4Gate = new();
    private readonly List<string> _rpc4Handlers = [];
    private readonly List<Rpc4Invocation> _rpc4Invocations = [];
    private Rpc4? _rpc4;

    public async Task AttachRpc4Async(CancellationToken cancellationToken = default)
    {
        var peer = Started("rpc4Attach");
        if (_rpc4 is not null)
            return;
        _rpc4 = new Rpc4();
        await AttachAsync(peer, _rpc4, cancellationToken).ConfigureAwait(false);
    }

    public Task<int?> RegisterRpc4FunctionAsync(string name, CancellationToken cancellationToken = default)
    {
        var rpc = RequireRpc4();
        var handler = NewRpc4Handler(name);
        if (rpc.RegisterFunction(name, Recorder(handler, interrupt: false)))
            return Task.FromResult<int?>(handler);
        RemoveLastRpc4Handler();
        return Task.FromResult<int?>(null);
    }

    public Task<int> RegisterRpc4SlotAsync(string name, int priority, bool interrupt = false, CancellationToken cancellationToken = default)
    {
        var rpc = RequireRpc4();
        var handler = NewRpc4Handler(name);
        rpc.RegisterSlot(name, Recorder(handler, interrupt), priority);
        return Task.FromResult(handler);
    }

    public Task<int?> RegisterRpc4BlockingAsync(string name, Rpc4Bits? returnData, CancellationToken cancellationToken = default)
    {
        var rpc = RequireRpc4();
        var handler = NewRpc4Handler(name);
        var record = Recorder(handler, interrupt: false);
        var registered = rpc.RegisterBlockingFunction(name, (parameters, returned, sender) =>
        {
            record(parameters, sender);
            if (returnData is { Bits: > 0 })
                WriteBits(returned, returnData);
        });
        if (registered)
            return Task.FromResult<int?>(handler);
        RemoveLastRpc4Handler();
        return Task.FromResult<int?>(null);
    }

    public Task RegisterRpc4LocalCallbackAsync(string name, byte messageId, CancellationToken cancellationToken = default)
    {
        RequireRpc4().RegisterLocalCallback(name, messageId);
        return Task.CompletedTask;
    }

    public Task<bool> UnregisterRpc4Async(Rpc4Registration kind, string name, byte messageId = 0, CancellationToken cancellationToken = default)
    {
        var rpc = RequireRpc4();
        return Task.FromResult(kind switch
        {
            Rpc4Registration.Function => rpc.UnregisterFunction(name),
            Rpc4Registration.Blocking => rpc.UnregisterBlockingFunction(name),
            Rpc4Registration.Slot => rpc.UnregisterSlot(name),
            _ => rpc.UnregisterLocalCallback(name, messageId),
        });
    }

    public Task Rpc4CallAsync(string name, Rpc4Bits? parameters, Rpc4Send send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        var rpc = RequireRpc4();
        using var stream = ToBitStream(parameters);
        rpc.Call(name, stream, send.Priority, send.Reliability, send.Channel, Rpc4Target("rpc4Call", send), send.Broadcast);
        return Task.CompletedTask;
    }

    public Task Rpc4SignalAsync(string name, Rpc4Bits? parameters, Rpc4Send send, bool invokeLocal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        var rpc = RequireRpc4();
        using var stream = ToBitStream(parameters);
        rpc.Signal(name, stream, send.Priority, send.Reliability, send.Channel, Rpc4Target("rpc4Signal", send), send.Broadcast, invokeLocal);
        return Task.CompletedTask;
    }

    public Task Rpc4CallLoopbackAsync(string name, Rpc4Bits? parameters, CancellationToken cancellationToken = default)
    {
        var rpc = RequireRpc4();
        using var stream = ToBitStream(parameters);
        rpc.CallLoopback(name, stream);
        return Task.CompletedTask;
    }

    public async Task<(bool Returned, Rpc4Bits ReturnData)> Rpc4CallBlockingAsync(string name, Rpc4Bits? parameters, Rpc4Send send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        var rpc = RequireRpc4();
        if (send.Target is not { } target)
            throw new PeerCommandException("rpc4CallBlocking", "missing field 'target'");
        using var stream = ToBitStream(parameters);
        using var returnData = new BitStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BlockingCallTimeout);
        var returned = await rpc.CallBlockingAsync(name, stream, send.Priority, send.Reliability, send.Channel, new RakNetGuid(target), returnData, timeout.Token).ConfigureAwait(false);
        return (returned, Rpc4Bits.From(returnData));
    }

    public Task<IReadOnlyList<Rpc4Invocation>> GetRpc4InvocationsAsync(CancellationToken cancellationToken = default)
    {
        RequireRpc4();
        lock (_rpc4Gate)
            return Task.FromResult<IReadOnlyList<Rpc4Invocation>>([.. _rpc4Invocations]);
    }

    private int NewRpc4Handler(string name)
    {
        lock (_rpc4Gate)
        {
            _rpc4Handlers.Add(name);
            return _rpc4Handlers.Count - 1;
        }
    }

    private void RemoveLastRpc4Handler()
    {
        lock (_rpc4Gate)
            _rpc4Handlers.RemoveAt(_rpc4Handlers.Count - 1);
    }

    // interop-peer's RecordInvocation: the unread parameter bits, the packet's GUID, address and wasGeneratedLocally.
    private Rpc4Function Recorder(int handler, bool interrupt) => (parameters, sender) =>
    {
        using var copy = new BitStream();
        copy.Write(ref parameters);
        lock (_rpc4Gate)
        {
            _rpc4Invocations.Add(new Rpc4Invocation(
                handler,
                _rpc4Handlers[handler],
                sender.RakNetGuid.Value,
                sender.Address.IsAssigned ? sender.Address.ToString(writePort: true, ':') : "",
                sender.IsLocal,
                Rpc4Bits.From(copy)));
        }
        if (interrupt)
            _rpc4?.InterruptSignal();
    };

    private static BitStream? ToBitStream(Rpc4Bits? bits)
    {
        if (bits is null)
            return null;
        var stream = new BitStream();
        WriteBits(stream, bits);
        return stream;
    }

    // BitStream::WriteBits(data, bits, false): a partial last byte gives its high bits.
    private static void WriteBits(BitStream stream, Rpc4Bits bits)
    {
        var writer = stream.BeginWrite(bits.Data.Length + 1);
        writer.WriteBits(bits.Data, bits.Bits, rightAlignedBits: false);
        stream.EndWrite(writer);
    }

    private static AddressOrGuid Rpc4Target(string command, Rpc4Send send)
    {
        if (send.Target is { } target)
            return new RakNetGuid(target);
        return send.Broadcast ? default : throw new PeerCommandException(command, "missing field 'target'");
    }

    private Rpc4 RequireRpc4() =>
        _rpc4 ?? throw new PeerCommandException("rpc4", "no RPC4 is attached; send rpc4Attach first");
}
