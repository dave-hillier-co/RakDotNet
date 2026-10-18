using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>The answer to <see cref="RakPeer.PingAsync"/>: the fields of an ID_UNCONNECTED_PONG.</summary>
public readonly record struct PongResult(RakNetGuid RakNetGuid, SystemAddress Address, TimeSpan RoundTrip, ReadOnlyMemory<byte> Response);
