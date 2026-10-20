# RakDotNet public API design

This document covers the public API of RakDotNet: layer 3 of [plan.md](plan.md) (namespace and assembly `RakDotNet`), the layer 0 types it adds to `RakDotNet.Protocol.Wire`, and the layer 4 assemblies that sit directly on it. It is binding for the implementation, for `RakDotNetPeerDriver` and for every plugin port.

The API is declared as compiling code. Signatures, and what each member does, are in the source and its XML doc comments. This document keeps the principles, the rules that span several types, the mappings from RakNet, and the decisions with their reasons.

C++ references are to RakNet 4.081 at commit `1a169895`, as `File.cpp:line`. Where this API differs from RakNet's observable behaviour, the difference is stated with its reason.

## 1. Purpose

The API exposes the whole of RakNet's `RakPeerInterface` and `PluginInterface2` in a form that is natural in .NET 10:

- Every RakNet capability is reachable, so every RakNet plugin and utility can be ported on the public API alone.
- The bytes an application or plugin sees for a RakNet packet are exactly the bytes RakNet's `Receive()` would return, so ported code and the interop harness behave identically against native and C# peers.
- The hot path allocates nothing per message in steady state: connections established, pools warm and the event queue below its cap. Connects and disconnects, DNS, offline traffic from unknown endpoints, `ReadAllAsync` enumerators, pool and event-ring growth up to their caps, and `IPEndPoint` conversions the caller asks for may allocate. A milestone 6 test enforces this in both threading modes.
- Everything is deterministic under test: time comes from `TimeProvider`, I/O from a replaceable datagram transport, and the I/O cycle can be driven by hand.

## 2. Design principles

1. **One ordered event stream per peer.** Connection events, messages, receipts and offline replies arrive in one stream, in the order RakNet would return them from `Receive()`.
2. **RakNet-visible bytes are never invented.** An event that RakNet would not produce is a distinct kind with `IsRakNetPacket == false` and no RakNet bytes. The compatibility facade and the interop driver drop these events, so the stream they report is the stream native RakNet produces.
3. **Borrowed by default, owned by request.** A received event is valid until the next read. The type system stops it escaping (it is a `ref struct`), and a generation check catches the rest. Keeping an event is an explicit `Detach()`.
4. **Commands are thread-safe and never block.** `Send`, `Close`, `Connect` and every setter enqueue a command for the I/O cycle. Queries read snapshots.
5. **Programming errors throw; runtime outcomes are values.** Bad arguments throw `ArgumentException`, misuse `InvalidOperationException`, and use after shutdown or of a released event `ObjectDisposedException`. Startup, connection and send outcomes are typed results or events. Malformed or hostile network input never throws: it is dropped, counted and reported to network observers. A fatal transport failure faults `Events.Completion` and is not turned into events.
6. **Plugins run where RakNet runs them.** Plugin hooks run on the event consumer, inside the read, in RakNet's order. Reliability-layer observers run in the I/O cycle.
7. **RakNet names where they carry meaning.** `RakNetGuid`, `SystemAddress`, `AddressOrGuid`, `ConnectionState`, `RakNetStatistics`, `BitStream`, the `ConnectAttemptResult` and failure names, and the plugin hook names stay recognisable, so porting is mostly mechanical.
8. **Escape hatches are grouped.** Out-of-band sends, raw datagrams, TTL, datagram filtering, address migration, the network simulator and observers live under `peer.LowLevel`, which keeps the main surface small.
9. **No reflection.** Every library project sets `IsAotCompatible`, so the trim, AOT and single-file analyzers run with warnings as errors. Plugins and observers are registered as instances and nothing is discovered; extension points are interfaces with default methods and delegates, and generic methods such as `Send<TState>` are non-virtual on sealed classes. CI publishes a sample with NativeAOT that connects, sends and receives over loopback.

## 3. Assemblies and namespaces

| Assembly | Namespace | Contents | Source |
|---|---|---|---|
| `RakDotNet.Protocol` | `RakDotNet.Protocol.Wire` | Layer 0, public: the RakNet enums and wire types, `RakNetGuid`, `SystemAddress`, `BitStreamReader`, `BitStreamWriter`, `BitStream`, `BitStreamEncodings`, `BitSerializer`, `StringCompressor`, `HuffmanEncodingTree`, `StringTable`; in `.Messages`, the message codecs, `RakNetConstants`, `TimestampHeader`, `OutOfBandHeader` | `src/RakDotNet.Protocol/Wire/` |
| `RakDotNet.Protocol` | `.Session`, `.Peer` | Layers 1 and 2, `internal` (decision 11.2) | `src/RakDotNet.Protocol/Session/`, `Peer/` |
| `RakDotNet` | `RakDotNet` | Layer 3: `RakPeer`, `RakConnection`, the event stream, sending, options, statistics, plugins and observers | `src/RakDotNet/` |
| `RakDotNet` | `RakDotNet.Security` | Key and connect-security types | `src/RakDotNet/Security/` |
| `RakDotNet.Testing` | `RakDotNet.Testing` | `InMemoryNetwork`, a datagram transport fake for sociable multi-peer tests | `src/RakDotNet.Testing/` |
| `RakDotNet.Connections` | `RakDotNet.Connections` | Layer 4: `ConnectionDemultiplexer`, a queue per connection | `src/RakDotNet.Connections/` |
| `RakDotNet.Compatibility` | `RakDotNet.Compatibility` | Layer 4: the `RakPeerInterface`-shaped facade (decision 11.1) | `src/RakDotNet.Compatibility/` |
| `RakDotNet.Plugins` | `RakDotNet.Plugins` | Layer 4: RakNet's plugins and utilities on the public API, one folder per plugin | `src/RakDotNet.Plugins/` |

