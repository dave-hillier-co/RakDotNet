# RakDotNet public API design

This document defines the public API of RakDotNet: layer 3 of [plan.md](plan.md) (namespace and assembly `RakDotNet`), the layer 0 types it adds to `RakDotNet.Protocol.Wire`, and the layer 4 packages that sit directly on it. It is binding for the implementation, for `RakDotNetPeerDriver` and for every plugin port.

The API is declared as compiling code. Signatures, and the semantics of each member, are in the source and its XML doc comments; this document gives the principles, the rules that span types, the mappings from RakNet, and the decisions with their reasons.

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
7. **RakNet names where they carry meaning.** `RakNetGuid`, `SystemAddress`, `AddressOrGuid`, `ConnectionState`, `RakNetStatistics`, `BitStream`, the `ConnectAttemptResult` and failure names, and the plugin hook names stay recognisable, so porting is mostly mechanical.
8. **Escape hatches are grouped.** Out-of-band sends, raw datagrams, TTL, datagram filtering, address migration, the network simulator and observers live under `peer.LowLevel`, which keeps the main surface small.
9. **No reflection.** Trimming and NativeAOT are supported with analyzers as errors.

## 3. Assemblies and namespaces

| Assembly | Namespace | Contents | Source |
|---|---|---|---|
| `RakDotNet.Protocol` | `RakDotNet.Protocol.Wire` | Layer 0, public: `Reliability`, `Priority`, `MessageId`, `UInt24`, `BitStreamReader`, `BitStreamWriter`, `RakNetGuid`, `SystemAddress`, `BitStream`, `BitStreamEncodings`, `BitSerializer`; in `.Messages`, the message codecs, `RakNetConstants`, `TimestampHeader`, `OutOfBandHeader` | `src/RakDotNet.Protocol/Wire/` |
| `RakDotNet.Protocol` | `.Session`, `.Peer` | Layers 1 and 2, `internal` (decision 18.2) | `src/RakDotNet.Protocol/Session/`, `Peer/` |
| `RakDotNet` | `RakDotNet` | Layer 3: everything in sections 4 to 14 except security | `src/RakDotNet/` |
| `RakDotNet` | `RakDotNet.Security` | Key and connect-security types (section 13) | `src/RakDotNet/Security/` |
| `RakDotNet.Testing` | `RakDotNet.Testing` | `InMemoryNetwork`, a datagram transport fake for sociable multi-peer tests (section 16) | `src/RakDotNet.Testing/` |
| `RakDotNet.Connections` | `RakDotNet.Connections` | Layer 4: `ConnectionDemultiplexer` (section 6.4) | `src/RakDotNet.Connections/` |
| `RakDotNet.Compatibility` | `RakDotNet.Compatibility` | Layer 4: the `RakPeerInterface`-shaped facade (decision 18.1) | `src/RakDotNet.Compatibility/` |

Wire types are reused everywhere and never redeclared, as [conventions.md](conventions.md) requires. Layer 2 has internal types named `PeerEvent` and `ConnectionState`; code in a `RakDotNet.*` namespace that uses them qualifies them as `Protocol.Peer.PeerEvent`, because the public types of the same name in `RakDotNet` take precedence over a `using` there.

## 4. Identity and addressing

`RakNetGuid` (`src/RakDotNet.Protocol/Wire/RakNetGuid.cs`) and `SystemAddress` (`src/RakDotNet.Protocol/Wire/SystemAddress.cs`) are values in Wire, so lookups and observer callbacks never allocate (decision 18.11). `SystemAddress` holds an IPv4 or IPv6 address and port inline; it converts from `IPEndPoint` implicitly and back with `ToIPEndPoint`, and copies to and from a `SocketAddress` without allocating on the send and receive paths. `default(SystemAddress)` is `Unassigned`, IPv4 `0.0.0.0:0`, as RakNet's default `SystemAddress` is. `default(RakNetGuid)` is GUID 0, which RakNet treats as assigned but never generates. Both format as the interop protocol writes them, and as RakNet's `ToString` for the unassigned values.

