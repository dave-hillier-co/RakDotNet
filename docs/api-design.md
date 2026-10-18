# RakDotNet public API design

This document defines the public API of RakDotNet: layer 3 of [plan.md](plan.md) (namespace and assembly `RakDotNet`), the layer 0 types it adds to `RakDotNet.Protocol.Wire`, and the layer 4 packages that sit directly on it. It is binding for the implementation, for `RakDotNetPeerDriver` and for every plugin port.

C++ references are to RakNet 4.081 at commit `1a169895`, as `File.cpp:line`. Where this API differs from RakNet's observable behaviour, the difference is stated with its reason.

## 1. Purpose

The API exposes the whole of RakNet's `RakPeerInterface` and `PluginInterface2` in a form that is natural in .NET 10:

- Every RakNet capability is reachable, so every RakNet plugin and utility can be ported on the public API alone.
- The bytes an application or plugin sees for a RakNet packet are exactly the bytes RakNet's `Receive()` would return, so ported code and the interop harness behave identically against native and C# peers.
- The hot path allocates nothing per message once connections are established and pools are warm.
- Everything is deterministic under test: time comes from `TimeProvider`, I/O from a replaceable datagram transport, and the I/O cycle can be driven by hand.

## 2. Design principles

1. **One ordered event stream per peer.** Connection events, messages, receipts and offline replies arrive in one stream, in the order RakNet would return them from `Receive()`.
2. **RakNet-visible bytes are never invented.** An event that RakNet would not produce is a distinct kind with `IsRakNetPacket == false` and no RakNet bytes. The compatibility facade and the interop driver drop these events, so the stream they report is the stream native RakNet produces.
3. **Borrowed by default, owned by request.** A received event is valid until the next read. The type system stops it escaping (it is a `ref struct`), and a generation check catches the rest. Keeping an event is an explicit `Detach()`.
4. **Commands are thread-safe and never block.** `Send`, `Close`, `Connect` and every setter enqueue a command for the I/O cycle. Queries read snapshots.
5. **Programming errors throw; runtime outcomes are values.** Bad arguments and misuse throw. Network, connection and send outcomes are returned as typed results or events. Malformed input from the network never throws.
6. **Plugins run where RakNet runs them.** Plugin hooks run on the event consumer, inside the read, in RakNet's order. Reliability-layer observers run in the I/O cycle.
7. **RakNet names where they carry meaning.** `RakNetGuid`, `SystemAddress`, `AddressOrGuid`, `ConnectionState`, `RakNetStatistics`, the `ConnectionAttemptResult` and failure names, and the plugin hook names stay recognisable, so porting is mostly mechanical.
8. **Escape hatches are grouped.** Out-of-band sends, raw datagrams, TTL, datagram filtering, address migration, the network simulator and observers live under `peer.LowLevel`, which keeps the main surface small.
9. **No reflection.** Trimming and NativeAOT are supported with analyzers as errors.

## 3. Assemblies and namespaces

| Assembly | Namespace | Contents |
|---|---|---|
| `RakDotNet.Protocol` | `RakDotNet.Protocol.Wire` | Layer 0, public. Existing: `Reliability`, `Priority`, `MessageId`, `UInt24`, `BitStreamReader`, `BitStreamWriter`, the message codecs, `RakNetConstants`. Added by this design: `RakNetGuid`, `SystemAddress`, `BitStream`, `TimestampHeader`, `OutOfBandHeader`, the `BitStreamWriter(Span<byte>, int bitPosition)` constructor, the RakNet application encodings (section 15). |
| `RakDotNet.Protocol` | `.Session`, `.Peer` | Layers 1 and 2, `internal` (decision 18.2). |
| `RakDotNet` | `RakDotNet` | Layer 3: everything in sections 4 to 14 except security. |
| `RakDotNet` | `RakDotNet.Security` | Key and connect-security types (section 13). |
| `RakDotNet.Testing` | `RakDotNet.Testing` | `InMemoryNetwork`, a datagram transport fake for sociable multi-peer tests (section 16). |
| `RakDotNet.Connections` | `RakDotNet.Connections` | Layer 4: `ConnectionDemultiplexer` (section 6.4). |
| `RakDotNet.Compatibility` | `RakDotNet.Compatibility` | Layer 4: the `RakPeerInterface`-shaped facade (decision 18.1). |

Wire types are reused everywhere and never redeclared, as [conventions.md](conventions.md) requires.

## 4. Identity and addressing

```csharp
namespace RakDotNet.Protocol.Wire;

/// RakNetGUID (RakNetTypes.h). Serialised as a big-endian u64.
public readonly record struct RakNetGuid(ulong Value) : ISpanFormattable, ISpanParsable<RakNetGuid>
{
    public static RakNetGuid Unassigned { get; }          // UNASSIGNED_RAKNET_GUID, RakNetConstants.UnassignedGuid
    public bool IsAssigned { get; }
    public override string ToString();                    // decimal, as RakNetGUID::ToString and interop-protocol.md
}

/// An IPv4 or IPv6 address and port held inline. No allocation; usable as a dictionary key.
public readonly struct SystemAddress : IEquatable<SystemAddress>, ISpanFormattable, ISpanParsable<SystemAddress>
{
    public static SystemAddress Unassigned { get; }       // UNASSIGNED_SYSTEM_ADDRESS
    public SystemAddress(IPAddress address, int port);
    public static SystemAddress FromSocketAddress(SocketAddress address);  // receive path; copies the bytes
    public void CopyTo(SocketAddress destination);                         // send path; no allocation
    public static implicit operator SystemAddress(IPEndPoint endPoint);
    public IPEndPoint ToIPEndPoint();                     // allocates; for BCL interop
    public AddressFamily AddressFamily { get; }
    public ushort Port { get; }
    public bool IsAssigned { get; }
    public bool IsLoopback { get; }
    public bool EqualsIgnoringPort(SystemAddress other);  // SystemAddress::EqualsExcludingPort
    public bool TryWriteAddressBytes(Span<byte> destination, out int bytesWritten);
    public override string ToString();                    // "a.b.c.d:port", as interop-protocol.md
}
```

`BitStreamWriter.WriteAddress(SystemAddress)` and `BitStreamReader.TryReadAddress(out SystemAddress)` sit beside the existing `IPEndPoint` overloads. IPv6 serialisation follows the platform `sockaddr_in6` caveat in [plan.md](plan.md); until per-platform captures exist, an IPv6 binding fails at start with `StartupError.SocketFamilyNotSupported`.

```csharp
namespace RakDotNet;

/// AddressOrGUID. Holds exactly one of a GUID or an address, never both, so a target is never ambiguous.
public readonly struct AddressOrGuid : IEquatable<AddressOrGuid>
{
    public RakNetGuid Guid { get; }             // Unassigned when this holds an address
    public SystemAddress Address { get; }       // Unassigned when this holds a GUID
    public bool IsUndefined { get; }
    public static implicit operator AddressOrGuid(RakNetGuid guid);
    public static implicit operator AddressOrGuid(SystemAddress address);
    public static implicit operator AddressOrGuid(IPEndPoint endPoint);
    public static implicit operator AddressOrGuid(RakConnection connection);   // the connection's Guid
}

public readonly record struct SendReceipt(uint Value)
{
    public static SendReceipt None { get; }     // 0: "assign one" when passed as forceReceipt
}

public enum ConnectionState : byte              // RakNet ConnectionState, same order
{ Pending, Connecting, Connected, Disconnecting, SilentlyDisconnecting, Disconnected, NotConnected }
```

Hostnames are `string` and are resolved with `Dns.GetHostAddressesAsync`. No public member takes a hostname as bytes.

## 5. Main types

### 5.1 `RakPeer`

```csharp
public sealed class RakPeer : IPluginHost, IAsyncDisposable, IDisposable
{
    // ---- Lifecycle
    public static RakPeer Start(RakPeerOptions options);    // binds, starts the loop; throws RakStartupException
    public ValueTask ShutdownAsync(TimeSpan blockDuration, byte orderingChannel = 0,
                                   Priority notificationPriority = Priority.Low,
                                   CancellationToken cancellationToken = default);
    public void Dispose();                                  // ShutdownAsync(TimeSpan.Zero), synchronously; idempotent
    public ValueTask DisposeAsync();                        // ShutdownAsync(options.ShutdownBlockDuration); idempotent
    public bool IsActive { get; }
    public void Update();                                   // PeerThreading.Manual only: one I/O cycle (RunUpdateCycle)

    // ---- The event stream (section 6)
    public PeerEventReader Events { get; }

    // ---- Identity and clock
    public RakNetGuid Guid { get; }                         // GetMyGUID
    public TimeProvider TimeProvider { get; }
    public ulong GetTime();                                 // RakNet::GetTime, ms, from TimeProvider
    public IReadOnlyList<RakSocket> Sockets { get; }        // one per SocketBinding, index = binding index
    public IReadOnlyList<IPAddress> LocalAddresses { get; } // GetNumberOfAddresses / GetLocalIP
    public bool IsLocalAddress(IPAddress address);          // IsLocalIP
    public IReadOnlyList<SystemAddress> InternalAddresses { get; }    // GetInternalID(UNASSIGNED, i)
    public void SetInternalAddress(SystemAddress address, int index = 0); // SetInternalID
    public SystemAddress ExternalAddress { get; }           // GetExternalID(UNASSIGNED): first address a remote reported

    // ---- Connection table
    public int MaxConnections { get; }                      // GetMaximumNumberOfPeers
    public int MaxIncomingConnections { get; set; }         // clamped to MaxConnections at start (RakPeer.cpp:605)
    public int ConnectionCount { get; }                     // NumberOfConnections
    public ConnectionList Connections { get; }              // GetConnectionList / GetSystemList
    public bool TryGetConnection(RakNetGuid guid, [NotNullWhen(true)] out RakConnection? connection);
    public bool TryGetConnection(SystemAddress address, [NotNullWhen(true)] out RakConnection? connection);
    public bool TryGetConnectionByIndex(int systemIndex, [NotNullWhen(true)] out RakConnection? connection);
    public RakNetGuid GetGuidFromAddress(SystemAddress address);   // Unassigned input -> own Guid (RakPeer.cpp:2396)
    public SystemAddress GetAddressFromGuid(RakNetGuid guid);
    public ConnectionState GetConnectionState(AddressOrGuid target);   // includes Pending attempts

    // ---- Connecting (section 8)
    public ConnectAttemptResult Connect(SystemAddress remote, ConnectOptions? options = null);
    public ValueTask<ConnectResult> ConnectAsync(SystemAddress remote, ConnectOptions? options = null,
                                                 CancellationToken cancellationToken = default);
    public ValueTask<ConnectResult> ConnectAsync(string host, int port, ConnectOptions? options = null,
                                                 CancellationToken cancellationToken = default);
    public void CancelConnectionAttempt(SystemAddress remote);
    public void Close(AddressOrGuid target, bool notify = true, byte orderingChannel = 0,
                      Priority notificationPriority = Priority.Low);    // CloseConnection

    // ---- Sending (section 9)
    public SendResult Send(OutgoingMessage message, AddressOrGuid target, Reliability reliability,
                           Priority priority = Priority.High, byte orderingChannel = 0,
                           SendReceipt forceReceipt = default);
    public SendResult Send<TState>(AddressOrGuid target, Reliability reliability, Priority priority,
                                   byte orderingChannel, int sizeHint, TState state, MessageWriter<TState> write)
        where TState : allows ref struct;
    public SendResult Broadcast(OutgoingMessage message, Reliability reliability,
                                Priority priority = Priority.High, byte orderingChannel = 0,
                                AddressOrGuid except = default, SendReceipt forceReceipt = default);
    public SendResult Broadcast<TState>(Reliability reliability, Priority priority, byte orderingChannel,
                                        int sizeHint, TState state, MessageWriter<TState> write,
                                        AddressOrGuid except = default)
        where TState : allows ref struct;
    public ValueTask<DeliveryOutcome> SendAndTrackAsync(OutgoingMessage message, AddressOrGuid target,
                                                        Reliability reliability, Priority priority = Priority.High,
                                                        byte orderingChannel = 0,
                                                        CancellationToken cancellationToken = default);
    public void SendLoopback(ReadOnlySpan<byte> message);
    public SendReceipt PeekNextReceipt();                   // GetNextSendReceipt
    public SendReceipt ReserveReceipt();                    // IncrementNextSendReceipt

    // ---- Injecting events (PushBackPacket)
    public void Inject(ReadOnlySpan<byte> data, SystemAddress source, RakNetGuid sourceGuid, bool atHead = false); // copies
    public void Inject(OwnedPeerEvent owned, bool atHead = false);    // consumes ownership; no copy

    // ---- Offline
    public bool PingUnconnected(SystemAddress remote, bool onlyReplyOnAcceptingConnections = false, int socketIndex = 0);
    public ValueTask<bool> PingUnconnectedAsync(string host, int port, bool onlyReplyOnAcceptingConnections = false,
                                                int socketIndex = 0, CancellationToken cancellationToken = default);
    public ValueTask<PongResult> PingAsync(SystemAddress remote, TimeSpan timeout,
                                           bool onlyReplyOnAcceptingConnections = false, int socketIndex = 0,
                                           CancellationToken cancellationToken = default);
    public ReadOnlyMemory<byte> OfflinePingResponse { get; set; }     // copied on set; > 400 bytes throws
    public bool AdvertiseSystem(SystemAddress remote, ReadOnlySpan<byte> data = default, int socketIndex = 0);
    public ValueTask<bool> AdvertiseSystemAsync(string host, int port, ReadOnlyMemory<byte> data = default,
                                                int socketIndex = 0, CancellationToken cancellationToken = default);

    // ---- Runtime settings (initial values from RakPeerOptions; take effect on the next I/O cycle)
    public ReadOnlyMemory<byte> IncomingPassword { get; set; }        // copied on set; > 255 bytes throws
    public TimeSpan DefaultTimeout { get; set; }    // SetTimeoutTime(t, UNASSIGNED): default and every live connection (RakPeer.cpp:2526-2538)
    public TimeSpan UnreliableTimeout { get; set; }
    public bool OccasionalPing { get; set; }
    public int SplitMessageProgressInterval { get; set; }
    public bool LimitIpConnectionFrequency { get; set; }
    public bool AllowConnectionResponseIpMigration { get; set; }
    public long PerConnectionOutgoingBandwidthLimit { get; set; }     // bits per second, 0 = unlimited

    // ---- Admission control
    public BanList BanList { get; }
    public AddressPatternList SecurityExceptions { get; }
    public bool IsSecurityEnabled { get; }

    // ---- Statistics (section 12)
    public int CopyStatistics(Span<ConnectionStatistics> destination);   // GetStatisticsList

    // ---- Plugins (section 11)
    public PluginCollection Plugins { get; }

    // ---- Escape hatches
    public RakPeerLowLevel LowLevel { get; }

    // ---- Diagnostics
    public PoolStatistics Pools { get; }
}

/// Immutable snapshot, rebuilt only when a connection is added or removed. Struct enumerator, no allocation.
public readonly struct ConnectionList : IReadOnlyList<RakConnection>
{
    public int Count { get; }
    public RakConnection this[int index] { get; }
    public Enumerator GetEnumerator();
    public struct Enumerator : IEnumerator<RakConnection> { public RakConnection Current { get; } public bool MoveNext(); }
}

public readonly record struct PongResult(RakNetGuid Guid, SystemAddress Address, TimeSpan RoundTrip, ReadOnlyMemory<byte> Response);
```

