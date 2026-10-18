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
    private Packet() { }

    /// <summary>Packet::systemAddress</summary>
    public SystemAddress SystemAddress => throw new NotImplementedException();

    /// <summary>Packet::guid</summary>
    public RakNetGuid RakNetGuid => throw new NotImplementedException();

    /// <summary>Packet::length</summary>
    public int Length => throw new NotImplementedException();

    /// <summary>Packet::bitSize</summary>
    public int BitSize => throw new NotImplementedException();

    /// <summary>Packet::data</summary>
    public ReadOnlyMemory<byte> Data => throw new NotImplementedException();

    /// <summary>Packet::wasGeneratedLocally</summary>
    public bool WasGeneratedLocally => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}