`AddressOrGuid` (`src/RakDotNet/AddressOrGuid.cs`) holds exactly one of a GUID or an address. `default` is undefined, which is what `except: default` means on a broadcast. Converting an unassigned GUID or address gives an undefined target.

`BitStreamWriter.WriteAddress(SystemAddress)` sits beside the `IPEndPoint` overload. The reader's counterpart is `TryReadSystemAddress`, not a `TryReadAddress` overload, because an overload on the `out` type would make every `TryReadAddress(out var a)` ambiguous. IPv6 serialisation follows the platform `sockaddr_in6` caveat in [plan.md](plan.md); until per-platform captures exist, an IPv6 binding fails at start with `StartupError.SocketFamilyNotSupported`.

Hostnames are `string` and are resolved with `Dns.GetHostAddressesAsync`. No public member takes a hostname as bytes.

## 5. Main types

- **`RakPeer`** (`src/RakDotNet/RakPeer.cs`) is `RakPeerInterface`. A peer starts once: `Start` either returns an active peer or throws, so there is no "constructed but not started" state. After shutdown every command throws `ObjectDisposedException` and every query returns its last value. To start again, create a new peer (decision 18.10).
- **`RakConnection`** (`src/RakDotNet/RakConnection.cs`) mirrors one entry in RakNet's remote system list and is created when that entry is assigned: for an outgoing attempt when the offline handshake completes and the attempt moves into the connection table, for an incoming one when the request is accepted. Before that an outgoing attempt is visible only as `GetConnectionState(address) == Pending` and its `ConnectAsync` task. A `RakConnection` is never reused, so a stale reference cannot address a new peer and object identity is safe as a dictionary key; after the connection ends it stays readable with `State == Disconnected`. In a cross-connection RakNet handles the second `ID_CONNECTION_REQUEST` on the existing entry (`RakPeer.cpp:5981-5988`), so the API promises one object per entry and nothing more; the `CrossConnectionTest`-derived interop scenario defines which events each side observes.
- **`ConnectionList`** (`src/RakDotNet/ConnectionList.cs`) is an immutable snapshot, rebuilt only when a connection is added or removed, with a struct enumerator.
- **`RakSocket`** (`src/RakDotNet/RakSocket.cs`) is one binding. The underlying `Socket` is never handed out: its receive side belongs to the peer, and a second reader would steal datagrams.
- **`RakPeerLowLevel`** (`src/RakDotNet/RakPeerLowLevel.cs`) is `peer.LowLevel`, for NAT, relay and diagnostic plugins.

## 6. The event stream

### 6.1 Events

`PeerEvent` (`src/RakDotNet/PeerEvent.cs`) is a borrowed `ref struct` view of one stream entry: a pooled segment plus the generation it was issued with. `PeerEventKind` (`src/RakDotNet/PeerEventKind.cs`) is a pure function of `Data[0]`, as RakNet applications switch on it, except for `ReceiptAbandoned`. A packet that starts with `ID_TIMESTAMP` is a `Message`. Kind-specific members return `default` when the kind does not match.

`Data` holds exactly the bytes of RakNet's `Packet::data`, including RakNet's rewrites of unconnected pongs, advertisements, out-of-band messages, timestamps and receipts; the remarks on `PeerEvent` list them with their citations. `BitLength` is `Packet::bitSize`, which can end mid-byte (decision 18.9).

Internal traffic never reaches the stream: connected pings and pongs, unconnected pings, handshake messages and `ID_DETECT_LOST_CONNECTIONS`. Network observers can see it (section 11.4).

### 6.2 Reading

`PeerEventReader` (`src/RakDotNet/PeerEventReader.cs`) is `peer.Events`. It is purpose-built rather than a `ChannelReader<T>`, because a read runs the plugin chain and releases the previous borrowed event, and it has no `TryPeek` (decision 18.5). `TryRead` is one dispatch step (section 11.2). `WaitToReadAsync` completes synchronously when an event is queued and otherwise awaits one reusable value-task source; while plugins are attached it also wakes every `PluginUpdateInterval` so `OnUpdate` keeps running. `ReadAllAsync` yields owned events for consumers that must `await` while holding one.

