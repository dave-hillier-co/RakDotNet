using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Compatibility;

/// <summary>RakNet's StartupResult (RakNetTypes.h:41-55), in the same order.</summary>
public enum StartupResult
{
    RakNetStarted,
    RakNetAlreadyStarted,
    InvalidSocketDescriptors,
    InvalidMaxConnections,
    SocketFamilyNotSupported,
    SocketPortAlreadyInUse,
    SocketFailedToBind,
    SocketFailedTestSend,
    PortCannotBeZero,
    FailedToCreateNetworkThread,
    CouldNotGenerateGuid,
    StartupOtherFailure,
}

/// <summary>RakNet's ConnectionAttemptResult (RakNetTypes.h:58-66), in the same order, with INVALID_PARAMETER.</summary>
public enum ConnectionAttemptResult
{
    ConnectionAttemptStarted,
    InvalidParameter,
    CannotResolveDomainName,
    AlreadyConnectedToEndpoint,
    ConnectionAttemptAlreadyInProgress,
    SecurityInitializationFailed,
}

/// <summary>RakNet's SocketDescriptor: a port and an optional host address to bind.</summary>
public readonly record struct SocketDescriptor(ushort Port, string? HostAddress = null)
{
    public AddressFamily SocketFamily { get; init; } = AddressFamily.InterNetwork;
}

/// <summary>
/// RakNet's Packet, as <see cref="RakPeerInterface.Receive"/> returns it: a detached event. Disposing it is
/// <see cref="RakPeerInterface.DeallocatePacket"/>, which is idempotent and optional because an undisposed packet is
/// collected; use after deallocation throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class Packet : IDisposable
{
    private readonly SystemAddress _systemAddress;
    private readonly RakNetGuid _rakNetGuid;
    private readonly int _length;
    private readonly int _bitSize;
    private readonly bool _wasGeneratedLocally;
    private OwnedPeerEvent _owned;
    private int _released;

    internal Packet(OwnedPeerEvent owned)
    {
        _owned = owned;
        var e = owned.Event;
        _systemAddress = e.Address;
        _rakNetGuid = e.RakNetGuid;
        _length = e.Data.Length;
        _bitSize = e.BitLength;
        _wasGeneratedLocally = e.IsLocal;
    }

    /// <summary>Packet::systemAddress</summary>
    public SystemAddress SystemAddress => Live(_systemAddress);

    /// <summary>Packet::guid</summary>
    public RakNetGuid RakNetGuid => Live(_rakNetGuid);

    /// <summary>Packet::length</summary>
    public int Length => Live(_length);

    /// <summary>Packet::bitSize</summary>
    public int BitSize => Live(_bitSize);

    /// <summary>Packet::data</summary>
    public ReadOnlyMemory<byte> Data => Live(_owned.Data);

    /// <summary>Packet::wasGeneratedLocally</summary>
    public bool WasGeneratedLocally => Live(_wasGeneratedLocally);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _owned.Dispose();
    }

    /// <summary>Hands the owned event over (PushBackPacket); the packet is deallocated without releasing it.</summary>
    internal OwnedPeerEvent Take()
    {
        ObjectDisposedException.ThrowIf(Interlocked.Exchange(ref _released, 1) != 0, this);
        var owned = _owned;
        _owned = default;
        return owned;
    }

    private T Live<T>(T value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        return value;
    }
}