Wire types are reused everywhere and never redeclared, as [conventions.md](conventions.md) requires. Layer 2 has internal types named `PeerEvent` and `ConnectionState`; code in a `RakDotNet.*` namespace that uses them qualifies them as `Protocol.Peer.PeerEvent`, because the public types of the same name in `RakDotNet` take precedence over a `using` there.

Usage is shown by the projects in [samples/](../samples): a server loop ([RakDotNet.Samples.Server](../samples/RakDotNet.Samples.Server/Program.cs)), a client that connects and sends ([RakDotNet.Samples.Client](../samples/RakDotNet.Samples.Client/Program.cs)), the manual update mode ([RakDotNet.Samples.ManualUpdate](../samples/RakDotNet.Samples.ManualUpdate/Program.cs)), and a client and server echoing a message in one process, which CI publishes with NativeAOT ([RakDotNet.Samples.Loopback](../samples/RakDotNet.Samples.Loopback/LoopbackEcho.cs)).

## 4. Event ordering

The stream is `peer.Events`, RakNet's `Receive()`. Each entry is a borrowed `PeerEvent` whose `Data` holds exactly RakNet's `Packet::data`, rewrites included. Internal traffic (connected pings, handshake messages, `ID_DETECT_LOST_CONNECTIONS`) never reaches it. An unconnected ping this peer answers does, as RakNet queues it: a one-byte `Message` holding `ID_UNCONNECTED_PING` or `ID_UNCONNECTED_PING_OPEN_CONNECTIONS`, from the sender's address and GUID (`RakPeer.cpp:4672-4678`).

- Entries are delivered in the order the I/O cycle produced them, which is RakNet's `packetReturnQueue` order. Per-channel ordering and sequencing hold within the stream.
- `Connected` precedes that connection's first message. A connection's last delivered message precedes its `Disconnected`, and nothing for that connection follows it.
- A receipt event follows the send that produced it. `ReceiptAbandoned` events for a connection precede its `Disconnected` when it has one.
- `Inject(..., atHead: true)` places the entry so the next `TryRead` returns it, as `PushBackPacket(packet, true)` (`RakPeer.cpp:2838-2841`).
- An `ID_TIMESTAMP` entry has its time shifted when it is read, not when it arrives: every entry popped, injected ones included, is shifted by the clock differential of the active connection at its address at that moment, as `RakPeer::Receive` does (`RakPeer.cpp:1548-1555`, `3773-3799`).
- Commands from one thread are applied in call order.
- Async completions (`ConnectAsync`, `CloseAsync`, `Closed`, `SendAndTrackAsync`, `PingAsync`) resolve when the I/O cycle produces the outcome, not when the matching event is read, and the event still appears at its position (decision 11.8). Tracked receipts go to their awaiter instead of the stream.
- When the stream holds `PeerLimits.EventQueueCapacity` entries the cycle stops reading datagrams until the consumer drains it below half; locally generated control entries use reserved headroom, so nothing delivered is dropped or reordered.

Every untracked receipt ends on the stream with `ReceiptAcked`, `ReceiptLost` or `ReceiptAbandoned`, or with `Events.Completion` at shutdown; every tracked receipt completes its task. This is the hardening rule "every send's receipt or task completes or faults".

## 5. Ownership and lifetime

An event returned by `TryRead` is borrowed: it is valid until the next `TryRead` on that peer, until `Update` in Manual mode, or until shutdown, and an event passed to a plugin hook is valid for the call. `PeerEvent`, `UnconnectedPongView`, `DownloadProgressView` and `FrameInfo` are `ref struct`s, so the compiler stops them being stored, captured, boxed or held across `await`. Behind that, every `PeerEvent` member checks a generation and throws `ObjectDisposedException` once its segment is released; reused memory is never read, though a span already taken from `Data` is not tracked.

`Detach()` turns a borrowed event into an `OwnedPeerEvent` that the caller disposes or hands to `Inject`, which consumes it without a copy. Release is a compare-exchange on the generation, so disposing a copy or disposing twice is harmless, and an owned event that is never disposed is collected by the GC and counted as a pool miss. No native memory is involved.