A `RakPeer` starts once. `Start` either returns an active peer or throws, so there is no "constructed but not started" state. After shutdown every command throws `ObjectDisposedException` and every query returns its last value. To start again, create a new peer (decision 18.10).

### 5.2 `RakConnection`

There is one `RakConnection` per entry in RakNet's remote system list. It is created when that entry is assigned: for an outgoing attempt, when the offline handshake completes and the attempt moves from the request queue into the connection table; for an incoming one, when the connection request is accepted into the table. Before that, an outgoing attempt is visible only through `peer.GetConnectionState(address) == Pending` and its `ConnectAsync` task.

A `RakConnection` is never reused. A later connection from the same address or GUID gets a new object, so a stale reference cannot address a new peer. After the connection ends the object stays readable with `State == Disconnected`, every property keeps its last value, and `Send` returns `SendStatus.NotConnected`. Object identity is therefore safe as a dictionary key.

```csharp
public sealed class RakConnection
{
    public RakPeer Peer { get; }
    public RakNetGuid Guid { get; }
    public SystemAddress Address { get; }              // changes only through LowLevel.ChangeSystemAddress
    public int SystemIndex { get; }                    // GetIndexFromSystemAddress
    public bool IsIncoming { get; }
    public RakSocket Socket { get; }                   // GetSocket(target)
    public ConnectionState State { get; }
    public bool IsConnected { get; }
    public int Mtu { get; }                            // GetMTUSize(target)
    public TimeSpan Timeout { get; set; }              // SetTimeoutTime / GetTimeoutTime(target)
    public TimeSpan? AveragePing { get; }              // null when RakNet returns -1 (no sample yet)
    public TimeSpan? LastPing { get; }
    public TimeSpan? LowestPing { get; }
    public long ClockDifferential { get; }             // GetClockDifferential, signed ms
    public SystemAddress ExternalAddress { get; }      // GetExternalID(target): our address as the remote sees it
    public IReadOnlyList<SystemAddress> RemoteInternalAddresses { get; }   // GetInternalID(target, i)
    public bool IsSecure { get; }
    public RakPublicKey? RemoteIdentityKey { get; }    // GetClientPublicKeyFromSystemAddress
    public object? UserState { get; set; }             // application state; never read by the library
    public Task Closed { get; }                        // completes when State reaches Disconnected

    public RakNetStatistics GetStatistics();           // GetStatistics(target); value snapshot

    public SendResult Send(OutgoingMessage message, Reliability reliability, Priority priority = Priority.High,
                           byte orderingChannel = 0, SendReceipt forceReceipt = default);
    public SendResult Send<TState>(Reliability reliability, Priority priority, byte orderingChannel,
                                   int sizeHint, TState state, MessageWriter<TState> write)
        where TState : allows ref struct;
    public ValueTask<DeliveryOutcome> SendAndTrackAsync(OutgoingMessage message, Reliability reliability,
                                                        Priority priority = Priority.High, byte orderingChannel = 0,
                                                        CancellationToken cancellationToken = default);
    public void Ping();                                // Ping(SystemAddress), connected ping
    public void Close(bool notify = true, byte orderingChannel = 0, Priority notificationPriority = Priority.Low);
    public ValueTask CloseAsync(bool notify = true, byte orderingChannel = 0,
                                Priority notificationPriority = Priority.Low,
                                CancellationToken cancellationToken = default);   // completes with Closed
}
```

In a cross-connection (both sides connecting at once) RakNet handles the second `ID_CONNECTION_REQUEST` on the existing entry while it is in `REQUESTED_CONNECTION` (`RakPeer.cpp:5981-5988`). Because a `RakConnection` mirrors that entry, the API promises one object per entry and nothing more; the `CrossConnectionTest`-derived interop scenario defines which events each side observes.

### 5.3 `RakSocket`

```csharp
public sealed class RakSocket          // GetSockets / GetSocket; one per binding
{
    public int Index { get; }
    public SystemAddress LocalAddress { get; }                       // GetMyBoundAddress(index)
    public AddressFamily AddressFamily { get; }
    public bool IsOpen { get; }                                      // false after shutdown
    public bool SendTo(ReadOnlySpan<byte> datagram, SystemAddress remote);           // raw, no RakNet framing
    public bool SendTo(ReadOnlySpan<byte> datagram, SystemAddress remote, int ttl);  // SendTTL-style raw send
}
```

The underlying `Socket` is never handed out: its receive side belongs to the peer, and a second reader would steal datagrams.

### 5.4 `RakPeerLowLevel`

```csharp
public sealed class RakPeerLowLevel    // peer.LowLevel: NAT, relay and diagnostic plugins
{
    public bool SendOutOfBand(SystemAddress remote, ReadOnlySpan<byte> payload, int socketIndex = 0);
    public ValueTask<bool> SendOutOfBandAsync(string host, int port, ReadOnlyMemory<byte> payload,
                                              int socketIndex = 0, CancellationToken cancellationToken = default);
    public void WriteOutOfBandHeader(ref BitStreamWriter writer);   // ID_OUT_OF_BAND_INTERNAL, our GUID, magic
    public void WriteOutOfBandHeader(BitStream stream);
    public bool SendTtl(SystemAddress remote, int ttl, int socketIndex = 0);           // SendTTL
    public void ChangeSystemAddress(RakNetGuid guid, SystemAddress address);           // Router2
    public IncomingDatagramFilter? IncomingDatagramFilter { get; set; }                // SetIncomingDatagramEventHandler
    public NetworkSimulatorOptions? NetworkSimulator { get; set; }                     // ApplyNetworkSimulator; null = off
    public bool IsNetworkSimulatorActive { get; }
    public NetworkObserverCollection Observers { get; }                                // observers that are not plugins
}

/// Returns false to drop the datagram before RakNet processes it. Runs in the I/O cycle.
public delegate bool IncomingDatagramFilter(ReadOnlySpan<byte> datagram, SystemAddress from, int socketIndex);

public sealed class NetworkObserverCollection : IReadOnlyCollection<INetworkObserver>
{ public void Attach(INetworkObserver observer); public bool Detach(INetworkObserver observer); }
```

## 6. The event stream

### 6.1 Events

```csharp
public enum PeerEventKind : byte
{
    Message,          // any packet not listed below: user IDs, plugin IDs, ID_REMOTE_*, ID_TIMESTAMP-prefixed, loopback
    Connected,        // ID_CONNECTION_REQUEST_ACCEPTED (IsIncoming false) or ID_NEW_INCOMING_CONNECTION (true)
    ConnectFailed,    // ID_CONNECTION_ATTEMPT_FAILED and the nine rejection IDs; see ConnectFailure
    Disconnected,     // ID_DISCONNECTION_NOTIFICATION or ID_CONNECTION_LOST; see DisconnectReason
    ReceiptAcked,     // ID_SND_RECEIPT_ACKED
    ReceiptLost,      // ID_SND_RECEIPT_LOSS
    ReceiptAbandoned, // not a RakNet packet: the receipt's connection ended first (section 9.3)
    UnconnectedPong,  // ID_UNCONNECTED_PONG
    AdvertiseSystem,  // ID_ADVERTISE_SYSTEM
    OutOfBand,        // ID_OUT_OF_BAND_INTERNAL
    DownloadProgress, // ID_DOWNLOAD_PROGRESS
}

/// A borrowed view of one stream entry: a pooled segment plus the generation it was issued with.
/// Valid until the next read on Events (section 7). A ref struct, so it cannot be stored in a field,
/// boxed, captured or held across await.
public readonly ref struct PeerEvent
{
    public PeerEventKind Kind { get; }
    public bool IsRakNetPacket { get; }           // false only for kinds RakNet never produces (ReceiptAbandoned)
    public MessageId Id { get; }                  // Data[0], or the ID after the ID_TIMESTAMP header
    public RakConnection? Connection { get; }     // null for offline, loopback and some injected events
    public RakNetGuid Guid { get; }               // Packet::guid
    public SystemAddress Address { get; }         // Packet::systemAddress
    public int SocketIndex { get; }
    public ulong ReceivedTime { get; }            // GetTime() when the entry was queued
    public bool IsLocal { get; }                  // Packet::wasGeneratedLocally

    // Payload: exactly Packet::data and Packet::bitSize. Empty when IsRakNetPacket is false.
    public ReadOnlySpan<byte> Data { get; }
    public int BitLength { get; }
    public ReadOnlySpan<byte> Body { get; }       // after the ID, or after ID_TIMESTAMP + time + ID
    public BitStreamReader GetReader();           // over Data, limited to BitLength
    public BitStreamReader GetBodyReader();
    public bool HasTimestamp { get; }
    public ulong Timestamp { get; }               // already shifted into local time (ShiftIncomingTimestamp)

    // Kind-specific; default when Kind does not match
    public bool IsIncoming { get; }               // Connected
    public DisconnectReason DisconnectReason { get; }   // Disconnected
    public bool ClosedLocally { get; }            // Disconnected: the connection ended because this peer called Close
    public ConnectFailure ConnectFailure { get; } // ConnectFailed
    public byte RemoteProtocolVersion { get; }    // ConnectFailed + IncompatibleProtocolVersion
    public SendReceipt Receipt { get; }           // ReceiptAcked, ReceiptLost, ReceiptAbandoned
    public UnconnectedPongView Pong { get; }      // UnconnectedPong
    public DownloadProgressView Progress { get; } // DownloadProgress

    public OwnedPeerEvent Detach();               // take ownership (section 7)
    public byte[] ToArray();                      // unpooled copy of Data
}

public readonly ref struct UnconnectedPongView
{ public uint SentTime { get; } public TimeSpan RoundTrip { get; } public ReadOnlySpan<byte> Response { get; } }

public readonly ref struct DownloadProgressView
{ public uint PartsArrived { get; } public uint PartCount { get; } public uint PartLength { get; } public ReadOnlySpan<byte> FirstPart { get; } }

public enum DisconnectReason : byte    // PI2_LostConnectionReason values RakNet raises (RakPeer.cpp:6417-6422)
{
    DisconnectionNotification,         // ID_DISCONNECTION_NOTIFICATION
    ConnectionLost,                    // ID_CONNECTION_LOST
}

/// An event the caller owns. The handle is (segment, generation); release is a CompareExchange on the
/// generation, so disposing a copy, disposing twice, or disposing after Inject consumed it is a no-op.
public struct OwnedPeerEvent : IDisposable
{
    public bool IsValid { get; }
    public PeerEvent Event { get; }               // a view, valid while owned; throws ObjectDisposedException after
    public ReadOnlyMemory<byte> Data { get; }     // same bytes as Event.Data, usable across await
    public void Dispose();
}
```

