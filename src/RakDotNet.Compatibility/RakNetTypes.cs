using System.Net.Sockets;
using System.Runtime.InteropServices;
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
/// RakNet's Packet, as <see cref="RakPeerInterface.Receive"/> returns it (a detached event) or
/// <see cref="RakPeerInterface.AllocatePacket"/> creates it. As in RakNet, the data, address and GUID may be changed
/// before <see cref="RakPeerInterface.PushBackPacket"/>. Disposing it is <see cref="RakPeerInterface.DeallocatePacket"/>,
/// which is idempotent and optional because an undisposed packet is collected; use after deallocation throws
/// <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class Packet : IDisposable
{
    private readonly Memory<byte> _data;
    private readonly int _bitSize;
    private readonly bool _wasGeneratedLocally;
    private readonly SystemAddress _receivedAddress;
    private readonly RakNetGuid _receivedGuid;
    private SystemAddress _systemAddress;
    private RakNetGuid _rakNetGuid;
    private OwnedPeerEvent _owned;
    private int _released;

    internal Packet(OwnedPeerEvent owned)
    {
        _owned = owned;
        var e = owned.Event;
        // The event is detached, so this packet owns its bytes and may write them, as RakNet's packet->data allows.
        _data = MemoryMarshal.AsMemory(owned.Data);
        _systemAddress = _receivedAddress = e.Address;
        _rakNetGuid = _receivedGuid = e.RakNetGuid;
        _bitSize = e.BitLength;
        _wasGeneratedLocally = e.IsLocal;
    }

    /// <summary>AllocPacket (RakPeer.cpp:159-169): zeroed data, bitSize from the length, unassigned GUID.</summary>
    internal Packet(int length)
    {
        _data = new byte[length];
        _bitSize = length * 8;
        _systemAddress = SystemAddress.Unassigned;
        _rakNetGuid = RakNetGuid.Unassigned;
    }

    /// <summary>Packet::systemAddress</summary>
    public SystemAddress SystemAddress
    {
        get => Live(_systemAddress);
        set => _systemAddress = Live(value);
    }

    /// <summary>Packet::guid</summary>
    public RakNetGuid RakNetGuid
    {
        get => Live(_rakNetGuid);
        set => _rakNetGuid = Live(value);
    }

    /// <summary>Packet::length</summary>
    public int Length => Live(_data.Length);

    /// <summary>Packet::bitSize</summary>
    public int BitSize => Live(_bitSize);

    /// <summary>Packet::data, writable as in RakNet.</summary>
    public Memory<byte> Data => Live(_data);

    /// <summary>Packet::wasGeneratedLocally</summary>
    public bool WasGeneratedLocally => Live(_wasGeneratedLocally);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _owned.Dispose();
    }

    /// <summary>
    /// Hands the owned event of a received packet over (PushBackPacket), with <paramref name="edited"/> set when its
    /// address or GUID changed. Otherwise returns false and the caller copies <see cref="Data"/> and then disposes the packet.
    /// </summary>
    internal bool TryTake(out OwnedPeerEvent owned, out bool edited)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        edited = _systemAddress != _receivedAddress || _rakNetGuid != _receivedGuid;
        if (!_owned.IsValid || Interlocked.Exchange(ref _released, 1) != 0)
        {
            owned = default;
            return false;
        }
        owned = _owned;
        _owned = default;
        return true;
    }

    private T Live<T>(T value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        return value;
    }
}