Data flows the other way by copy. Every send copies the caller's bytes into a pooled segment, or lets the caller write straight into it, before returning, so the caller's buffer is free when `Send` returns. `Inject` of a span copies. Byte settings (offline ping response, passwords) and key material are copied on assignment.

Shutdown releases every borrowed event and discards undelivered entries, as RakNet's `Shutdown` does (`RakPeer.cpp:1176-1181`); owned events stay valid until disposed. A `RakConnection` is never reused: it stays readable with `State == Disconnected` after its connection ends, so object identity is safe as a key.

## 6. Connection lifecycle

```
Connect ─► Pending ─► Connecting ─┬─► Connected ──┬─ Close(notify: true) ─► Disconnecting ─► Disconnected
                                  │               ├─ Close(notify: false) ─────────────────► Disconnected
incoming request ─────────────────┤               ├─ remote notification ──────────────────► Disconnected
                                  │               └─ timeout or dead link ─────────────────► Disconnected
                                  └─► ConnectFailed ─► NotConnected
```

A `RakConnection` is one slot of RakNet's remote system list (`RemoteSystemStruct`). It is created when the remote system takes a slot (`AssignSystemAddressToRemoteSystemList`, `RakPeer.cpp:3595-3770`): an outgoing attempt once the server answers the offline handshake, an incoming one once its `OPEN_CONNECTION_REQUEST_2` is accepted. It is `Connecting` until `Connected`, and lookups, `GetConnectionState`, sends, broadcasts and the `AlreadyConnectedToEndpoint` check see it in every state, as RakNet's slot semantics (`isActive` and `connectMode`) prescribe; `Connections`, `ConnectionCount` and `TryGetConnectionByIndex` list only `Connected` ones (`RakPeer.cpp:1799-1850`). A handshake that RakNet ends without a packet ends the object with no event. `Pending` belongs to an attempt that has no slot yet and is visible only through `GetConnectionState` and the `ConnectAsync` task.

Every transition into `Disconnected` is reported by the packet RakNet itself enqueues for it (decision 11.3), and every failed attempt by RakNet's failure ID. The citations are on `RakPeer.Close`, `DisconnectReason` and `ConnectFailure`. `Connect` returns the synchronous `ConnectAttemptResult`; the outcome arrives later as `Connected` or `ConnectFailed` (decision 11.13). Cancelling a queued attempt produces no event, as in RakNet (`RakPeer.cpp:1686-1710`).

## 7. Shutdown

`ShutdownAsync(blockDuration, ...)` follows `RakPeer::Shutdown` (`RakPeer.cpp:1041-1181`). From the call on, the stream delivers nothing more: a concurrent `TryRead` finishes first and later reads return false. With a positive block duration every active connection is notified and the peer waits on its `TimeProvider` until none is active or the duration elapses, running the cycle itself in Manual mode. Then `OnShutdown` runs once on every plugin, on the dispatch step; the sockets close, undelivered entries are discarded and `Events.Completion` completes.

Pending operations end rather than hang: connect, ping and host-resolving operations throw `OperationCanceledException`, tracked sends complete with `DeliveryStatus.PeerShutdown`, and `Closed` tasks complete. No `Disconnected` or other event is added for connections ended by shutdown, because RakNet clears its queue (`RakPeer.cpp:1177-1181`). `Dispose` is a shutdown with no block, and `DisposeAsync` blocks for `RakPeerOptions.ShutdownBlockDuration`; both are idempotent. After shutdown every command throws `ObjectDisposedException` and every query returns its last value.

## 8. Threading

Layers 1 and 2 (sessions, the connection table, ban list and timers) are owned by the I/O cycle and need no locks. One cycle runs at a time. A cycle drains commands, receives datagrams without blocking until the transport is empty or the event queue is full, feeds the sans-IO layers with the time from the `TimeProvider`, runs every timer, flushes outgoing datagrams and calls `INetworkObserver.OnUpdateCycle`.

`PeerThreading` chooses who runs the cycle (decision 11.6). In Background mode one dedicated loop thread per peer runs it, waiting on one reusable source signalled by the transports when a datagram arrives, by a `TimeProvider` timer 10 ms after the previous cycle as RakNet's `WaitOnEvent(10)` after each `RunUpdateCycle` (`RakPeer.cpp:6357-6358`), by commands, and by a cycle that queued a ping or pong where RakNet sets `quitAndDataEvents` to update again at once (the occasional ping, the ping answering `ID_NEW_INCOMING_CONNECTION` and `ID_CONNECTED_PONG`, `RakPeer.cpp:5926-5930, 6010-6013, 6088-6091`); so advancing a `FakeTimeProvider` wakes it, and there are no per-socket receive threads or hand-offs on the receive path. In Manual mode the peer creates no threads: `Update` runs one cycle on the caller, and calling it in Background mode throws.

Everything else falls into three groups:

- **Commands**, from any thread: sends, closes, connects, pings, advertisements, injections, receipt reservation, every setter, the ban and security exception lists, plugin attach and detach, and everything on `LowLevel`. They enqueue on a lock-free MPSC ring and never block.
- **Queries**, from any thread: properties, connection lookups, connection state and statistics. They read snapshots the cycle publishes (volatile fields, a sequence lock per statistics block, the copy-on-write `ConnectionList`) and may be one cycle old.
- **The dispatch step**: `Events.TryRead`, `Update` and shutdown's plugin step. One caller at a time, on any thread; an interlocked guard throws `InvalidOperationException` on concurrent use. Every `IRakPlugin` hook runs here, so plugin state touched only from hooks needs no locks. `INetworkObserver` hooks and the incoming datagram filter instead run in the I/O cycle and must not block.

The guard records the thread that holds it, and holds it only while a dispatch step runs, which includes every plugin hook but not the application's code between reads. Plugin attach, detach and `Inject` called from a plugin hook act immediately inside the current step; called from anywhere else they are applied at the start of the next one. A game that calls `Update` and drains `TryRead` once per frame therefore runs every plugin hook on its own thread, as RakNet runs plugins inside `Receive()`.

Continuations of the async members run on the thread pool with `RunContinuationsAsynchronously`, so user code never runs inside the I/O cycle, and no `SynchronizationContext` is captured.

## 9. Plugin dispatch

Plugins are written against the public API only. Everything a RakNet plugin reaches through `RakPeerInterface` is a public member of `RakPeer`, `RakConnection`, `RakPeerLowLevel` or `IPluginHost`, and built-in plugins get no special access.

RakNet keeps two plugin lists: plugins whose `UsesReliabilityLayer()` is false (`pluginListTS`) and those whose is true (`pluginListNTS`), each in attach order (`RakPeer.cpp:2760-2781`). Here a plugin that also implements `INetworkObserver` is in the second list and every other plugin is in the first (decision 11.7). Each dispatch step (one `TryRead` call) does what `RakPeer::Receive` does (`RakPeer.cpp:1528-1600`):

1. Release the previously borrowed event. Apply deferred attaches, detaches and injections.
2. Call `OnUpdate` on every plugin in the first list, then the second. This happens once per `TryRead` call, before anything is popped, including calls that return false.
3. Pop the next entry, or return false. `ReceiptAbandoned` entries skip steps 4 and 5 (decision 11.4).
4. Connection callbacks (`CallPluginCallbacks`, `RakPeer.cpp:6411-6461`), first list then second, keyed on `Data[0]`: `ID_DISCONNECTION_NOTIFICATION` and `ID_CONNECTION_LOST` call `OnDisconnected`, `ID_NEW_INCOMING_CONNECTION` and `ID_CONNECTION_REQUEST_ACCEPTED` call `OnConnected`, and the ten failure IDs call `OnConnectFailed`. An entry with no connection behind it, such as an injected `ID_NEW_INCOMING_CONNECTION`, still calls them, as RakNet passes the packet's own address and GUID: the hook gets a `RakConnection` that carries them, is in no lookup, and has `State == NotConnected`.
5. `OnEvent` on the first list in order, then the second. The first `Consume` stops the chain, releases the event (unless it was detached) and continues from step 3.
6. Return the event to the application.

`OnDisconnected` is raised only through step 4, so a local `Close` reaches plugins exactly when its packet is dispatched. Locally generated packets pass through the chain too (`RakPeer.cpp:1559-1562`), so a plugin that injects a message for other plugins (for example `ID_FCM2_NEW_HOST`) uses `host.Inject`.

`OnAttached` runs before any other hook of that plugin, on the dispatch step. Plugins listed in `RakPeerOptions.Plugins` are attached during `Start`, on the caller, before the loop exists, and receive `OnAttached` then `OnStartup`; a plugin attached later receives no `OnStartup`, as in RakNet, where `OnRakPeerStartup` runs only in `Startup`. `Detach` takes effect at the next dispatch step and calls `OnDetached` there (`RakPeer.cpp:2788-2818`). `OnInjected` runs when an injection is applied, before the entry is queued (`RakPeer.cpp:2826-2843`). `OnShutdown` runs once, first list then second (`RakPeer.cpp:1086-1093`). A plugin that implements `INetworkObserver` attached while the peer runs starts receiving observer hooks on the next I/O cycle. The plugin lists are copy-on-write arrays, so dispatch allocates nothing.