`Data` holds exactly the bytes RakNet's `Packet::data` would hold, including RakNet's rewrites:

- `ID_UNCONNECTED_PONG`: the ID, the ping time as a 4-byte big-endian `TimeMS`, then the response; the GUID and magic are removed (`RakPeer.cpp:4682-4704`).
- `ID_ADVERTISE_SYSTEM` and `ID_OUT_OF_BAND_INTERNAL`: the ID then the payload; the GUID and magic are removed (`RakPeer.cpp:4706-4735`).
- `ID_TIMESTAMP`: the 8-byte time is shifted in place by the clock differential (`RakPeer.cpp:1549-1555`).
- Receipt events: the ID then the receipt number as 4 bytes in host (little-endian) order (`ReliabilityLayer.cpp:760`, `ReliabilityLayer.cpp:1871-1872`).
- `Connected`: the original `ID_CONNECTION_REQUEST_ACCEPTED` or `ID_NEW_INCOMING_CONNECTION` message.

`BitLength` is `Packet::bitSize`. For messages from a connected system it is the bit length the reliability layer delivered (`RakPeer.cpp:6046`, `RakPeer.cpp:6231`), which can end mid-byte when the sender used `Send(BitStream*)` with a partial final byte; `ID_CONNECTION_REQUEST_ACCEPTED` is rounded to whole bytes (`RakPeer.cpp:6186`).

`Kind` is a pure function of `Data[0]`, as RakNet applications switch on it, except for `ReceiptAbandoned`. A packet that starts with `ID_TIMESTAMP` is a `Message`.

Internal traffic never reaches the stream: connected pings and pongs, unconnected pings, handshake messages and `ID_DETECT_LOST_CONNECTIONS`. Network observers can see it (section 11.4).

### 6.2 `PeerEventReader`

```csharp
public sealed class PeerEventReader
{
    public bool TryRead(out PeerEvent e);                                   // Receive()
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default);
    public IAsyncEnumerable<OwnedPeerEvent> ReadAllAsync(CancellationToken cancellationToken = default);
    public int Count { get; }                                               // GetReceiveBufferSize
    public Task Completion { get; }   // completes at shutdown; faults with RakTransportException on transport failure
}
```

`PeerEventReader` is purpose-built rather than a `ChannelReader<T>` subclass, because a read has side effects that a `ChannelReader` must not have: it runs the plugin chain and releases the previous borrowed event. It has no `TryPeek`, since peeking would either run plugins or show an event a plugin is about to consume.

- `TryRead` runs one dispatch step (section 11.2) and returns the next event that no plugin consumed, or false.
- `WaitToReadAsync` returns true when an event may be available. It completes synchronously when one is queued, and otherwise awaits one reusable `ManualResetValueTaskSourceCore<bool>`. While plugins are attached it also returns true at least every `RakPeerOptions.PluginUpdateInterval`, so plugin `OnUpdate` keeps running for an idle async consumer; the following `TryRead` may then return false. It returns false after shutdown.
- `ReadAllAsync` yields owned events: each item has been detached and must be disposed. It is for consumers that need `await` while holding an event. It allocates one enumerator per enumeration and rents one pooled handle per item.

Consumption patterns:

```csharp
// Game loop, PeerThreading.Manual
peer.Update();
while (peer.Events.TryRead(out var e)) Handle(e);

// Service, PeerThreading.Background: allocation-free, borrowed events, synchronous handler
while (await peer.Events.WaitToReadAsync(ct))
    while (peer.Events.TryRead(out var e))
        Handle(e);

// Service that awaits per event
await foreach (var owned in peer.Events.ReadAllAsync(ct))
    using (owned) await HandleAsync(owned);

static void Handle(PeerEvent e)
{
    switch (e.Kind)
    {
        case PeerEventKind.Connected: e.Connection!.UserState = new Player(); break;
        case PeerEventKind.Message when e.Id == GameIds.Move:
            var r = e.GetBodyReader();
            if (r.TryReadSingle(out var x) && r.TryReadSingle(out var y)) Move(e.Connection!, x, y);
            break;
        case PeerEventKind.Disconnected: Remove(e.Connection!, e.DisconnectReason); break;
    }
}
```

### 6.3 Ordering guarantees

- Entries are delivered in the order the I/O cycle produced them, which is RakNet's `packetReturnQueue` order. Per-channel ordering and sequencing hold within the stream.
- `Connected` precedes that connection's first message. A connection's last delivered message precedes its `Disconnected`, and nothing for that connection follows it.
- A receipt event follows the send that produced it. `ReceiptAbandoned` events for a connection precede its `Disconnected` when it has one.
- `Inject(..., atHead: true)` places the entry so the next `TryRead` returns it, as `PushBackPacket(packet, true)` (`RakPeer.cpp:2838-2841`).
- Commands from one thread are applied in call order.

### 6.4 `ConnectionDemultiplexer` (layer 4)

For servers structured per connection. It is built on the public API only and becomes the single reader of `peer.Events`.

```csharp
namespace RakDotNet.Connections;

public sealed class ConnectionDemultiplexer : IAsyncDisposable
{
    public ConnectionDemultiplexer(RakPeer peer, DemultiplexerOptions? options = null);
    public ChannelReader<RakConnection> Connections { get; }       // each Connected, incoming and outgoing
    public ChannelReader<OwnedPeerEvent> Offline { get; }          // pongs, advertisements, out-of-band, ConnectFailed
    public ChannelReader<OwnedPeerEvent> EventsOf(RakConnection connection);
        // that connection's messages and receipts, then exactly its terminal event if it has one, then completion
    public Task RunAsync(CancellationToken cancellationToken);     // the pump; plugins run on this task
}

public sealed class DemultiplexerOptions
{
    public int PerConnectionCapacity { get; init; } = 1024;
    public DemultiplexerOverflow Overflow { get; init; } = DemultiplexerOverflow.Wait;
}

public enum DemultiplexerOverflow : byte
{
    Wait,             // the pump waits: one slow handler pauses every connection (head-of-line blocking)
    CloseConnection,  // the full connection is closed silently and its queue completed; others keep flowing
}
```

Events cross threads here, so each one is detached and per-connection consumers dispose them. Order is kept within each connection, not between connections. The default is `Wait` because it never drops or closes anything the application did not ask for; the head-of-line cost is stated on the option so it is a visible choice.

## 7. Ownership and lifetime

1. **Borrowed by default.** An event returned by `TryRead` is valid until the next `TryRead` on that peer, until `Update` in Manual mode, or until shutdown. An event passed to a plugin hook is valid for the duration of the call. Nothing needs freeing.
2. **The compiler enforces the common case.** `PeerEvent`, `UnconnectedPongView`, `DownloadProgressView` and `FrameInfo` are `ref struct`s: they cannot be stored in fields, captured by lambdas, boxed or held across `await`.
3. **Stale access throws.** Every `PeerEvent` member compares its generation with the segment's. After release, access throws `ObjectDisposedException`; reused memory is never read. A `ReadOnlySpan<byte>` already taken from `Data` is not tracked, and must not be used after the next read.
4. **Detach to keep.** `e.Detach()` returns an `OwnedPeerEvent` and removes the segment from automatic release. Detaching twice throws `InvalidOperationException`. The owner calls `Dispose()`, or passes it to `peer.Inject(owned, atHead)`, which consumes it. Release is an `Interlocked.CompareExchange` on the generation, so disposing a copy or disposing twice never frees someone else's reuse of the segment.
5. **Leaks are bounded.** An owned event that is never disposed is collected by the GC. The pool misses once and allocates a replacement, and `peer.Pools.Misses` counts it. No native memory is involved.
6. **Sending never takes ownership.** Every `Send` copies the caller's bytes into a pooled segment, or lets the caller write straight into it (`MessageWriter<TState>`), before returning. The caller's buffer is free when `Send` returns.
7. **Injecting copies unless handed ownership.** `Inject(span, ...)` copies. `Inject(OwnedPeerEvent, ...)` takes the segment without copying.
8. **Settings are copied.** `OfflinePingResponse`, `IncomingPassword`, `ConnectOptions.Password` and key material are copied on assignment. Getters return library-owned read-only memory.
9. **Shutdown** releases every borrowed event and discards undelivered entries, as RakNet's `Shutdown` does (`RakPeer.cpp:1176-1181`). Owned events stay valid until disposed.

## 8. Connections

### 8.1 Options and results

```csharp
public sealed record ConnectOptions
{
    public ReadOnlyMemory<byte> Password { get; init; }            // at most 255 bytes; longer throws
    public ConnectSecurity Security { get; init; } = ConnectSecurity.Insecure;   // PublicKey
    public int SocketIndex { get; init; }                          // connectionSocketIndex / ConnectWithSocket
    public int AttemptCount { get; init; } = 12;                   // sendConnectionAttemptCount (RakPeerInterface.h:139)
    public TimeSpan AttemptInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan Timeout { get; init; }                         // 0 = peer.DefaultTimeout
}

/// ConnectionAttemptResult: the synchronous answer to starting an attempt.
public enum ConnectAttemptResult : byte
{
    Started,                              // CONNECTION_ATTEMPT_STARTED
    CannotResolveDomainName,              // CANNOT_RESOLVE_DOMAIN_NAME (host overloads only)
    AlreadyConnectedToEndpoint,           // ALREADY_CONNECTED_TO_ENDPOINT
    ConnectionAttemptAlreadyInProgress,   // CONNECTION_ATTEMPT_ALREADY_IN_PROGRESS
    SecurityInitializationFailed,         // SECURITY_INITIALIZATION_FAILED
}
// INVALID_PARAMETER: a bad argument throws ArgumentException; a stopped peer throws ObjectDisposedException.

/// PI2_FailedConnectionAttemptReason: why a started attempt failed (RakPeer.cpp:6429-6458).
public enum ConnectFailure : byte
{
    ConnectionAttemptFailed,          // ID_CONNECTION_ATTEMPT_FAILED
    AlreadyConnected,                 // ID_ALREADY_CONNECTED
    NoFreeIncomingConnections,        // ID_NO_FREE_INCOMING_CONNECTIONS
    ConnectionBanned,                 // ID_CONNECTION_BANNED
    InvalidPassword,                  // ID_INVALID_PASSWORD
    IncompatibleProtocolVersion,      // ID_INCOMPATIBLE_PROTOCOL_VERSION
    IpRecentlyConnected,              // ID_IP_RECENTLY_CONNECTED
    RemoteSystemRequiresPublicKey,    // ID_REMOTE_SYSTEM_REQUIRES_PUBLIC_KEY
    OurSystemRequiresSecurity,        // ID_OUR_SYSTEM_REQUIRES_SECURITY
    PublicKeyMismatch,                // ID_PUBLIC_KEY_MISMATCH
}

/// The outcome of ConnectAsync. Three separate facts, never merged into one enum.
public readonly struct ConnectResult
{
    public ConnectAttemptResult AttemptResult { get; }   // Started unless the attempt was rejected synchronously
    public ConnectFailure? Failure { get; }              // set when a started attempt failed
    public RakConnection? Connection { get; }            // set when connected
    public byte RemoteProtocolVersion { get; }           // Failure == IncompatibleProtocolVersion only
    public bool IsConnected { get; }
    public RakConnection EnsureConnected();              // throws RakConnectException carrying this result
}
```

### 8.2 Lifecycle

```
Connect ─► Pending ─► Connecting ─┬─► Connected ──┬─ Close(notify: true) ─► Disconnecting ─► Disconnected
                                  │               ├─ Close(notify: false) ─────────────────► Disconnected
incoming request ─────────────────┤               ├─ remote notification ──────────────────► Disconnected
                                  │               └─ timeout or dead link ─────────────────► Disconnected
                                  └─► ConnectFailed ─► NotConnected
```