A server on the background loop, allocation-free with borrowed events:

```csharp
await using var server = RakPeer.Start(new RakPeerOptions
{
    MaxConnections = 64,
    MaxIncomingConnections = 64,
    Bindings = [SocketBinding.Port(19132)],
});

while (await server.Events.WaitToReadAsync(ct))
    while (server.Events.TryRead(out var e))
    {
        switch (e.Kind)
        {
            case PeerEventKind.Connected:
                e.Connection!.UserState = new Player();
                break;
            case PeerEventKind.Message when e.Id == GameIds.Move:
                var r = e.GetBodyReader();
                if (r.TryReadSingle(out var x) && r.TryReadSingle(out var y))
                    Move(e.Connection!, x, y);
                break;
            case PeerEventKind.Disconnected:
                Remove(e.Connection!, e.DisconnectReason);
                break;
        }
    }
```

A consumer that awaits per event uses `await foreach (var owned in peer.Events.ReadAllAsync(ct))` and disposes each item.

### 6.3 Ordering guarantees

- Entries are delivered in the order the I/O cycle produced them, which is RakNet's `packetReturnQueue` order. Per-channel ordering and sequencing hold within the stream.
- `Connected` precedes that connection's first message. A connection's last delivered message precedes its `Disconnected`, and nothing for that connection follows it.
- A receipt event follows the send that produced it. `ReceiptAbandoned` events for a connection precede its `Disconnected` when it has one.
- `Inject(..., atHead: true)` places the entry so the next `TryRead` returns it, as `PushBackPacket(packet, true)` (`RakPeer.cpp:2838-2841`).
- Commands from one thread are applied in call order.

### 6.4 `ConnectionDemultiplexer` (layer 4)

`ConnectionDemultiplexer` (`src/RakDotNet.Connections/ConnectionDemultiplexer.cs`) is for servers structured per connection. It is built on the public API only and becomes the single reader of `peer.Events`, so plugins run on its pump. Events cross threads there, so each one is detached and per-connection consumers dispose them. Order is kept within each connection, not between connections. The default overflow policy is `Wait`, because it never drops or closes anything the application did not ask for; its head-of-line cost is stated on the option so it is a visible choice.

## 7. Ownership and lifetime

1. **Borrowed by default.** An event returned by `TryRead` is valid until the next `TryRead` on that peer, until `Update` in Manual mode, or until shutdown. An event passed to a plugin hook is valid for the duration of the call. Nothing needs freeing.
2. **The compiler enforces the common case.** `PeerEvent`, `UnconnectedPongView`, `DownloadProgressView` and `FrameInfo` are `ref struct`s: they cannot be stored in fields, captured by lambdas, boxed or held across `await`.
3. **Stale access throws.** Every `PeerEvent` member compares its generation with the segment's. After release, access throws `ObjectDisposedException`; reused memory is never read. A `ReadOnlySpan<byte>` already taken from `Data` is not tracked, and must not be used after the next read.
4. **Detach to keep.** `e.Detach()` returns an `OwnedPeerEvent` (`src/RakDotNet/OwnedPeerEvent.cs`) and removes the segment from automatic release. Detaching twice throws `InvalidOperationException`. The owner calls `Dispose()`, or passes it to `peer.Inject(owned, atHead)`, which consumes it. Release is an `Interlocked.CompareExchange` on the generation, so disposing a copy or disposing twice never frees someone else's reuse of the segment.
5. **Leaks are bounded.** An owned event that is never disposed is collected by the GC. The pool misses once and allocates a replacement, and `peer.Pools.Misses` counts it. No native memory is involved.
6. **Sending never takes ownership.** Every `Send` copies the caller's bytes into a pooled segment, or lets the caller write straight into it (`MessageWriter<TState>`), before returning. The caller's buffer is free when `Send` returns.
7. **Injecting copies unless handed ownership.** `Inject(span, ...)` copies. `Inject(OwnedPeerEvent, ...)` takes the segment without copying.
8. **Settings are copied.** `OfflinePingResponse`, `IncomingPassword`, `ConnectOptions.Password` and key material are copied on assignment. Getters return library-owned read-only memory.
9. **Shutdown** releases every borrowed event and discards undelivered entries, as RakNet's `Shutdown` does (`RakPeer.cpp:1176-1181`). Owned events stay valid until disposed.