## 10. Mapping from `RakPeerInterface`

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
| `GetConnectionList` | `peer.Connections` (each `Address`), in `activeSystemList` order |
| `GetNextSendReceipt` / `IncrementNextSendReceipt` | `peer.PeekNextReceipt()` / `peer.ReserveReceipt()` |
| `Send(data, length, priority, reliability, channel, target, broadcast, forceReceipt)` | `peer.Send(message, target, reliability, priority, channel, forceReceipt)` or `connection.Send(...)`; `broadcast = true` → `peer.Broadcast(..., except: target)` |
| `Send(BitStream*, ...)` | the same calls with an `OutgoingMessage` from `BitStream` or `BitStreamWriter` (bit-exact length), or `Send<TState>` with a `MessageWriter` |
| `SendList` | `new OutgoingMessage(in ReadOnlySequence<byte>)` |
| `SendLoopback` | `peer.SendLoopback(span)`; arrives as `Message` from this peer's GUID, with `IsLocal` clear as RakNet's `AllocPacket` leaves it (`RakPeer.cpp:165`) |
| `Receive` | `peer.Events.TryRead(out PeerEvent)` (after `peer.Update()` in Manual mode); `WaitToReadAsync`; `ReadAllAsync` |
| `DeallocatePacket` | automatic at the next read; `OwnedPeerEvent.Dispose` for detached events |
| `GetMaximumNumberOfPeers` | `peer.MaxConnections` |
| `CloseConnection(target, notify, channel, priority)` | `peer.Close(target, notify, channel, priority)` / `connection.Close` / `CloseAsync` |
| `GetConnectionState` | `peer.GetConnectionState(AddressOrGuid)` (`Disconnected` for an address a closed slot held) / `connection.State` |
| `CancelConnectionAttempt` | `peer.CancelConnectionAttempt(address)`, or cancel the `ConnectAsync` token |
| `GetIndexFromSystemAddress` | `peer.TryGetConnection(address, out c)` → `c.SystemIndex` |
| `GetSystemAddressFromIndex` / `GetGUIDFromIndex` | `peer.TryGetConnectionByIndex(i, out c)` → `c.Address` / `c.RakNetGuid` |
| `GetSystemList` | `peer.Connections` (each `Address` and `RakNetGuid`) |
| `AddToBanList` / `RemoveFromBanList` / `ClearBanList` / `IsBanned` | `peer.BanList.Add(pattern, duration)` / `Remove` / `Clear` / `IsBanned` |
| `SetLimitIPConnectionFrequency` | `peer.LimitIpConnectionFrequency` |
| `Ping(SystemAddress)` | `connection.Ping()`; takes a receipt number, as RakNet's goes through `Send` |
| `Ping(host, port, onlyReplyOnAcceptingConnections, socketIndex)` | `peer.PingUnconnected(address, ...)` / `PingUnconnectedAsync(host, port, ...)`, answered by an `UnconnectedPong` event; or `peer.PingAsync(address, timeout)` |
| `GetAveragePing` / `GetLastPing` / `GetLowestPing` | `connection.AveragePing` / `LastPing` / `LowestPing` (`null` for -1) |
| `SetOccasionalPing` | `peer.OccasionalPing` |
| `GetClockDifferential` | `connection.ClockDifferential`; incoming `ID_TIMESTAMP` is already shifted (`PeerEvent.Timestamp`) |
| `SetOfflinePingResponse` / `GetOfflinePingResponse` | `peer.OfflinePingResponse` |
| `GetInternalID(UNASSIGNED, i)` / `GetInternalID(target, i)` | `peer.InternalAddresses[i]` / `connection.RemoteInternalAddresses[i]` |
| `SetInternalID` | `peer.SetInternalAddress(address, i)` |
| `GetExternalID(target)` | `connection.ExternalAddress`; `peer.ExternalAddress` for `UNASSIGNED` |
| `GetMyGUID` | `peer.RakNetGuid` |
| `GetMyBoundAddress(i)` | `peer.Sockets[i].LocalAddress` |
| `Get64BitUniqueRandomNumber` | `RandomSource.Shared.NextUInt64()` |
| `GetGuidFromSystemAddress` | `peer.GetGuidFromAddress(address)` (own GUID for `Unassigned`) |
| `GetSystemAddressFromGuid` | `peer.GetAddressFromGuid(rakNetGuid)` (`InternalAddresses[0]` for the own GUID) |
| `GetClientPublicKeyFromSystemAddress` | `connection.RemoteIdentityKey` |
| `SetTimeoutTime(ms, UNASSIGNED)` | `peer.DefaultTimeout` (default and every live connection) |
| `SetTimeoutTime(ms, target)` / `GetTimeoutTime(target)` | `connection.Timeout` |
| `GetMTUSize(target)` | `connection.Mtu`; for `UNASSIGNED` RakNet's `defaultMTUSize`, the smallest of `RakNetConstants.MtuSizes` (`RakPeer.cpp:206`, `2580`) |
| `GetNumberOfAddresses` / `GetLocalIP(i)` | `peer.LocalAddresses.Count` / `peer.LocalAddresses[i]`, the assigned head of `InternalAddresses` (ipList) |
| `IsLocalIP` | `peer.IsLocalAddress(IPAddress)` |
| `AllowConnectionResponseIPMigration` | `peer.AllowConnectionResponseIpMigration` |
| `AdvertiseSystem` | `peer.AdvertiseSystem(address, data, i)` / `AdvertiseSystemAsync(host, port, ...)`; received as `AdvertiseSystem` |
| `SetSplitMessageProgressInterval` / `GetSplitMessageProgressInterval` | `peer.SplitMessageProgressInterval`; received as `DownloadProgress` |
| `SetUnreliableTimeout` | `peer.UnreliableTimeout` |
| `SendTTL` | `peer.LowLevel.SendTtl(address, ttl, i)` |
| `AttachPlugin` / `DetachPlugin` | `peer.Plugins.Attach` / `Detach` (`IRakPlugin`; also implementing `INetworkObserver` puts it in the reliability-layer list) |
| `PushBackPacket(packet, pushAtHead)` | `peer.Inject(OwnedPeerEvent, atHead)` or `Inject(OwnedPeerEvent, address, rakNetGuid, atHead)` (consume), or `Inject(span, address, rakNetGuid, atHead)` (copies) |
| `ChangeSystemAddress(guid, address)` | `peer.LowLevel.ChangeSystemAddress(rakNetGuid, address)` |
| `AllocatePacket` | not needed: build the bytes with `BitStream` or `BitStreamWriter` and `Inject` them |
| `GetSocket(target)` | `connection.Socket`; `peer.Sockets[0]` for `UNASSIGNED` |
| `GetSockets` / `ReleaseSockets` | `peer.Sockets`; nothing to release (`IsOpen` becomes false at shutdown) |
| `WriteOutOfBandHeader` | `peer.LowLevel.WriteOutOfBandHeader(ref writer)`, or Wire `new OutOfBandHeader(peer.RakNetGuid).Write(ref writer)` |
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