- **`Connect`** checks the published connection table and the attempt registry, then queues the attempt and returns `Started` or the synchronous rejection. The outcome later arrives on the stream as `Connected` or `ConnectFailed`. `Started` does not rule out an immediate `ConnectFailed(ConnectionAttemptFailed)`.
- **`ConnectAsync`** resolves a host name asynchronously (RakNet resolves on the caller, blocking), then behaves as `Connect`. It completes when the I/O cycle enqueues the `Connected` or `ConnectFailed` event, with the same outcome; the event still appears on the stream at its position. A synchronous rejection completes it immediately with `AttemptResult` set. Cancelling the token throws `OperationCanceledException` and stops the attempt: while it is still in the request queue by `CancelConnectionAttempt`, and once it has an entry in the connection table by a silent close, which for a system that is not yet connected produces no event.
- **`CancelConnectionAttempt`** removes a queued attempt and produces no stream event, as in RakNet (`RakPeer.cpp:1686-1710`). It does nothing once the attempt has an entry in the connection table.
- **`Close(notify: true)`** on a connected system flags it for disconnection (`Disconnecting`). Once its outgoing data has drained, the I/O cycle enqueues `ID_DISCONNECTION_NOTIFICATION` (`RakPeer.cpp:5873-5903`), delivered as `Disconnected(DisconnectionNotification)` with `ClosedLocally == true`.
- **`Close(notify: false)`** on a connected system enqueues `ID_CONNECTION_LOST` immediately with `IsLocal == true` (`RakPeer.cpp:1667-1678`), delivered as `Disconnected(ConnectionLost)` with `ClosedLocally == true`. A silent close of a system that is not yet connected produces no event, as in RakNet.
- **Remote close**: the remote's `ID_DISCONNECTION_NOTIFICATION` moves the entry to wait for its ACK (`RakPeer.cpp:6095-6099`), after which `ID_DISCONNECTION_NOTIFICATION` is enqueued (`RakPeer.cpp:5903`): `Disconnected(DisconnectionNotification)`.
- **Timeout or dead link**: `ID_CONNECTION_LOST` (`RakPeer.cpp:5900-5901`): `Disconnected(ConnectionLost)`.
- **Failed attempt**: a pending entry that times out yields `ID_CONNECTION_ATTEMPT_FAILED` (`RakPeer.cpp:5898-5899`); rejections yield their IDs.

`ClosedLocally` is information the C# peer has and RakNet does not expose; it changes neither `Data` nor the plugin callbacks.

### 8.3 Shutdown

`ShutdownAsync(blockDuration, ...)` follows `RakPeer::Shutdown` (`RakPeer.cpp:1041-1181`):

1. From the call on, the stream delivers nothing more; a concurrent `TryRead` finishes first, and later reads return false.
2. If `blockDuration > 0`, every active connection is notified and the peer waits, on its `TimeProvider`, until none is active or the duration elapses. In Manual mode `ShutdownAsync` runs the I/O cycle itself while it waits.
3. `OnShutdown` runs on every plugin, on the dispatch step (section 10).
4. The sockets close, undelivered entries are discarded, and `Events.Completion` completes.

Pending operations end: `ConnectAsync`, `PingAsync` and the `*Async` host overloads throw `OperationCanceledException`; `SendAndTrackAsync` completes with `DeliveryStatus.PeerShutdown`; `Closed` tasks complete. No `Disconnected` or other event is added to the stream for connections ended by shutdown, because RakNet clears its queue (`RakPeer.cpp:1177-1181`). For an untracked receipt still outstanding at shutdown, the completion of `Events.Completion` is its completion.

## 9. Sending

### 9.1 Members

```csharp
/// What Send accepts. One parameter type covers Send(char*), Send(BitStream*) and SendList.
public readonly ref struct OutgoingMessage
{
    public OutgoingMessage(ReadOnlySpan<byte> data);
    public OutgoingMessage(ReadOnlySpan<byte> data, int bitLength);     // partial final byte, as Send(BitStream*)
    public OutgoingMessage(in ReadOnlySequence<byte> parts);             // SendList: concatenated without a copy
    public static implicit operator OutgoingMessage(ReadOnlySpan<byte> data);
    public static implicit operator OutgoingMessage(byte[] data);
    public static implicit operator OutgoingMessage(BitStreamWriter writer);  // Written, BitsWritten
    public static implicit operator OutgoingMessage(BitStream stream);        // Written, BitsWritten
    public int BitLength { get; }
}

/// Writes straight into the pooled outgoing segment. The writer's capacity is at least sizeHint bytes;
/// writing beyond it throws. Use a static lambda for zero allocations.
public delegate void MessageWriter<TState>(ref BitStreamWriter writer, TState state) where TState : allows ref struct;

public enum SendStatus : byte { Queued, NotActive, NotConnected }

public readonly record struct SendResult(SendStatus Status, SendReceipt Receipt)
{ public bool IsQueued => Status == SendStatus.Queued; }

public enum DeliveryStatus : byte
{
    Acked,             // ID_SND_RECEIPT_ACKED
    Lost,              // ID_SND_RECEIPT_LOSS (unreliable receipts only)
    Abandoned,         // the connection ended first
    NotSent,           // Send returned NotActive or NotConnected; see SendStatus
    PeerShutdown,
}
public readonly record struct DeliveryOutcome(DeliveryStatus Status, SendReceipt Receipt, SendStatus SendStatus);
```

Reliability is a required parameter on every send. Priority defaults to `High` and the ordering channel to 0. There is no options struct whose `default` would silently mean `Unreliable` at `Immediate` priority.

### 9.2 Semantics

- RakNet's model is unchanged: 8 reliabilities, 4 priorities, 32 ordering channels, sequenced messages sharing their channel's ordering index. Ack-receipt reliabilities go on the wire as their base types (`Reliability.ToWireReliability()`).
- **Programming errors throw `ArgumentException`**: an empty message, `orderingChannel >= 32`, an undefined reliability or priority, an undefined target. RakNet asserts in these cases (`RakPeer.cpp:1311-1315`).
- **Receipt numbers follow RakNet.** A send to an active peer takes a receipt number from the peer-wide counter, or uses `forceReceipt` when it is not `None` (`RakPeer.cpp:1326-1330`). This happens even when the target turns out not to be connected, because RakNet assigns the number before the update thread finds the target (`RakPeer.cpp:1349`); the result is then `NotConnected` with that receipt, so the receipt sequence matches native for the same calls. A send to a stopped peer returns `NotActive` with `SendReceipt.None` (`RakPeer.cpp:1320-1321`).
- **Only `*WithAckReceipt` reliabilities produce receipt events.** Reliable receipts are acknowledged with `ReceiptAcked`. Unreliable receipts produce `ReceiptAcked`, or `ReceiptLost` once `UnreliableTimeout` passes without an ACK (`ReliabilityLayer.cpp:1860-1876`).
- **Broadcast** is RakNet's `broadcast = true`: every connected system except `except`, sharing one receipt, with one receipt event per recipient.
- **Loopback.** A non-broadcast send whose target is this peer becomes `SendLoopback`, followed by an `ID_SND_RECEIPT_ACKED` built as `RakPeer.cpp:1332-1346` builds it when the reliability carries a receipt.
- **`Priority.Immediate`** wakes the I/O loop at once in Background mode, and goes out first in the next `Update` in Manual mode. No socket I/O happens on the caller's thread inside `Send`.
- **`SendLoopback`** queues the bytes as a local packet through the injection path, as RakNet's `SendLoopback` calls `PushBackPacket` (`RakPeer.cpp:1354-1364`).

### 9.3 Every receipt completes

The hardening rule "every send's receipt or task completes or faults" holds for both forms of receipt:

- **Untracked receipts** (an explicit `*WithAckReceipt` reliability on `Send` or `Broadcast`) end with `ReceiptAcked`, `ReceiptLost` or `ReceiptAbandoned` on the stream. `ReceiptAbandoned` is emitted when the receipt's connection ends, or when the I/O cycle cannot find the target, with the receipt still outstanding. RakNet emits nothing in that case, so `ReceiptAbandoned` has `IsRakNetPacket == false`, empty `Data`, and is not dispatched to plugins (decision 18.4). At shutdown, `Events.Completion` ends every outstanding receipt.
- **Tracked receipts** (`SendAndTrackAsync`) complete their `ValueTask<DeliveryOutcome>`. The method accepts a base or `*WithAckReceipt` reliability that has a receipt flavour (`Unreliable`, `Reliable`, `ReliableOrdered`), sends the receipt flavour, and throws `ArgumentException` for the sequenced reliabilities, which RakNet gives no receipt flavour. Tracked receipts are delivered to the awaiter and not to the stream. The task is backed by a pooled `IValueTaskSource<DeliveryOutcome>` returned to the pool in `GetResult`.

## 10. Threading

| What | Runs on | Rule |
|---|---|---|
| Layers 1 and 2 (sessions, connection table, ban list, timers) | the I/O cycle | owned by the cycle, lock-free |
| The I/O cycle | Background: one dedicated loop thread per peer. Manual: whichever thread calls `Update` | one cycle at a time |
| `Send*`, `Broadcast`, `Close`, `Connect`, `ConnectAsync`, `CancelConnectionAttempt`, `Ping*`, `AdvertiseSystem*`, `SendLoopback`, `Inject`, `ReserveReceipt`, every setter, `BanList`, `SecurityExceptions`, `Plugins.Attach/Detach`, everything on `LowLevel` | any thread | enqueue a command on a lock-free MPSC ring; never block |
| Property reads, `Connections`, `TryGetConnection*`, `GetConnectionState`, `GetStatistics`, `CopyStatistics` | any thread | read snapshots published by the cycle (volatile fields, a sequence lock per statistics block, copy-on-write `ConnectionList`); may be one cycle old |
| `Events.TryRead`, `Update`, `ShutdownAsync`'s plugin step | **the dispatch step**: one caller at a time, not thread-affine | an interlocked guard throws `InvalidOperationException` on concurrent use |
| `IRakPlugin` hooks, all of them | the dispatch step | plugin state touched only from hooks needs no locks |
| `INetworkObserver` hooks, `IncomingDatagramFilter` | the I/O cycle | must not block; may call the thread-safe commands |
| Continuations of `ConnectAsync`, `CloseAsync`, `PingAsync`, `SendAndTrackAsync`, `Closed`, `WaitToReadAsync` | thread pool (`RunContinuationsAsynchronously`) | user code never runs inside the I/O cycle; no `SynchronizationContext` is captured |

```csharp
public enum PeerThreading : byte
{
    Background,   // one loop thread runs the I/O cycle; the application reads Events
    Manual,       // no threads; the application calls Update() then reads Events (RAKPEER_USER_THREADED + RunUpdateCycle)
}
```

**The I/O cycle**, in both modes: drain commands; receive datagrams without blocking until the transport is empty or the event queue is full; feed the sans-IO layers with `now` from the `TimeProvider`; run timers (resends, ACK/NAK, pings, timeouts, connection retries, ban expiry, split reassembly timeouts, receipt timeouts); flush outgoing datagrams; call `INetworkObserver.OnUpdateCycle`.

**Background mode** waits between cycles on one reusable value-task source that is signalled by the transports when a datagram arrives, by an `ITimer` from `TimeProvider.CreateTimer` at the next deadline, and by commands on an idle-to-busy transition or an `Immediate` send. `FakeTimeProvider.Advance` therefore wakes it. There are no per-socket receive threads and no hand-off between threads on the receive path.

**Manual mode** creates no threads. `Update` runs one cycle on the caller. It is part of the dispatch step, so it cannot run concurrently with `TryRead`. Calling `Update` in Background mode throws `InvalidOperationException`.

**Calls from inside plugin hooks.** The guard records the thread that holds it, and it is held only while a dispatch step runs, which includes every plugin hook but not the application's code between reads. `Plugins.Attach/Detach` and `Inject` called from a plugin hook act immediately, inside the current step. Called from anywhere else, they are applied at the start of the next dispatch step. Either way every plugin hook runs on the dispatch step.

A game that calls `Update` and drains `TryRead` once per frame therefore runs every plugin hook on the game thread, as RakNet runs plugins inside `Receive()`.

## 11. Plugin model

Plugins are written against the public API only. Everything a RakNet plugin reaches through `RakPeerInterface` is a public member of `RakPeer`, `RakConnection`, `RakPeerLowLevel` or `IPluginHost`. Built-in plugins get no special access.

### 11.1 Types