## 8. Connections

### 8.1 Options and results

`ConnectOptions` (`src/RakDotNet/ConnectOptions.cs`) carries the arguments of RakNet's `Connect` after the host and port, with RakNet's defaults. `ConnectResult.cs` declares the three facts about an attempt, kept separate (decision 18.13): `ConnectAttemptResult` (the synchronous answer, RakNet's `ConnectionAttemptResult`), `ConnectFailure` (RakNet's failure IDs, `RakPeer.cpp:6429-6458`) and `ConnectResult`, which `ConnectAsync` returns. RakNet's `INVALID_PARAMETER` is an `ArgumentException`, and a stopped peer throws `ObjectDisposedException`.

A client:

```csharp
await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 1 });

var result = await client.ConnectAsync("game.example.com", 19132, cancellationToken: ct);
var server = result.EnsureConnected();   // throws RakConnectException carrying the result

Span<byte> buffer = stackalloc byte[16];
var w = new BitStreamWriter(buffer);
new TimestampHeader(client.GetTime()).Write(ref w);
w.WriteByte((byte)GameIds.Move);
w.WriteSingle(x);
w.WriteSingle(y);
server.Send(w, Reliability.UnreliableSequenced, Priority.High, orderingChannel: 1);
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
- **`ConnectAsync`** resolves a host name asynchronously (RakNet resolves on the caller, blocking), then behaves as `Connect`. It completes when the I/O cycle enqueues the `Connected` or `ConnectFailed` event, with the same outcome; the event still appears on the stream at its position (decision 18.8). A synchronous rejection completes it immediately with `AttemptResult` set. Cancelling the token throws `OperationCanceledException` and stops the attempt: while it is still in the request queue by `CancelConnectionAttempt`, and once it has an entry in the connection table by a silent close, which for a system that is not yet connected produces no event.
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

Pending operations end: `ConnectAsync`, `PingAsync` and the `*Async` host overloads throw `OperationCanceledException`; `SendAndTrackAsync` completes with `DeliveryStatus.PeerShutdown`; `Closed` tasks complete. No `Disconnected` or other event is added to the stream for connections ended by shutdown, because RakNet clears its queue (`RakPeer.cpp:1177-1181`). For an untracked receipt still outstanding at shutdown, the completion of `Events.Completion` is its completion. `Dispose` is `ShutdownAsync(TimeSpan.Zero)` run synchronously, and `DisposeAsync` uses `RakPeerOptions.ShutdownBlockDuration`; both are idempotent.

## 9. Sending

### 9.1 Shape

Every send takes an `OutgoingMessage` (`src/RakDotNet/OutgoingMessage.cs`), a `ref struct` that one parameter type uses to cover `Send(char*)`, `Send(BitStream*)` with its bit-exact length, and `SendList`. It converts implicitly from spans, arrays, `BitStreamWriter` and `BitStream`. The `Send<TState>` overloads take a `MessageWriter<TState>` that writes straight into the pooled outgoing segment. Results are in `src/RakDotNet/SendResult.cs`: `SendReceipt`, `SendStatus`, `SendResult`, and for tracked sends `DeliveryStatus` and `DeliveryOutcome`.

Reliability is a required parameter on every send. Priority defaults to `High` and the ordering channel to 0. There is no options struct whose `default` would silently mean `Unreliable` at `Immediate` priority (decision 18.12).

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

`PeerThreading` (`src/RakDotNet/PeerThreading.cs`) selects the mode (decision 18.6).

**The I/O cycle**, in both modes: drain commands; receive datagrams without blocking until the transport is empty or the event queue is full; feed the sans-IO layers with `now` from the `TimeProvider`; run timers (resends, ACK/NAK, pings, timeouts, connection retries, ban expiry, split reassembly timeouts, receipt timeouts); flush outgoing datagrams; call `INetworkObserver.OnUpdateCycle`.

**Background mode** waits between cycles on one reusable value-task source that is signalled by the transports when a datagram arrives, by an `ITimer` from `TimeProvider.CreateTimer` at the next deadline, and by commands on an idle-to-busy transition or an `Immediate` send. `FakeTimeProvider.Advance` therefore wakes it. There are no per-socket receive threads and no hand-off between threads on the receive path.

**Manual mode** creates no threads. `Update` runs one cycle on the caller. It is part of the dispatch step, so it cannot run concurrently with `TryRead`. Calling `Update` in Background mode throws `InvalidOperationException`. A game loop:

```csharp
using var peer = RakPeer.Start(new RakPeerOptions { MaxConnections = 8, Threading = PeerThreading.Manual });