`Packet` fields map to `PeerEvent` as: `data`/`length` → `Data`, `bitSize` → `BitLength`, `systemAddress` → `Address`, `guid` → `RakNetGuid`, `wasGeneratedLocally` → `IsLocal`.

### 10.1 `PluginInterface2`

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
| `OnReliabilityLayerNotification` | `INetworkObserver.OnReliabilityNotification`, with RakNet's message text, for the notifications RakNet raises without `LOG_TRIVIAL_NOTIFICATIONS` |
| `OnInternalPacket` | `INetworkObserver.OnFrame(in FrameInfo)`, on send, resend and receive, where ReliabilityLayer.cpp calls it |
| `OnAck` | `INetworkObserver.OnAck`, for each message number of an acknowledged datagram |
| `OnPushBackPacket` | `IRakPlugin.OnInjected` |
| `GetRakPeerInterface` / `SetRakPeerInterface` / `SetTCPInterface` | the `IPluginHost` passed to `OnAttached`; `host.Peer` |
| `GetMyGUIDUnified` | `host.RakNetGuid` |
| `SendUnified` / `SendListUnified` | `host.Send` / `host.Broadcast` with any `OutgoingMessage` |
| `AllocatePacketUnified` / `PushBackPacketUnified` / `DeallocPacketUnified` | `host.Inject` / `OwnedPeerEvent.Dispose`; `generatedLocally: true` for a packet whose `wasGeneratedLocally` the plugin sets |
| `packet->data[0] = id` in `OnReceive`, passing the packet on | `e.RewriteId(id)`, for a `Message` event that stays one |

### 10.2 `IPeerDriver` on the public API

| Driver call | API |
|---|---|
| `StartupAsync` | `RakPeer.Start(new() { MaxConnections, MaxIncomingConnections, Bindings = [new(endPoint)] })` |
| `ConnectAsync` | `peer.Connect(remote, options)`, reporting the `ConnectAttemptResult` name in RakNet's spelling |
| `SendAsync` | `peer.Send` / `Broadcast` → `Receipt.Value` |
| `CloseAsync` | `peer.Close(rakNetGuid, notify)` |
| `PingAsync` | `peer.PingUnconnected` |
| `SetOfflinePingResponseAsync` | `peer.OfflinePingResponse = data` |
| `GetConnectionStateAsync` | `peer.GetConnectionState(rakNetGuid)` |
| `ShutdownAsync` | `peer.ShutdownAsync(blockMs)` |
| `Packets` | a pump over `WaitToReadAsync`/`TryRead` that skips events with `IsRakNetPacket == false` and writes `ReceivedPacket` from `Data`, `RakNetGuid`, `Address`, `ReceivedTime` and `Timestamp` |

## 11. Decisions

### 11.1 Compatibility facade: provided, as the separate layer 4 assembly `RakDotNet.Compatibility`