```csharp
public enum PluginResult : byte
{
    Continue,   // RR_CONTINUE_PROCESSING
    Consume,    // RR_STOP_PROCESSING_AND_DEALLOCATE; Detach() first for RR_STOP_PROCESSING
}

public interface IRakPlugin
{
    void OnAttached(IPluginHost host) { }                                     // SetRakPeerInterface + OnAttach
    void OnDetached() { }                                                     // OnDetach
    void OnStartup() { }                                                      // OnRakPeerStartup
    void OnShutdown() { }                                                     // OnRakPeerShutdown
    void OnUpdate() { }                                                       // Update
    void OnConnected(RakConnection connection, bool isIncoming) { }          // OnNewConnection
    void OnDisconnected(RakConnection connection, DisconnectReason reason) { } // OnClosedConnection
    void OnConnectFailed(PeerEvent e, ConnectFailure failure) { }            // OnFailedConnectionAttempt
    PluginResult OnEvent(PeerEvent e) => PluginResult.Continue;              // OnReceive
    void OnInjected(ReadOnlySpan<byte> data, int bitLength, SystemAddress source) { } // OnPushBackPacket
}

/// What a plugin may call, independent of host. RakPeer implements it; so does TcpInterface (milestone 8.6).
/// Replaces the C++ *Unified helpers.
public interface IPluginHost
{
    RakNetGuid Guid { get; }                                         // GetMyGUIDUnified
    TimeProvider TimeProvider { get; }
    ulong GetTime();
    SendResult Send(OutgoingMessage message, AddressOrGuid target, Reliability reliability,
                    Priority priority = Priority.High, byte orderingChannel = 0,
                    SendReceipt forceReceipt = default);              // SendUnified, SendListUnified
    SendResult Broadcast(OutgoingMessage message, Reliability reliability, Priority priority = Priority.High,
                         byte orderingChannel = 0, AddressOrGuid except = default,
                         SendReceipt forceReceipt = default);
    void Inject(ReadOnlySpan<byte> data, SystemAddress source, RakNetGuid sourceGuid, bool atHead = false); // AllocatePacketUnified + PushBackPacketUnified
    void Inject(OwnedPeerEvent owned, bool atHead = false);
    RakPeer? Peer { get; }        // null when hosted by TCP; NAT, Router2 and similar plugins require a RakPeer
}

public sealed class PluginCollection : IReadOnlyCollection<IRakPlugin>
{ public void Attach(IRakPlugin plugin); public bool Detach(IRakPlugin plugin); }
```

`PeerEvent` is a `ref struct`, so a plugin cannot keep the event it is given without `Detach()`.

### 11.2 Dispatch order

RakNet keeps two plugin lists: plugins whose `UsesReliabilityLayer()` is false (`pluginListTS`) and those whose is true (`pluginListNTS`), each in attach order (`RakPeer.cpp:2760-2781`). Here a plugin that also implements `INetworkObserver` is in the second list; every other plugin is in the first. Each dispatch step (one `TryRead` call) does exactly what `RakPeer::Receive` does (`RakPeer.cpp:1528-1600`):

1. Release the previously borrowed event. Apply deferred attaches, detaches and injections.
2. Call `OnUpdate` on every plugin in the first list, then the second. This happens once per `TryRead` call, before anything is popped, including calls that return false.
3. Pop the next entry, or return false. `ReceiptAbandoned` entries skip steps 4 and 5.
4. Connection callbacks (`CallPluginCallbacks`, `RakPeer.cpp:6411-6461`), first list then second, keyed on `Data[0]`: `ID_DISCONNECTION_NOTIFICATION` and `ID_CONNECTION_LOST` call `OnDisconnected`, `ID_NEW_INCOMING_CONNECTION` and `ID_CONNECTION_REQUEST_ACCEPTED` call `OnConnected`, and the ten failure IDs call `OnConnectFailed`.
5. `OnEvent` on the first list in order, then the second. The first `Consume` stops the chain, releases the event (unless it was detached) and continues from step 3.
6. Return the event to the application.

`OnDisconnected` is raised only through step 4, so a local `Close` reaches plugins exactly when its `ID_DISCONNECTION_NOTIFICATION` or `ID_CONNECTION_LOST` is dispatched. RakNet never raises `LCR_CLOSED_BY_USER`: the loop that would is commented out in `RakPeer::CloseConnection` (`RakPeer.cpp:1652-1663`). There is no `ClosedByUser` reason.

Locally generated packets pass through the chain too (`RakPeer.cpp:1559-1562`), so a plugin that injects a message for other plugins (for example `ID_FCM2_NEW_HOST`) uses `host.Inject`.

### 11.3 Attach, detach, startup and shutdown

- `OnAttached` runs before any other hook of that plugin, on the dispatch step (section 10). Plugins listed in `RakPeerOptions.Plugins` are attached during `Start`, on the caller, before the loop exists, and receive `OnAttached` then `OnStartup`. A plugin attached after start receives no `OnStartup`, as in RakNet, where `OnRakPeerStartup` runs only in `Startup`.
- `Detach` takes effect at the next dispatch step and calls `OnDetached` there (`RakPeer.cpp:2788-2818`).
- Attaching a plugin that implements `INetworkObserver` while the peer is running is allowed; its observer hooks start on the next I/O cycle.
- `OnShutdown` runs once, first list then second (`RakPeer.cpp:1086-1093`).
- `OnInjected` runs when `Inject` is applied, on the dispatch step, before the entry is queued (`RakPeer.cpp:2826-2843`).
- The plugin lists are copy-on-write arrays, so dispatch allocates nothing.

### 11.4 Network observers (`UsesReliabilityLayer() == true`)

```csharp
public interface INetworkObserver     // runs in the I/O cycle; must not block
{
    void OnDatagramSent(ReadOnlySpan<byte> datagram, SystemAddress remote) { }      // OnDirectSocketSend
    void OnDatagramReceived(ReadOnlySpan<byte> datagram, SystemAddress remote) { }  // OnDirectSocketReceive
    void OnFrame(in FrameInfo frame) { }                                            // OnInternalPacket
    void OnAck(uint messageNumber, SystemAddress remote, ulong timeMs) { }          // OnAck
    void OnReliabilityNotification(ReadOnlySpan<char> message, int bitLength,
                                   SystemAddress remote, bool isError) { }          // OnReliabilityLayerNotification
    void OnUpdateCycle() { }                                                        // SetUserUpdateThread callback
}

public readonly ref struct FrameInfo   // the InternalPacket fields PacketLogger prints
{
    public bool IsSend { get; }               public uint FrameNumber { get; }        public ulong TimeMs { get; }
    public SystemAddress Remote { get; }      public RakConnection? Connection { get; }
    public Reliability Reliability { get; }   public Priority Priority { get; }       public byte OrderingChannel { get; }
    public UInt24 MessageNumber { get; }      public UInt24 OrderingIndex { get; }    public UInt24 SequencingIndex { get; }
    public bool IsSplit { get; }              public ushort SplitId { get; }          public uint SplitIndex { get; }
    public uint SplitCount { get; }           public ulong CreationTimeUs { get; }
    public ReadOnlySpan<byte> Data { get; }   public int BitLength { get; }
}
```

Addresses are passed as `SystemAddress` values, so nothing an observer receives is invalidated when the callback returns, other than the spans. Observers that are not plugins attach through `peer.LowLevel.Observers`. PacketLogger uses `OnFrame` and the datagram hooks, and prints what RakNet's PacketLogger prints.

### 11.5 What milestone 8 plugins need, and where it is

| Need | API |
|---|---|
| Send to a GUID, address or connection; broadcast | `IPluginHost.Send`, `Broadcast` with `AddressOrGuid` |
| Lookup by GUID or address, connection state | `TryGetConnection`, `GetGuidFromAddress`, `GetAddressFromGuid`, `GetConnectionState` |
| Inject packets (FCM2, NatPunch, ReadyEvent, Router2, RM3 download groups) | `Inject` |
| Keep a packet (`RR_STOP_PROCESSING`) and re-queue it | `Detach()`, `Consume`, later `Inject(owned, atHead)` |
| Forced receipts (VariableDeltaSerializer) | `ReserveReceipt`, `forceReceipt` |
| Out-of-band, TTL, raw socket sends, bound address | `LowLevel.SendOutOfBand`, `WriteOutOfBandHeader`, `SendTtl`, `RakSocket.SendTo`, `RakSocket.LocalAddress` |
| Address migration (Router2) | `LowLevel.ChangeSystemAddress` |
| Datagram interception (UDPProxy, NAT type detection) | `LowLevel.IncomingDatagramFilter` |
| Logging and statistics (PacketLogger, StatisticsHistory) | `INetworkObserver`, `GetStatistics`, `CopyStatistics` |
| Plugins on either `RakPeer` or `TcpInterface` (FileListTransfer and its dependents) | `IPluginHost` |

## 12. Options, limits and statistics

```csharp
public sealed record RakPeerOptions
{
    public required int MaxConnections { get; init; }                     // > 0, else ArgumentOutOfRangeException
    public int MaxIncomingConnections { get; init; }                      // 0 (RakPeer.cpp:208)
    public IReadOnlyList<SocketBinding> Bindings { get; init; } = [SocketBinding.AnyIPv4];
    public PeerThreading Threading { get; init; } = PeerThreading.Background;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public IDatagramTransportFactory Transport { get; init; } = UdpTransportFactory.Instance;
    public RakNetGuid? Guid { get; init; }                                // fixed GUID for tests; default random
    public RandomSource Random { get; init; } = RandomSource.Shared;      // GUIDs and non-security randomness
    public ReadOnlyMemory<byte> IncomingPassword { get; init; }
    public ReadOnlyMemory<byte> OfflinePingResponse { get; init; }        // at most 400 bytes (RakPeer.cpp:127)
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(10);        // RakPeer.cpp:249 (release builds)
    public TimeSpan UnreliableTimeout { get; init; } = TimeSpan.FromMilliseconds(1000); // RakPeer.cpp:238
    public bool OccasionalPing { get; init; }                             // false (RakPeer.cpp:228)
    public int SplitMessageProgressInterval { get; init; }                // 0 (RakPeer.cpp:236): no ID_DOWNLOAD_PROGRESS
    public bool LimitIpConnectionFrequency { get; init; }                 // false (RakPeer.cpp:312)
    public bool AllowConnectionResponseIpMigration { get; init; }         // false (RakPeer.cpp:233)
    public long PerConnectionOutgoingBandwidthLimit { get; init; }        // 0 (RakPeer.cpp:239)
    public ServerSecurity? Security { get; init; }                        // InitializeSecurity; null = DisableSecurity
    public IReadOnlyList<string> SecurityExceptions { get; init; } = [];
    public IReadOnlyList<IRakPlugin> Plugins { get; init; } = [];
    public NetworkSimulatorOptions? NetworkSimulator { get; init; }
    public PeerLimits Limits { get; init; } = PeerLimits.Default;
    public TimeSpan PluginUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(10);
    public TimeSpan ShutdownBlockDuration { get; init; } = TimeSpan.FromMilliseconds(100);   // used by DisposeAsync
}

public sealed record SocketBinding(IPEndPoint LocalEndPoint)          // SocketDescriptor; family from the address
{
    public static SocketBinding AnyIPv4 { get; }                       // 0.0.0.0:0
    public static SocketBinding Port(int port);                        // SocketDescriptor(port, 0)
    public int? ReceiveBufferSize { get; init; }
    public int? SendBufferSize { get; init; }
    public IDatagramTransport? Transport { get; init; }                // adopt a transport created elsewhere
}

/// Hardening limits from plan.md. Defaults accept everything stock RakNet sends.
public sealed record PeerLimits
{
    public static PeerLimits Default { get; }
    public int MaxSplitCount { get; init; }
    public int MaxSplitMessageBytes { get; init; }
    public int MaxConcurrentSplitsPerConnection { get; init; }
    public TimeSpan SplitReassemblyTimeout { get; init; }
    public int MaxOrderingBufferMessagesPerChannel { get; init; }
    public int EventQueueCapacity { get; init; } = 65_536;
}

public sealed record NetworkSimulatorOptions(float PacketLoss, TimeSpan MinExtraPing, TimeSpan ExtraPingVariance, int Seed = 0);

public abstract class RandomSource
{
    public static RandomSource Shared { get; }                         // thread-safe, unseeded
    public static RandomSource FromSeed(int seed);                     // deterministic tests
    public abstract ulong NextUInt64();                                // Get64BitUniqueRandomNumber
}
```

**Event queue backpressure.** When the stream holds `EventQueueCapacity` entries, the I/O cycle stops reading datagrams until the consumer drains it below half. The kernel then queues or drops datagrams and the reliability layer recovers them. Locally generated control entries (disconnects, receipts, connection failures) use reserved headroom above the cap, so they are never dropped or reordered. Memory stays bounded and nothing delivered is discarded.

**Statistics.**

