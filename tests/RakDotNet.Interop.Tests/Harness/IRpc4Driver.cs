using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives an RPC4 plugin: the rpc4* commands of docs/interop-protocol.md. Every function the driver
/// registers records its invocations; every command but <see cref="AttachRpc4Async"/> fails until one is attached.
/// </summary>
public interface IRpc4Driver
{
    /// <summary>AttachPlugin of an RPC4.</summary>
    Task AttachRpc4Async(CancellationToken cancellationToken = default);

    /// <summary>RegisterFunction: the registration's handler index, or null when the name is taken.</summary>
    Task<int?> RegisterRpc4FunctionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>RegisterSlot; with <paramref name="interrupt"/> the slot calls InterruptSignal.</summary>
    Task<int> RegisterRpc4SlotAsync(string name, int priority, bool interrupt = false, CancellationToken cancellationToken = default);

    /// <summary>RegisterBlockingFunction of a function that returns <paramref name="returnData"/>.</summary>
    Task<int?> RegisterRpc4BlockingAsync(string name, Rpc4Bits? returnData, CancellationToken cancellationToken = default);

    /// <summary>RegisterLocalCallback</summary>
    Task RegisterRpc4LocalCallbackAsync(string name, byte messageId, CancellationToken cancellationToken = default);

    /// <summary>UnregisterFunction, UnregisterBlockingFunction, UnregisterSlot or UnregisterLocalCallback.</summary>
    Task<bool> UnregisterRpc4Async(Rpc4Registration kind, string name, byte messageId = 0, CancellationToken cancellationToken = default);

    /// <summary>Call</summary>
    Task Rpc4CallAsync(string name, Rpc4Bits? parameters, Rpc4Send send, CancellationToken cancellationToken = default);

    /// <summary>Signal</summary>
    Task Rpc4SignalAsync(string name, Rpc4Bits? parameters, Rpc4Send send, bool invokeLocal, CancellationToken cancellationToken = default);

    /// <summary>CallLoopback</summary>
    Task Rpc4CallLoopbackAsync(string name, Rpc4Bits? parameters, CancellationToken cancellationToken = default);

    /// <summary>CallBlocking to <paramref name="send"/>'s target: whether it returned, and the bits returned.</summary>
    Task<(bool Returned, Rpc4Bits ReturnData)> Rpc4CallBlockingAsync(string name, Rpc4Bits? parameters, Rpc4Send send, CancellationToken cancellationToken = default);

    /// <summary>What every registered function was called with, in order.</summary>
    Task<IReadOnlyList<Rpc4Invocation>> GetRpc4InvocationsAsync(CancellationToken cancellationToken = default);
}

public enum Rpc4Registration
{
    Function,
    Blocking,
    Slot,
    LocalCallback,
}

/// <summary>A bit string, most significant bit of each byte first, as BitStream holds it.</summary>
public sealed record Rpc4Bits(byte[] Data, int Bits)
{
    public static Rpc4Bits From(BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new Rpc4Bits(stream.Written.ToArray(), stream.BitsWritten);
    }

    public BitStreamReader CreateReader() => new(Data, Bits);

    public bool Equals(Rpc4Bits? other) => other is not null && Bits == other.Bits && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => HashCode.Combine(Bits, Data.Length);

    public override string ToString() => $"{Convert.ToHexStringLower(Data)}/{Bits}";
}

/// <summary>The send arguments; a null target is UNASSIGNED_RAKNET_GUID, which a broadcast excludes nobody with.</summary>
public sealed record Rpc4Send(
    ulong? Target,
    bool Broadcast = false,
    Reliability Reliability = Reliability.ReliableOrdered,
    Priority Priority = Priority.High,
    byte Channel = 0);

/// <param name="Handler">The registration that ran.</param>
/// <param name="Address">The packet's system address, empty when unassigned.</param>
/// <param name="Local">The packet's wasGeneratedLocally.</param>
/// <param name="Parameters">The unread bits of the parameter stream when the function ran.</param>
public sealed record Rpc4Invocation(int Handler, string Name, ulong PeerGuid, string Address, bool Local, Rpc4Bits Parameters);