`RakPeerInterface` (`src/RakDotNet.Compatibility/RakPeerInterface.cs`) holds one `RakPeer` and implements the mapping table in reverse, with RakNet's names and shapes: `Receive()` runs `Update()` in Manual mode, reads with `TryRead`, skips events with `IsRakNetPacket == false`, detaches the rest and returns a managed `Packet` class. `DeallocatePacket` disposes it, is idempotent, and is optional, because an undisposed packet is collected. Use after deallocation throws. As in RakNet, a `Packet`'s data, address and GUID are writable; `AllocatePacket` creates one to fill in, and `PushBackPacket` consumes the owned event of a received packet, with its edited address and GUID, so its bit length and `wasGeneratedLocally` survive as with RakNet's queued pointer, and copies an allocated one through `Inject`. `GetSocket` and `GetSockets` return `RakSocket`, and `ConnectWithSocket` takes one of the peer's own (decision 11.14). `SetUserUpdateThread` registers an `INetworkObserver` whose `OnUpdateCycle` runs the callback, and `RunUpdateCycle` runs `Update()`. Queries that RakNet answers from inactive `remoteSystemList` slots (`GetExternalID` and the ping queries) answer by address from the last connection the facade read on that slot until another connection takes it, never by GUID because closing a slot clears its guid, and nothing after `Shutdown`; RakNet's raw values are kept: 65535 from `GetLastPing` and `GetLowestPing` before the first pong, and the default from `GetTimeoutTime` for any target. Hostnames are `string`. RakNet's `StartupResult` and `ConnectionAttemptResult`, including `INVALID_PARAMETER`, are returned as RakNet returns them.

Reasons:

1. It is the cheapest migration path for code written against RakNet or Brutal.RakNet, and for porting RakNet's own samples and tests line by line.
2. It stays a thin adapter, as [plan.md](plan.md) intends, because `PeerEvent.Data` already holds RakNet's exact bytes, `ConnectAttemptResult` is returned synchronously by `Connect`, and synthetic events are flagged.
3. It does not bring back the pitfalls a faithful C++ shape would: there is no unmanaged memory to free, no double free, and no unterminated hostname.
4. Building it proves the mapping table is complete, because every row must compile against the public API.
5. Keeping it out of the core assembly keeps the core surface modern and small. The core never depends on it, no plugin may depend on it, and `RakDotNetPeerDriver` targets the public API directly so the interop matrix exercises the real surface.

### 11.2 Layer 2 (Peer): not public; it stays `internal`

Reasons:

1. Every real need for it is met by a small, stable public seam: custom I/O, socket adoption and simulation by `IDatagramTransport`; deterministic tests by `TimeProvider`, `PeerThreading.Manual` and `InMemoryNetwork`; raw traffic by `IncomingDatagramFilter`, `INetworkObserver` and `RakSocket.SendTo`; plugins by `IPluginHost` and `RakPeerLowLevel`.
2. Publishing it would freeze internals that the hardening, MTU, performance and security milestones must stay free to reshape: the connection table, the timer wheel, the ban and rate-limit structures, buffer ownership between layers, and the secure handshake states.
3. The sans-IO layers are single-threaded and trust their caller to sequence calls. A public layer 2 would need defensive checks on every call (hot-path cost) or would hand users a footgun.
4. Plugins must be implementable on the public API alone. Keeping layer 2 internal enforces that: a capability a plugin port needs is added to the public surface deliberately.

Layer 0 (Wire) stays public: its codecs are stable, checked against golden vectors, and needed for application and plugin payloads.

### 11.3 Local close produces RakNet's packets and no others

A local `Close` is reported by the packets RakNet itself enqueues: `ID_CONNECTION_LOST` immediately for a silent close of a connected system (`RakPeer.cpp:1667-1678`), and `ID_DISCONNECTION_NOTIFICATION` after the outgoing data drains for a notifying close (`RakPeer.cpp:5873-5903`). No extra `Disconnected` event is synthesised and there is no `ClosedByUser` reason, because RakNet's `LCR_CLOSED_BY_USER` callback is commented out (`RakPeer.cpp:1652-1663`). Reason: anything else would give a C# peer a different stream from a native one, against the interop pass criterion, and would make the facade drop real packets. `ClosedLocally` carries the extra knowledge without changing the stream.

### 11.4 Synthetic events are flagged, carry no RakNet bytes and skip plugins

The only event kind the C# peer adds is `ReceiptAbandoned`, needed so that every untracked receipt completes: RakNet reports nothing for a receipt whose connection ends, or whose unreliable message the unreliable timeout discards unsent (`ReliabilityLayer.cpp:1745-1793`). It has `IsRakNetPacket == false` and empty `Data`, so it can never be mistaken for `ID_SND_RECEIPT_LOSS`, which RakNet emits only for an unreliable message that was sent and not acknowledged within its retransmission timeout (`ReliabilityLayer.cpp:1860-1876`). It is not dispatched to plugins, because plugins are ports of RakNet code that observe RakNet's stream; RakNet plugins learn about lost connections from `OnClosedConnection`, which they still get. The facade and the interop driver filter on the flag.

### 11.5 The event is a `ref struct` and the reader is purpose-built