```csharp
public enum RnsPerSecondMetric : byte
{ UserMessageBytesPushed, UserMessageBytesSent, UserMessageBytesResent, UserMessageBytesReceivedProcessed,
  UserMessageBytesReceivedIgnored, ActualBytesSent, ActualBytesReceived }

[InlineArray(7)] public struct MetricValues { private ulong _e; public readonly ulong this[RnsPerSecondMetric m] { get; } }
[InlineArray(4)] public struct PriorityValues<T> where T : unmanaged { private T _e; public readonly T this[Priority p] { get; } }

public readonly struct RakNetStatistics      // RakNetStatistics, by value, no arrays
{
    public MetricValues ValueOverLastSecond { get; }
    public MetricValues RunningTotal { get; }
    public ulong ConnectionStartTimeUs { get; }         // on the peer's TimeProvider
    public bool IsLimitedByCongestionControl { get; }
    public ulong BpsLimitByCongestionControl { get; }
    public bool IsLimitedByOutgoingBandwidthLimit { get; }
    public ulong BpsLimitByOutgoingBandwidthLimit { get; }
    public PriorityValues<uint> MessagesInSendBuffer { get; }
    public PriorityValues<double> BytesInSendBuffer { get; }
    public uint MessagesInResendBuffer { get; }
    public ulong BytesInResendBuffer { get; }
    public float PacketLossLastSecond { get; }
    public float PacketLossTotal { get; }
    public static RakNetStatistics operator +(in RakNetStatistics a, in RakNetStatistics b);   // operator+=
    public string ToString(StatisticsVerbosity verbosity);                                     // StatisticsToString
    public bool TryFormat(Span<char> destination, out int charsWritten, StatisticsVerbosity verbosity);
}

public enum StatisticsVerbosity : byte { Low, Medium, High, CongestionControl }   // verbosityLevel 0..3
public readonly record struct ConnectionStatistics(RakConnection Connection, RakNetStatistics Statistics);

public readonly record struct PoolStatistics(long SegmentsOutstanding, long OwnedEventsOutstanding, long Misses);
```

The I/O cycle publishes each connection's statistics block under a sequence lock every cycle; readers copy it without blocking the cycle or allocating. The field set covers the interop `stats` command, so `RakDotNetPeerDriver` maps it field for field. Peer-wide totals are also published through `System.Diagnostics.Metrics` (`Meter("RakDotNet")`, observable instruments over the snapshots).

## 13. Security configuration

```csharp
namespace RakDotNet.Security;

public sealed class RakPublicKey : IEquatable<RakPublicKey>
{
    public const int Length = 64;                                   // cat::EasyHandshake::PUBLIC_KEY_BYTES
    public static bool TryCreate(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out RakPublicKey? key);
    public ReadOnlySpan<byte> Bytes { get; }
}

/// The 32-byte private key (PRIVATE_KEY_BYTES) lives in a pinned array zeroed on Dispose.
public sealed class RakKeyPair : IDisposable
{
    public const int PrivateKeyLength = 32;
    public static RakKeyPair Generate();                            // EasyHandshake::GenerateServerKey, CSPRNG
    public static bool TryImport(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey,
                                 [NotNullWhen(true)] out RakKeyPair? keyPair);   // validated as the cat responder validates
    public RakPublicKey PublicKey { get; }
    public void ExportPrivateKey(Span<byte> destination);
    public void Dispose();                                          // CryptographicOperations.ZeroMemory
}

public sealed record ServerSecurity(RakKeyPair Keys)               // InitializeSecurity
{
    public bool RequireClientKey { get; init; }                     // bRequireClientKey
}

public enum PublicKeyMode : byte { InsecureConnection, AcceptAnyPublicKey, UseKnownPublicKey, UseTwoWayAuthentication }

public sealed class ConnectSecurity                                 // PublicKey + PublicKeyMode
{
    public static ConnectSecurity Insecure { get; }                                       // PKM_INSECURE_CONNECTION
    public static ConnectSecurity AcceptAnyPublicKey { get; }                             // PKM_ACCEPT_ANY_PUBLIC_KEY
    public static ConnectSecurity KnownServerKey(RakPublicKey serverPublicKey);           // PKM_USE_KNOWN_PUBLIC_KEY
    public static ConnectSecurity TwoWayAuthentication(RakPublicKey serverPublicKey,
                                                       RakKeyPair clientIdentity);        // PKM_USE_TWO_WAY_AUTHENTICATION
    public PublicKeyMode Mode { get; }
    public RakPublicKey? ServerPublicKey { get; }
    public RakKeyPair? ClientIdentity { get; }
}
```

```csharp
namespace RakDotNet;

public sealed class AddressPatternList : IReadOnlyCollection<string>   // security exception list
{
    public void Add(string pattern);          // '*' wildcards per octet, as RakNet; validated on Add
    public bool Remove(string pattern);
    public void Clear();                      // RemoveFromSecurityExceptionList(0)
    public bool Contains(IPAddress address);
    public bool Contains(string address);
}

public sealed class BanList
{
    public void Add(string pattern, TimeSpan duration = default);    // AddToBanList; default = forever; expiry on the TimeProvider
    public bool Remove(string pattern);
    public void Clear();
    public bool IsBanned(IPAddress address);
    public bool IsBanned(string address);
}
```

- **Server security is fixed at start** through `RakPeerOptions.Security`. RakNet accepts `InitializeSecurity` and `DisableSecurity` only while the peer is stopped (`RakPeer.cpp:765-766`), and a `RakPeer` is never stopped and then reused, so the options are the only place it can be set. Key material is validated when the `RakKeyPair` is created, so `Start` cannot fail on it.
- **The security exception list** is mutable at run time and applies to incoming connections only.
- **Client security** is `ConnectOptions.Security`. An invalid client configuration returns `ConnectAttemptResult.SecurityInitializationFailed` (`RakPeer.cpp:3296-3298`). Handshake rejections arrive as `RemoteSystemRequiresPublicKey`, `OurSystemRequiresSecurity` or `PublicKeyMismatch`.
- **Identity.** `connection.IsSecure` reports whether the link is encrypted; `connection.RemoteIdentityKey` is the client's key in two-way mode.
- **Encryption** of every datagram is invisible to the API, except that `Mtu` accounts for the authenticated-encryption overhead.
- **Randomness.** Key generation, cookies and handshake randomness always come from `RandomNumberGenerator`. `RakPeerOptions.Random` covers GUIDs and non-security randomness only, so no test seam can weaken production cryptography. Primitive-level known-answer tests run below the public API.

## 14. Error model

| Class | Mechanism |
|---|---|
| Invalid arguments: empty message, channel ≥ 32, undefined enum values, oversized password or offline response, bad indices, `MaxConnections ≤ 0`, an empty binding list | `ArgumentException` and subclasses |
| Misuse: concurrent dispatch steps, `Update` in Background mode, `Detach()` twice, attaching a plugin already attached elsewhere | `InvalidOperationException` |
| Use after shutdown; access to a released event | `ObjectDisposedException` |
| Environment failure at start | `RakStartupException` with `StartupError` |
| Connection start | `ConnectAttemptResult` |
| Connection outcome | `ConnectResult`, `ConnectFailed` events |
| Send outcome | `SendResult`, receipt events, `DeliveryOutcome` |
| Offline sends | `bool` "was sent" (`PingUnconnected`, `AdvertiseSystem`, `SendOutOfBand`, `SendTtl`, `RakSocket.SendTo`) |
| Malformed or hostile network input | never throws: the datagram or message is dropped, counted under `UserMessageBytesReceivedIgnored`, and reported to `INetworkObserver.OnReliabilityNotification` |
| Transient socket errors (ICMP-induced `ConnectionReset` on Windows) | ignored, as in RakNet |
| Fatal transport failure | `Events.Completion` faults with `RakTransportException`; pending async operations fault with it; no events are synthesised |

```csharp
public sealed class RakStartupException : Exception
{ public StartupError Error { get; } public int BindingIndex { get; } public SocketError SocketError { get; } }

public enum StartupError : byte          // StartupResult values that describe the environment
{
    SocketFamilyNotSupported,            // SOCKET_FAMILY_NOT_SUPPORTED
    SocketPortAlreadyInUse,              // SOCKET_PORT_ALREADY_IN_USE
    SocketFailedToBind,                  // SOCKET_FAILED_TO_BIND
    SocketFailedTestSend,                // SOCKET_FAILED_TEST_SEND
    FailedToCreateNetworkThread,         // FAILED_TO_CREATE_NETWORK_THREAD
    OtherFailure,                        // STARTUP_OTHER_FAILURE
}

public sealed class RakConnectException : Exception { public ConnectResult Result { get; } }
public sealed class RakTransportException : Exception { public int SocketIndex { get; } public SocketError SocketError { get; } }
```

`RAKNET_STARTED` is `Start` returning. `RAKNET_ALREADY_STARTED` cannot happen because a peer starts once. `INVALID_SOCKET_DESCRIPTORS` and `INVALID_MAX_CONNECTIONS` are argument validation. `PORT_CANNOT_BE_ZERO` applies only to console platforms RakDotNet does not target. `COULD_NOT_GENERATE_GUID` (`RakPeer.cpp:382`) cannot happen because the GUID is redrawn until it is neither 0 nor unassigned.

## 15. BitStream for application payloads

Bit packing stays in `RakDotNet.Protocol.Wire`, as [conventions.md](conventions.md) requires.

```csharp
namespace RakDotNet.Protocol.Wire;

// BitStreamWriter gains a resume constructor:
public BitStreamWriter(Span<byte> buffer, int bitPosition);

/// Growable, poolable BitStream. Every Write* delegates to BitStreamWriter; it never packs bits itself.
public sealed class BitStream : IBufferWriter<byte>, IDisposable
{
    public BitStream(int initialCapacityBytes = 256);         // ArrayPool<byte>.Shared
    public static BitStream Rent(int initialCapacityBytes = 256);
    public int BitsWritten { get; }
    public int BytesWritten { get; }
    public ReadOnlySpan<byte> Written { get; }
    public void Reset();                                      // keeps the buffer
    public BitStreamWriter BeginWrite(int sizeHintBytes);     // a writer at the current bit position, capacity ensured
    public void EndWrite(in BitStreamWriter writer);
    public void Write<T>(in T message) where T : IWireMessage<T>;
    // plus every BitStreamWriter primitive, forwarding through BeginWrite/EndWrite
    public BitStreamReader CreateReader();
    public void Dispose();
}

public readonly record struct TimestampHeader(ulong Time) : IWireMessage<TimestampHeader>;        // ID_TIMESTAMP + big-endian u64
public readonly record struct OutOfBandHeader(RakNetGuid Sender) : IWireMessage<OutOfBandHeader>; // WriteOutOfBandHeader
```

The RakNet encodings that application and plugin payloads use are extension methods on `ref BitStreamWriter` and `ref BitStreamReader`, so they work for stack buffers and `BitStream` alike: `WriteCompressed`/`TryReadCompressed`, `WriteBitsFromIntegerRange`, `WriteFloat16`, `WriteNormVector`, `WriteVector`, `WriteNormQuat`, `WriteOrthMatrix`, `WriteDelta`, `WriteCasted`, `WriteRakString`, `WriteCompressedString` (StringCompressor) and their readers. A `ref struct BitSerializer` with `IsWriting` covers RakNet's `Serialize(bool writeToBitstream, ...)` idiom. Each encoding is checked against native output with golden vectors.

```csharp
// Stack buffer, zero allocation, bit-exact length
Span<byte> buffer = stackalloc byte[64];
var w = new BitStreamWriter(buffer);
new TimestampHeader(peer.GetTime()).Write(ref w);
w.WriteByte((byte)GameIds.Move);
w.WriteCompressed(x);
connection.Send(w, Reliability.UnreliableSequenced, Priority.High, orderingChannel: 1);

// Writing straight into the outgoing segment
peer.Send(target, Reliability.UnreliableSequenced, Priority.High, 1, sizeHint: 32, (Pos: pos, Time: peer.GetTime()),
    static (ref BitStreamWriter w, (Vector3 Pos, ulong Time) s) =>
    {
        new TimestampHeader(s.Time).Write(ref w);
        w.WriteByte((byte)GameIds.Move);
        w.WriteVector(s.Pos);
    });

// A reused growable stream
_stream.Reset();
var bw = _stream.BeginWrite(256);
bw.WriteByte((byte)GameIds.Snapshot);
snapshot.Serialize(ref bw);
_stream.EndWrite(in bw);
peer.Broadcast(_stream, Reliability.ReliableOrdered);

// Receive: positioned after the ID and any timestamp header; Timestamp already in local time
if (e.Id == GameIds.Move) { var r = e.GetBodyReader(); r.TryReadVector(out var pos); var sentAt = e.Timestamp; }
```

User message IDs are `(MessageId)((byte)MessageId.UserPacketEnum + n)`.

## 16. Testability, allocation and AOT

### 16.1 Deterministic tests