// once per frame
peer.Update();
while (peer.Events.TryRead(out var e))
    Handle(e);
```

**Calls from inside plugin hooks.** The guard records the thread that holds it, and it is held only while a dispatch step runs, which includes every plugin hook but not the application's code between reads. `Plugins.Attach/Detach` and `Inject` called from a plugin hook act immediately, inside the current step. Called from anywhere else, they are applied at the start of the next dispatch step. Either way every plugin hook runs on the dispatch step.

A game that calls `Update` and drains `TryRead` once per frame therefore runs every plugin hook on the game thread, as RakNet runs plugins inside `Receive()`.

## 11. Plugin model

Plugins are written against the public API only. Everything a RakNet plugin reaches through `RakPeerInterface` is a public member of `RakPeer`, `RakConnection`, `RakPeerLowLevel` or `IPluginHost`. Built-in plugins get no special access.

### 11.1 Types

`IRakPlugin` and `PluginResult` (`src/RakDotNet/IRakPlugin.cs`) are `PluginInterface2` and `PluginReceiveResult`; every hook has a default no-op body. `IPluginHost` (`src/RakDotNet/IPluginHost.cs`) is what a plugin may call, independent of host, replacing the C++ `*Unified` helpers: `RakPeer` implements it, and so will `TcpInterface` (milestone 8.6). `PluginCollection` (`src/RakDotNet/PluginCollection.cs`) is `peer.Plugins`. `PeerEvent` is a `ref struct`, so a plugin cannot keep the event it is given without `Detach()`.

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

### 11.4 Network observers

`INetworkObserver` and `FrameInfo` (`src/RakDotNet/INetworkObserver.cs`) are the reliability-layer hooks (`UsesReliabilityLayer() == true`). They run in the I/O cycle and must not block. Addresses are passed as `SystemAddress` values, so nothing an observer receives is invalidated when the callback returns, other than the spans. Observers that are not plugins attach through `peer.LowLevel.Observers`. PacketLogger uses `OnFrame` and the datagram hooks, and prints what RakNet's PacketLogger prints.

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

`RakPeerOptions` (`src/RakDotNet/RakPeerOptions.cs`) holds the arguments of `Startup` and the initial runtime settings, with RakNet's release-build defaults (decision 18.15). `SocketBinding` (`src/RakDotNet/SocketBinding.cs`) is `SocketDescriptor`. `PeerLimits` (`src/RakDotNet/PeerLimits.cs`) holds the hardening limits of [plan.md](plan.md), whose defaults accept everything stock RakNet sends. `NetworkSimulatorOptions` and `RandomSource` are in their own files.

**Event queue backpressure.** When the stream holds `EventQueueCapacity` entries, the I/O cycle stops reading datagrams until the consumer drains it below half. The kernel then queues or drops datagrams and the reliability layer recovers them. Locally generated control entries (disconnects, receipts, connection failures) use reserved headroom above the cap, so they are never dropped or reordered. Memory stays bounded and nothing delivered is discarded.

**Statistics.** `RakNetStatistics` (`src/RakDotNet/RakNetStatistics.cs`) is a value with fixed-size `[InlineArray]` fields, so a snapshot is copied without arrays or allocation. The I/O cycle publishes each connection's statistics block under a sequence lock every cycle; readers copy it without blocking the cycle. The field set covers the interop `stats` command, so `RakDotNetPeerDriver` maps it field for field. Peer-wide totals are also published through `System.Diagnostics.Metrics` (`Meter("RakDotNet")`, observable instruments over the snapshots).

## 13. Security configuration

The key and connect-security types are in `src/RakDotNet/Security/`. The admission lists, `AddressPatternCollection` (the security exception list) and `BanList`, are in `src/RakDotNet/AdmissionLists.cs`.

- **Server security is fixed at start** through `RakPeerOptions.Security`. RakNet accepts `InitializeSecurity` and `DisableSecurity` only while the peer is stopped (`RakPeer.cpp:765-766`), and a `RakPeer` is never stopped and then reused, so the options are the only place it can be set. Key material is validated when the `RakKeyPair` is created, so `Start` cannot fail on it.
- **The security exception list** is mutable at run time and applies to incoming connections only.
- **Client security** is `ConnectOptions.Security`. An invalid client configuration returns `ConnectAttemptResult.SecurityInitializationFailed` (`RakPeer.cpp:3296-3298`). Handshake rejections arrive as `RemoteSystemRequiresPublicKey`, `OurSystemRequiresSecurity` or `PublicKeyMismatch`.
- **Identity.** `connection.IsSecure` reports whether the link is encrypted; `connection.RemoteIdentityKey` is the client's key in two-way mode.
- **Encryption** of every datagram is invisible to the API, except that `Mtu` accounts for the authenticated-encryption overhead.
- **Key material** lives in pinned arrays zeroed on dispose.
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

The exceptions and `StartupError` are in `src/RakDotNet/Exceptions.cs`. `RAKNET_STARTED` is `Start` returning. `RAKNET_ALREADY_STARTED` cannot happen because a peer starts once. `INVALID_SOCKET_DESCRIPTORS` and `INVALID_MAX_CONNECTIONS` are argument validation. `PORT_CANNOT_BE_ZERO` applies only to console platforms RakDotNet does not target. `COULD_NOT_GENERATE_GUID` (`RakPeer.cpp:382`) cannot happen because the GUID is redrawn until it is neither 0 nor unassigned.

## 15. BitStream for application payloads

Bit packing stays in `RakDotNet.Protocol.Wire`, as [conventions.md](conventions.md) requires.

- `BitStreamWriter(Span<byte>, int bitPosition)` resumes a writer at a bit position, so a writer can be recreated from a stored position.
- `BitStream` (`src/RakDotNet.Protocol/Wire/BitStream.cs`) is a growable, poolable stream over `ArrayPool<byte>.Shared`. A `BitStreamWriter` is a `ref struct` and cannot be held by a class, so the stream keeps a bit position and hands out writers with `BeginWrite`/`EndWrite`; every `Write*` forwards through them and the stream never packs bits itself.
- `TimestampHeader` and `OutOfBandHeader` (`src/RakDotNet.Protocol/Wire/Messages/ApplicationHeaders.cs`) are codecs for the `ID_TIMESTAMP` prefix and `WriteOutOfBandHeader`.
- The RakNet encodings that application and plugin payloads use (`WriteCompressed`, `WriteBitsFromIntegerRange`, `WriteFloat16`, `WriteNormVector`, `WriteVector`, `WriteNormQuat`, `WriteOrthMatrix`, `WriteDelta`, `WriteCasted`, `WriteRakString`, `WriteCompressedString` and their readers) are extension methods on `ref BitStreamWriter` and `ref BitStreamReader` in `src/RakDotNet.Protocol/Wire/BitStreamEncodings.cs`, so they work for stack buffers and `BitStream` alike. Integer encodings are generic over `IBinaryInteger<T>`; vectors use `System.Numerics`. Each encoding is checked against native output with golden vectors.
- `BitSerializer`, in the same file, covers RakNet's `Serialize(bool writeToBitstream, ...)` idiom. A `ref struct` cannot hold a `ref` to another `ref struct`, so it owns its writer or reader by value and returns the advanced copy.

Received payloads are read with `e.GetBodyReader()`, positioned after the ID and any timestamp header, with `e.Timestamp` already in local time. User message IDs are `(MessageId)((byte)MessageId.UserPacketEnum + n)`.

## 16. Testability, allocation and AOT

### 16.1 Deterministic tests

- Every timer reads `RakPeerOptions.TimeProvider`: retransmits, RTO, pings, timeouts, connection attempts, ban expiry, split reassembly, receipt timeouts and statistics. Layers 0 to 2 take time as input.
- `PeerThreading.Manual` plus `FakeTimeProvider` gives fully deterministic runs: a test advances the clock, calls `Update` on each peer and asserts on `TryRead`.
- The datagram transport is the only I/O seam: `IDatagramTransport` and `IDatagramTransportFactory` (`src/RakDotNet/IDatagramTransport.cs`), with `UdpTransportFactory` as the default. Every operation is non-blocking, and the transport signals the peer when a datagram becomes available.
- `RakDotNet.Testing.InMemoryNetwork` (`src/RakDotNet.Testing/InMemoryNetwork.cs`) is a hand-written fake for I/O, with seeded loss, delay, duplication and reordering scheduled on the peers' `TimeProvider`. Several real `RakPeer`s then run sociable tests in one process with no sockets. The same seam carries RakNet over other datagram media and implements the network simulator as a decorator.
- `RakPeerOptions.RakNetGuid` and `RandomSource.FromSeed` make GUIDs reproducible.

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
| `GetSystemAddressFromIndex` / `GetGUIDFromIndex` | `peer.TryGetConnectionByIndex(i, out c)` → `c.Address` / `c.RakNetGuid` |
| `GetSystemList` | `peer.Connections` (each `Address` and `RakNetGuid`) |
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
| `GetMyGUID` | `peer.RakNetGuid` |
| `GetMyBoundAddress(i)` | `peer.Sockets[i].LocalAddress` |
| `Get64BitUniqueRandomNumber` | `RandomSource.Shared.NextUInt64()` |
| `GetGuidFromSystemAddress` | `peer.GetGuidFromAddress(address)` (own GUID for `Unassigned`) |
| `GetSystemAddressFromGuid` | `peer.GetAddressFromGuid(rakNetGuid)` |
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
| `PushBackPacket(packet, pushAtHead)` | `peer.Inject(OwnedPeerEvent, atHead)` (consumes) or `Inject(span, address, rakNetGuid, atHead)` (copies) |
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
| `GetMyGUIDUnified` | `host.RakNetGuid` |
| `SendUnified` / `SendListUnified` | `host.Send` / `host.Broadcast` with any `OutgoingMessage` |
| `AllocatePacketUnified` / `PushBackPacketUnified` / `DeallocPacketUnified` | `host.Inject` / `OwnedPeerEvent.Dispose` |

### 17.2 `IPeerDriver` on the public API

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

## 18. Decisions

### 18.1 Compatibility facade: provided, as the separate layer 4 assembly `RakDotNet.Compatibility`

`RakPeerInterface` (`src/RakDotNet.Compatibility/RakPeerInterface.cs`) holds one `RakPeer` and implements the mapping table in reverse, with RakNet's names and shapes: `Receive()` runs `Update()` in Manual mode, reads with `TryRead`, skips events with `IsRakNetPacket == false`, detaches the rest and returns a managed `Packet` class. `DeallocatePacket` disposes it, is idempotent, and is optional, because an undisposed packet is collected. Use after deallocation throws. `PushBackPacket` consumes the owned event. Hostnames are `string`. RakNet's `StartupResult` and `ConnectionAttemptResult`, including `INVALID_PARAMETER`, are returned as RakNet returns them.

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

### 18.16 GUID members are named `RakNetGuid`

Properties and parameters that hold a GUID are named `RakNetGuid` and `rakNetGuid`, not `Guid`, as RakNet's `AddressOrGUID::rakNetGuid` is. Reason: a member named `Guid` reads as `System.Guid`, which is a different 128-bit type, and the analyzers reject it (CA1720).