A borrowed event that is a copyable struct can be stored or carried across `await` with no compiler help. Making `PeerEvent` a `ref struct` moves the common misuse to compile time, and the generation check remains as the backstop. The cost is that `await foreach` cannot yield borrowed events, so `ReadAllAsync` yields owned ones; the allocation-free async pattern is `WaitToReadAsync` with an inner `TryRead` loop. For the same reason the reader is not a `ChannelReader<T>`: a read runs plugins and releases the previous event, which no `ChannelReader` member may do, and `TryPeek` could not be given a sound meaning.

### 11.6 Both a background loop and a manual cycle

`PeerThreading.Background` (the default) suits services: one loop thread, the stream consumed from any thread. `PeerThreading.Manual` suits game loops, which already own a thread and a frame, and tests, which become fully deterministic with `FakeTimeProvider`. Both run the same cycle code. The background loop has a single thread with no per-socket receive threads, so there is no hand-off on the receive path.

### 11.7 Plugin dispatch keeps RakNet's two lists

RakNet runs every hook on its non-reliability-layer plugins before its reliability-layer plugins (`RakPeer.cpp:1528-1598`). A single attach-ordered list would change the `OnEvent` order for a mixed set of plugins, so a plugin's list is determined by whether it implements `INetworkObserver`, matching `UsesReliabilityLayer()`. Every hook, `OnAttached` and `OnDetached` included, runs on the dispatch step so plugin state is never touched from two threads.

### 11.8 Async completions resolve when the outcome is produced

`ConnectAsync`, `CloseAsync`, `Closed` and `SendAndTrackAsync` complete when the I/O cycle produces the outcome, not when the consumer reads the matching event. Reason: a task that depends on the consumer turns a missing or slow reader into a deadlock. The consequence is that `await ConnectAsync` may resume before plugins have run `OnConnected` for that connection; an application that must send only after plugin setup sends from its handler for the `Connected` event instead. The event still appears on the stream at its ordered position.

### 11.9 Received bit length is preserved

`PeerEvent.BitLength` is RakNet's `Packet::bitSize`, which for connected messages is the bit length delivered by the reliability layer (`RakPeer.cpp:6046`, `RakPeer.cpp:6231`) and is whole bytes only for the connection-accepted message (`RakPeer.cpp:6186`). The send side keeps the bit-exact length that `Send(BitStream*)` uses (`RakPeer.cpp:1408`) through `OutgoingMessage`, because the encapsulated message length on the wire is in bits.

### 11.10 A peer starts once

`RakPeer.Start` returns an active peer and shutdown is final. RakNet allows `Startup` after `Shutdown`; supporting it would add "stopped but reusable" states to every member, and fixing server security at start depends on it. Creating a new peer gives the same capability.

### 11.11 Addresses are values; targets are unambiguous

`SystemAddress` is a value type in Wire so lookups and observer callbacks never allocate and never expose a reused `SocketAddress` whose validity ends with a call. `AddressOrGuid` is kept for the many ported call sites that target "a GUID or an address", but holds exactly one of them, which removes RakNet's precedence question.

### 11.12 Reliability is always explicit

Every send names its reliability, and there is no options struct whose `default` means `Unreliable` at `Immediate` priority. Reason: such a default is wrong for almost every caller and can only be caught by an analyzer.

### 11.13 Connection start, outcome and failure stay separate

`ConnectAttemptResult` (synchronous, RakNet's `ConnectionAttemptResult`), `ConnectFailure` (asynchronous, RakNet's failure IDs) and success (`ConnectResult.Connection`) are three separate facts. Reason: a single enum mixing "started" with "connected" and with failures is easy to misread, and the interop driver needs the synchronous value under its RakNet name.

### 11.14 `ConnectWithSocket` and server security are fixed at start

Adopting an externally created socket happens through `SocketBinding.Transport` when the peer starts, and `ConnectOptions.SocketIndex` selects it; there is no borrowing of another running peer's socket. Server security is set through `RakPeerOptions.Security`. Reason: both are rare, both reach into state the I/O cycle owns, and making them free-threaded at run time would require cross-peer routing and reconfiguration of live handshake state.

### 11.15 RakNet's release-build defaults

Defaults are those of a RakNet release build: `DefaultTimeout` 10 s (`RakPeer.cpp:249`), `UnreliableTimeout` 1000 ms (`RakPeer.cpp:238`), occasional ping off (`RakPeer.cpp:228`), `MaxIncomingConnections` 0 (`RakPeer.cpp:208`), 12 connection attempts 500 ms apart (`RakPeerInterface.h:139`). Reason: receipt-loss timing, timeout detection and admission must match a native peer configured the same way.

### 11.16 GUID members are named `RakNetGuid`

Properties and parameters that hold a GUID are named `RakNetGuid` and `rakNetGuid`, not `Guid`, as RakNet's `AddressOrGUID::rakNetGuid` is. Reason: a member named `Guid` reads as `System.Guid`, which is a different 128-bit type, and the analyzers reject it (CA1720).