- Every timer reads `RakPeerOptions.TimeProvider`: retransmits, RTO, pings, timeouts, connection attempts, ban expiry, split reassembly, receipt timeouts and statistics. Layers 0 to 2 take time as input.
- `PeerThreading.Manual` plus `FakeTimeProvider` gives fully deterministic runs: a test advances the clock, calls `Update` on each peer and asserts on `TryRead`.
- The datagram transport is the only I/O seam:

```csharp
public interface IDatagramTransport : IDisposable
{
    SystemAddress LocalAddress { get; }
    bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote);   // non-blocking
    bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0); // non-blocking; 0 = default TTL
    void SetReadySignal(Action signal);    // called when a datagram becomes available (Background mode wake)
}

public interface IDatagramTransportFactory
{
    bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError error, out SocketError socketError);
}
```

- `RakDotNet.Testing.InMemoryNetwork` is a hand-written fake for I/O, with seeded loss, delay, duplication and reordering scheduled on the peers' `TimeProvider`. Several real `RakPeer`s then run sociable tests in one process with no sockets. The same seam carries RakNet over other datagram media and implements the network simulator as a decorator.
- `RakPeerOptions.Guid` and `RandomSource.FromSeed` make GUIDs reproducible.

### 16.2 Zero allocations per message in steady state

Steady state: connections established, pools warm, event queue below its cap.

**Send.** `OutgoingMessage` is a `ref struct` (no boxing or closures). `Send` validates, takes a receipt number with `Interlocked.Increment` (skipping 0), copies the bytes into a segment from the peer's own pool (or runs the `MessageWriter` over it), and enqueues a `SendCommand` struct on a preallocated MPSC ring. The loop is woken only on an idle-to-busy transition or an `Immediate` send. The cycle queues the message into the connection's preallocated priority heaps, frames datagrams into slab buffers, and sends with `Socket.SendTo(ReadOnlySpan<byte>, SocketFlags, SocketAddress)` using a `SocketAddress` cached on the connection. Resend entries are pooled structs; reliable segments return to the pool on ACK, unreliable ones after framing.

**Receive.** The UDP transport reads with `Socket.ReceiveFrom(Span<byte>, SocketFlags, SocketAddress)` into slab slots, reusing one `SocketAddress`, and copies it into a `SystemAddress` value. In Background mode one outstanding `Socket.ReceiveFromAsync(Memory<byte>, SocketFlags, SocketAddress)` per binding provides the wake signal, with no allocation. The connection lookup is a `Dictionary<SystemAddress, Slot>`. Layers 0 to 2 decode with `BitStreamReader`. A deliverable message is copied once into a pooled segment; split reassembly writes into a pooled buffer of the final size, which becomes the event's segment. The entry is a struct in a ring that grows to its cap and never shrinks. `TryRead` returns a `ref struct` over the segment, and `WaitToReadAsync` completes synchronously or through one reusable value-task source.

**Elsewhere.** Statistics copy `[InlineArray]` structs. `ConnectionList` enumerates without allocating. Plugin and observer dispatch iterate arrays.

**Allowed allocations.** Connect and disconnect (a `RakConnection`, its tables, a new `ConnectionList` snapshot, the `ConnectAsync` state), DNS, offline traffic from unknown endpoints, `ReadAllAsync` enumerators, pool growth up to configured caps, event-ring growth, and `IPEndPoint` conversions requested by the caller.

A milestone 6 test enforces the budget: after warm-up it sends and receives N messages between peers and asserts that `GC.GetTotalAllocatedBytes(precise: true)` is unchanged, in both threading modes, with `peer.Pools` confirming no pool misses.

### 16.3 Trimming and AOT

All library projects set `IsAotCompatible`, which enables the trim, AOT and single-file analyzers, with warnings as errors. There is no reflection, `Type.GetType`, `MakeGenericType`, expression compilation, `dynamic`, runtime code generation or reflection-based serialisation. Plugins and observers are registered as instances; nothing is discovered. Extension points are interfaces with default methods and delegates. Generic methods (`Send<TState>`) are non-virtual on sealed classes. Fixed-size statistics use `[InlineArray]`. `System.Diagnostics.Metrics` is AOT-safe. CI publishes a NativeAOT console sample that connects, sends and receives over loopback.

## 17. Mapping from `RakPeerInterface`

| `RakPeerInterface` | RakDotNet |
|---|---|
| `GetInstance` / `DestroyInstance` | `RakPeer.Start(options)` / `Dispose` / `DisposeAsync` |
| `Startup(maxConnections, socketDescriptors, count, threadPriority)` | `RakPeerOptions.MaxConnections`, `.Bindings`, `.Threading`; `RakPeer.Start` throws `RakStartupException(StartupError)`. Thread priority is not configurable. |
| `InitializeSecurity(pub, priv, requireClientKey)` | `RakPeerOptions.Security = new ServerSecurity(RakKeyPair) { RequireClientKey }` |
| `DisableSecurity` | `RakPeerOptions.Security = null` |
| `AddToSecurityExceptionList` / `RemoveFromSecurityExceptionList` / `IsInSecurityExceptionList` | `peer.SecurityExceptions.Add` / `Remove` (`Clear` for a null address) / `Contains` |
| `SetMaximumIncomingConnections` / `GetMaximumIncomingConnections` | `peer.MaxIncomingConnections` |
| `NumberOfConnections` | `peer.ConnectionCount` |
| `SetIncomingPassword` / `GetIncomingPassword` | `peer.IncomingPassword` |
| `Connect(host, port, pwd, len, publicKey, socketIndex, attempts, interval, timeout)` | `peer.Connect(SystemAddress, ConnectOptions)` → `ConnectAttemptResult`, or `ConnectAsync(host, port, ConnectOptions)` → `ConnectResult`; `publicKey` → `ConnectOptions.Security`; `socketIndex` → `ConnectOptions.SocketIndex` |
| `ConnectWithSocket` | `ConnectOptions.SocketIndex` selecting a binding whose `SocketBinding.Transport` adopted that socket at start |
| `Shutdown(blockDuration, channel, priority)` | `peer.ShutdownAsync(blockDuration, channel, priority)` |
| `IsActive` | `peer.IsActive` |
| `GetConnectionList` | `peer.Connections` (each `Address`) |
| `GetNextSendReceipt` / `IncrementNextSendReceipt` | `peer.PeekNextReceipt()` / `peer.ReserveReceipt()` |
| `Send(data, length, priority, reliability, channel, target, broadcast, forceReceipt)` | `peer.Send(message, target, reliability, priority, channel, forceReceipt)` or `connection.Send(...)`; `broadcast = true` → `peer.Broadcast(..., except: target)` |
| `Send(BitStream*, ...)` | the same calls with an `OutgoingMessage` from `BitStream` or `BitStreamWriter` (bit-exact length), or `Send<TState>` with a `MessageWriter` |
| `SendList` | `new OutgoingMessage(in ReadOnlySequence<byte>)` |
| `SendLoopback` | `peer.SendLoopback(span)`; arrives as `Message` with `IsLocal` |
| `Receive` | `peer.Events.TryRead(out PeerEvent)` (after `peer.Update()` in Manual mode); `WaitToReadAsync`; `ReadAllAsync` |
| `DeallocatePacket` | automatic at the next read; `OwnedPeerEvent.Dispose` for detached events |
| `GetMaximumNumberOfPeers` | `peer.MaxConnections` |
| `CloseConnection(target, notify, channel, priority)` | `peer.Close(target, notify, channel, priority)` / `connection.Close` / `CloseAsync` |
| `GetConnectionState` | `peer.GetConnectionState(AddressOrGuid)` / `connection.State` |
| `CancelConnectionAttempt` | `peer.CancelConnectionAttempt(address)`, or cancel the `ConnectAsync` token |
| `GetIndexFromSystemAddress` | `peer.TryGetConnection(address, out c)` → `c.SystemIndex` |
| `GetSystemAddressFromIndex` / `GetGUIDFromIndex` | `peer.TryGetConnectionByIndex(i, out c)` → `c.Address` / `c.Guid` |
| `GetSystemList` | `peer.Connections` (each `Address` and `Guid`) |
| `AddToBanList` / `RemoveFromBanList` / `ClearBanList` / `IsBanned` | `peer.BanList.Add(pattern, duration)` / `Remove` / `Clear` / `IsBanned` |
| `SetLimitIPConnectionFrequency` | `peer.LimitIpConnectionFrequency` |
| `Ping(SystemAddress)` | `connection.Ping()` |
| `Ping(host, port, onlyReplyOnAcceptingConnections, socketIndex)` | `peer.PingUnconnected(address, ...)` / `PingUnconnectedAsync(host, port, ...)`, answered by an `UnconnectedPong` event; or `peer.PingAsync(address, timeout)` |
| `GetAveragePing` / `GetLastPing` / `GetLowestPing` | `connection.AveragePing` / `LastPing` / `LowestPing` (`null` for -1) |
| `SetOccasionalPing` | `peer.OccasionalPing` |
| `GetClockDifferential` | `connection.ClockDifferential`; incoming `ID_TIMESTAMP` is already shifted (`PeerEvent.Timestamp`) |
| `SetOfflinePingResponse` / `GetOfflinePingResponse` | `peer.OfflinePingResponse` |
| `GetInternalID(UNASSIGNED, i)` / `GetInternalID(target, i)` | `peer.InternalAddresses[i]` / `connection.RemoteInternalAddresses[i]` |
| `SetInternalID` | `peer.SetInternalAddress(address, i)` |
| `GetExternalID(target)` | `connection.ExternalAddress`; `peer.ExternalAddress` for `UNASSIGNED` |
| `GetMyGUID` | `peer.Guid` |
| `GetMyBoundAddress(i)` | `peer.Sockets[i].LocalAddress` |
| `Get64BitUniqueRandomNumber` | `RandomSource.Shared.NextUInt64()` |
| `GetGuidFromSystemAddress` | `peer.GetGuidFromAddress(address)` (own GUID for `Unassigned`) |
| `GetSystemAddressFromGuid` | `peer.GetAddressFromGuid(guid)` |
| `GetClientPublicKeyFromSystemAddress` | `connection.RemoteIdentityKey` |
| `SetTimeoutTime(ms, UNASSIGNED)` | `peer.DefaultTimeout` (default and every live connection) |
| `SetTimeoutTime(ms, target)` / `GetTimeoutTime(target)` | `connection.Timeout` |
| `GetMTUSize(target)` | `connection.Mtu`; `RakNetConstants.MaximumMtuSize` for `UNASSIGNED` |
| `GetNumberOfAddresses` / `GetLocalIP(i)` | `peer.LocalAddresses.Count` / `peer.LocalAddresses[i]` |
| `IsLocalIP` | `peer.IsLocalAddress(IPAddress)` |
| `AllowConnectionResponseIPMigration` | `peer.AllowConnectionResponseIpMigration` |
| `AdvertiseSystem` | `peer.AdvertiseSystem(address, data, i)` / `AdvertiseSystemAsync(host, port, ...)`; received as `AdvertiseSystem` |
| `SetSplitMessageProgressInterval` / `GetSplitMessageProgressInterval` | `peer.SplitMessageProgressInterval`; received as `DownloadProgress` |
| `SetUnreliableTimeout` | `peer.UnreliableTimeout` |
| `SendTTL` | `peer.LowLevel.SendTtl(address, ttl, i)` |
| `AttachPlugin` / `DetachPlugin` | `peer.Plugins.Attach` / `Detach` (`IRakPlugin`; also implementing `INetworkObserver` puts it in the reliability-layer list) |
| `PushBackPacket(packet, pushAtHead)` | `peer.Inject(OwnedPeerEvent, atHead)` (consumes) or `Inject(span, address, guid, atHead)` (copies) |
| `ChangeSystemAddress(guid, address)` | `peer.LowLevel.ChangeSystemAddress(guid, address)` |
| `AllocatePacket` | not needed: build the bytes with `BitStream` or `BitStreamWriter` and `Inject` them |
| `GetSocket(target)` | `connection.Socket`; `peer.Sockets[0]` for `UNASSIGNED` |
| `GetSockets` / `ReleaseSockets` | `peer.Sockets`; nothing to release (`IsOpen` becomes false at shutdown) |
| `WriteOutOfBandHeader` | `peer.LowLevel.WriteOutOfBandHeader(ref writer)`, or Wire `new OutOfBandHeader(peer.Guid).Write(ref writer)` |
| `SendOutOfBand` | `peer.LowLevel.SendOutOfBand(address, payload, i)` / `SendOutOfBandAsync(host, port, ...)`; received as `OutOfBand` |
| `SetUserUpdateThread` | `INetworkObserver.OnUpdateCycle`, or drive the cycle in `PeerThreading.Manual` |
| `SetIncomingDatagramEventHandler` | `peer.LowLevel.IncomingDatagramFilter` |
| `ApplyNetworkSimulator` / `IsNetworkSimulatorActive` | `RakPeerOptions.NetworkSimulator` or `peer.LowLevel.NetworkSimulator` / `peer.LowLevel.IsNetworkSimulatorActive` |
| `SetPerConnectionOutgoingBandwidthLimit` | `peer.PerConnectionOutgoingBandwidthLimit` |
| `GetStatistics(address)` / `GetStatistics(index)` | `connection.GetStatistics()`, through `TryGetConnection` or `TryGetConnectionByIndex` |
| `GetStatisticsList` | `peer.CopyStatistics(Span<ConnectionStatistics>)` |
| `GetReceiveBufferSize` | `peer.Events.Count` |
| `RunUpdateCycle` | `peer.Update()` in `PeerThreading.Manual` |
| `StatisticsToString` | `RakNetStatistics.ToString(StatisticsVerbosity)` |

`Packet` fields map to `PeerEvent` as: `data`/`length` → `Data`, `bitSize` → `BitLength`, `systemAddress` → `Address`, `guid` → `Guid`, `wasGeneratedLocally` → `IsLocal`.

### 17.1 `PluginInterface2`

| `PluginInterface2` | RakDotNet |
|---|---|
| `OnAttach` / `OnDetach` | `IRakPlugin.OnAttached(host)` / `OnDetached` |
| `Update` | `OnUpdate` (each `TryRead`, and at least every `PluginUpdateInterval` for async consumers) |
| `OnReceive` → `PluginReceiveResult` | `OnEvent(PeerEvent)` → `Continue` / `Consume`; `Detach()` then `Consume` for `RR_STOP_PROCESSING` |
| `OnRakPeerStartup` / `OnRakPeerShutdown` | `OnStartup` / `OnShutdown` |
| `OnClosedConnection(address, guid, LCR_*)` | `OnDisconnected(RakConnection, DisconnectReason)` |
| `OnNewConnection(address, guid, isIncoming)` | `OnConnected(RakConnection, isIncoming)` |
| `OnFailedConnectionAttempt(packet, FCAR_*)` | `OnConnectFailed(PeerEvent, ConnectFailure)` |
| `UsesReliabilityLayer` | the plugin also implements `INetworkObserver` |
| `OnDirectSocketSend` / `OnDirectSocketReceive` | `INetworkObserver.OnDatagramSent` / `OnDatagramReceived` |
| `OnReliabilityLayerNotification` | `INetworkObserver.OnReliabilityNotification` |
| `OnInternalPacket` | `INetworkObserver.OnFrame(in FrameInfo)` |
| `OnAck` | `INetworkObserver.OnAck` |
| `OnPushBackPacket` | `IRakPlugin.OnInjected` |
| `GetRakPeerInterface` / `SetRakPeerInterface` / `SetTCPInterface` | the `IPluginHost` passed to `OnAttached`; `host.Peer` |
| `GetMyGUIDUnified` | `host.Guid` |
| `SendUnified` / `SendListUnified` | `host.Send` / `host.Broadcast` with any `OutgoingMessage` |
| `AllocatePacketUnified` / `PushBackPacketUnified` / `DeallocPacketUnified` | `host.Inject` / `OwnedPeerEvent.Dispose` |

### 17.2 `IPeerDriver` on the public API

| Driver call | API |
|---|---|
| `StartupAsync` | `RakPeer.Start(new() { MaxConnections, MaxIncomingConnections, Bindings = [new(endPoint)] })` |
| `ConnectAsync` | `peer.Connect(remote, options)`, reporting the `ConnectAttemptResult` name in RakNet's spelling |
| `SendAsync` | `peer.Send` / `Broadcast` → `Receipt.Value` |
| `CloseAsync` | `peer.Close(guid, notify)` |
| `PingAsync` | `peer.PingUnconnected` |
| `SetOfflinePingResponseAsync` | `peer.OfflinePingResponse = data` |
| `GetConnectionStateAsync` | `peer.GetConnectionState(guid)` |
| `ShutdownAsync` | `peer.ShutdownAsync(blockMs)` |
| `Packets` | a pump over `WaitToReadAsync`/`TryRead` that skips events with `IsRakNetPacket == false` and writes `ReceivedPacket` from `Data`, `Guid`, `Address`, `ReceivedTime` and `Timestamp` |

## 18. Decisions

### 18.1 Compatibility facade: provided, as the separate layer 4 assembly `RakDotNet.Compatibility`

`RakPeerInterface` in `RakDotNet.Compatibility` holds one `RakPeer` and implements the mapping table in reverse, with RakNet's names and shapes: `Receive()` runs `Update()` in Manual mode, reads with `TryRead`, skips events with `IsRakNetPacket == false`, detaches the rest and returns a managed `Packet` class. `DeallocatePacket` disposes it, is idempotent, and is optional, because an undisposed packet is collected. Use after deallocation throws. `PushBackPacket` consumes the owned event. Hostnames are `string`.

Reasons:

1. It is the cheapest migration path for code written against RakNet or Brutal.RakNet, and for porting RakNet's own samples and tests line by line.
2. It stays a thin adapter, as [plan.md](plan.md) intends, because `PeerEvent.Data` already holds RakNet's exact bytes, `ConnectAttemptResult` is returned synchronously by `Connect`, and synthetic events are flagged.
3. It does not bring back the pitfalls a faithful C++ shape would: there is no unmanaged memory to free, no double free, and no unterminated hostname.
4. Building it proves the mapping table is complete, because every row must compile against the public API.
5. Keeping it out of the core assembly keeps the core surface modern and small. The core never depends on it, no plugin may depend on it, and `RakDotNetPeerDriver` targets the public API directly so the interop matrix exercises the real surface.

### 18.2 Layer 2 (Peer): not public; it stays `internal`

Reasons:

1. Every real need for it is met by a small, stable public seam: custom I/O, socket adoption and simulation by `IDatagramTransport`; deterministic tests by `TimeProvider`, `PeerThreading.Manual` and `InMemoryNetwork`; raw traffic by `IncomingDatagramFilter`, `INetworkObserver` and `RakSocket.SendTo`; plugins by `IPluginHost` and `RakPeerLowLevel`.
2. Publishing it would freeze internals that the hardening, MTU, performance and security milestones must stay free to reshape: the connection table, the timer wheel, the ban and rate-limit structures, buffer ownership between layers, and the secure handshake states.
3. The sans-IO layers are single-threaded and trust their caller to sequence calls. A public layer 2 would need defensive checks on every call (hot-path cost) or would hand users a footgun.
4. Plugins must be implementable on the public API alone. Keeping layer 2 internal enforces that: a capability a plugin port needs is added to the public surface deliberately.

Layer 0 (Wire) stays public: its codecs are stable, checked against golden vectors, and needed for application and plugin payloads.

### 18.3 Local close produces RakNet's packets and no others

A local `Close` is reported by the packets RakNet itself enqueues: `ID_CONNECTION_LOST` immediately for a silent close of a connected system (`RakPeer.cpp:1667-1678`), and `ID_DISCONNECTION_NOTIFICATION` after the outgoing data drains for a notifying close (`RakPeer.cpp:5873-5903`). No extra `Disconnected` event is synthesised and there is no `ClosedByUser` reason, because RakNet's `LCR_CLOSED_BY_USER` callback is commented out (`RakPeer.cpp:1652-1663`). Reason: anything else would give a C# peer a different stream from a native one, against the interop pass criterion, and would make the facade drop real packets. `ClosedLocally` carries the extra knowledge without changing the stream.

### 18.4 Synthetic events are flagged, carry no RakNet bytes and skip plugins

The only event kind the C# peer adds is `ReceiptAbandoned`, needed so that every untracked receipt completes. It has `IsRakNetPacket == false` and empty `Data`, so it can never be mistaken for `ID_SND_RECEIPT_LOSS`, which RakNet emits only for unreliable receipts after the unreliable timeout (`ReliabilityLayer.cpp:1860-1876`). It is not dispatched to plugins, because plugins are ports of RakNet code that observe RakNet's stream; RakNet plugins learn about lost connections from `OnClosedConnection`, which they still get. The facade and the interop driver filter on the flag.

### 18.5 The event is a `ref struct` and the reader is purpose-built

A borrowed event that is a copyable struct can be stored or carried across `await` with no compiler help. Making `PeerEvent` a `ref struct` moves the common misuse to compile time, and the generation check remains as the backstop. The cost is that `await foreach` cannot yield borrowed events, so `ReadAllAsync` yields owned ones; the allocation-free async pattern is `WaitToReadAsync` with an inner `TryRead` loop. For the same reason the reader is not a `ChannelReader<T>`: a read runs plugins and releases the previous event, which no `ChannelReader` member may do, and `TryPeek` could not be given a sound meaning.

### 18.6 Both a background loop and a manual cycle

`PeerThreading.Background` (the default) suits services: one loop thread, the stream consumed from any thread. `PeerThreading.Manual` suits game loops, which already own a thread and a frame, and tests, which become fully deterministic with `FakeTimeProvider`. Both run the same cycle code. The background loop has a single thread with no per-socket receive threads, so there is no hand-off on the receive path.

### 18.7 Plugin dispatch keeps RakNet's two lists

RakNet runs every hook on its non-reliability-layer plugins before its reliability-layer plugins (`RakPeer.cpp:1528-1598`). A single attach-ordered list would change the `OnEvent` order for a mixed set of plugins, so a plugin's list is determined by whether it implements `INetworkObserver`, matching `UsesReliabilityLayer()`. Every hook, `OnAttached` and `OnDetached` included, runs on the dispatch step so plugin state is never touched from two threads.

### 18.8 Async completions resolve when the outcome is produced

`ConnectAsync`, `CloseAsync`, `Closed` and `SendAndTrackAsync` complete when the I/O cycle produces the outcome, not when the consumer reads the matching event. Reason: a task that depends on the consumer turns a missing or slow reader into a deadlock. The consequence is that `await ConnectAsync` may resume before plugins have run `OnConnected` for that connection; an application that must send only after plugin setup sends from its handler for the `Connected` event instead. The event still appears on the stream at its ordered position.

### 18.9 Received bit length is preserved

`PeerEvent.BitLength` is RakNet's `Packet::bitSize`, which for connected messages is the bit length delivered by the reliability layer (`RakPeer.cpp:6046`, `RakPeer.cpp:6231`) and is whole bytes only for the connection-accepted message (`RakPeer.cpp:6186`). The send side keeps the bit-exact length that `Send(BitStream*)` uses (`RakPeer.cpp:1408`) through `OutgoingMessage`, because the encapsulated message length on the wire is in bits.

### 18.10 A peer starts once

`RakPeer.Start` returns an active peer and shutdown is final. RakNet allows `Startup` after `Shutdown`; supporting it would add "stopped but reusable" states to every member, and fixing server security at start depends on it. Creating a new peer gives the same capability.

### 18.11 Addresses are values; targets are unambiguous

`SystemAddress` is a value type in Wire so lookups and observer callbacks never allocate and never expose a reused `SocketAddress` whose validity ends with a call. `AddressOrGuid` is kept for the many ported call sites that target "a GUID or an address", but holds exactly one of them, which removes RakNet's precedence question.

### 18.12 Reliability is always explicit

Every send names its reliability, and there is no options struct whose `default` means `Unreliable` at `Immediate` priority. Reason: such a default is wrong for almost every caller and can only be caught by an analyzer.

### 18.13 Connection start, outcome and failure stay separate

`ConnectAttemptResult` (synchronous, RakNet's `ConnectionAttemptResult`), `ConnectFailure` (asynchronous, RakNet's failure IDs) and success (`ConnectResult.Connection`) are three separate facts. Reason: a single enum mixing "started" with "connected" and with failures is easy to misread, and the interop driver needs the synchronous value under its RakNet name.

### 18.14 `ConnectWithSocket` and server security are fixed at start

Adopting an externally created socket happens through `SocketBinding.Transport` when the peer starts, and `ConnectOptions.SocketIndex` selects it; there is no borrowing of another running peer's socket. Server security is set through `RakPeerOptions.Security`. Reason: both are rare, both reach into state the I/O cycle owns, and making them free-threaded at run time would require cross-peer routing and reconfiguration of live handshake state.

### 18.15 RakNet's release-build defaults

Defaults are those of a RakNet release build: `DefaultTimeout` 10 s (`RakPeer.cpp:249`), `UnreliableTimeout` 1000 ms (`RakPeer.cpp:238`), occasional ping off (`RakPeer.cpp:228`), `MaxIncomingConnections` 0 (`RakPeer.cpp:208`), 12 connection attempts 500 ms apart (`RakPeerInterface.h:139`). Reason: receipt-loss timing, timeout detection and admission must match a native peer configured the same way.
