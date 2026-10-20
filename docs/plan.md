# RakDotNet plan

A modern C# implementation of the RakNet protocol, wire-compatible with the original C++ RakNet.

## Decisions

| Area | Decision |
|---|---|
| Scope | Full parity with RakNet 4.081: the core, the secure handshake and encryption (`LIBCAT_SECURITY`), and every feature enabled by default in `NativeFeatureIncludes.h` (listed under milestone 8). |
| Wire protocol | Exact compatibility with C++ RakNet 4.081 (protocol version 6). Verified by interop tests, not by reading code. |
| Semantics | Keep RakNet's concepts: 8 reliability types, 4 priorities, 32 ordering channels, sequenced messages sharing the channel's ordering index, send receipts, message IDs with user IDs from `ID_USER_PACKET_ENUM`, `ID_TIMESTAMP`, offline ping response. |
| API shape | Modern .NET first. No 1:1 mirror of `RakPeerInterface` in the core. A `RakPeerInterface`-shaped compatibility facade is provided as `RakDotNet.Compatibility` (layer 4), layered on the public API. |
| Target framework | net10.0. No netstandard, no Mono/legacy Unity. |
| Core design | Sans-IO: protocol and reliability logic take bytes and time as input and produce bytes, messages and events as output. No sockets or clocks inside. |
| Testing | Classic TDD, sociable tests. Golden vectors captured from native RakNet. Cross-implementation interop matrix. |
| Port source | C++ RakNet (BSD-3 + PATENTS) is the specification and the code we port from. [source-map.md](source-map.md) maps every RakNet `Source/` unit to its C# location and milestone. |
| Other references | whirvis/jraknet (MIT): small helpers may be borrowed. RakNetty (GPLv3): design ideas only, no code. luyinquan/JRakNet: ignored (an early snapshot of JRakNet, superseded). Brutal.RakNet: API surface checklist and a second native peer. |

## Architecture

Each layer depends only on the layers below it.

| # | Layer | Responsibility | I/O |
|---|---|---|---|
| 0 | Wire | `BitStream` reader/writer, `UInt24`, offline magic, message IDs, per-message encode/decode, datagram header, encapsulated message, ACK/NAK range lists | none |
| 1 | Session | Per-connection reliability state machine: ordering and sequencing, split and reassembly, ACK/NAK, sliding-window congestion control, RTO | none; time is an input |
| 2 | Peer | Offline and connected handshakes, connected pings, MTU discovery, connection table, ban list and rate limits, timer wheel; routes `(endpoint, bytes)` and produces the ordered event stream | none; time is an input |
| 3 | Transport + API | `Socket` loops, `TimeProvider`, `RakPeer`, `RakConnection`, `ConnectAsync`, ordered per-peer event stream, options, statistics, security configuration | yes |
| 4 | Plugins and utilities | Every RakNet plugin and utility, the compatibility facade, per-connection demultiplexing and test support, all built on the public API | via layer 3 |

Projects:

- `RakDotNet.Protocol`: layers 0 to 2, no dependencies. Layer 0 (Wire) is public. Layers 1 and 2 are namespaces with `internal` visibility; layer 2 stays internal so the public API is the only surface ([api-design.md](api-design.md), decision 11.2). The internal `Security` namespace beneath them is the port of cat (milestone 7).
- `RakDotNet`: layer 3, the public API, with `RakDotNet.Security` for key and connect-security types. Designed in [api-design.md](api-design.md).
- `RakDotNet.Connections`: layer 4, `ConnectionDemultiplexer`.
- `RakDotNet.Testing`: layer 4, `InMemoryNetwork` for sociable multi-peer tests.
- `RakDotNet.Compatibility`: layer 4, the `RakPeerInterface`-shaped facade. Nothing else depends on it.
- `RakDotNet.Plugins`: layer 4, RakNet's plugins and utilities on the public API, one folder per plugin.
- A test project per library, plus `RakDotNet.Interop.Tests`.
- `samples/`: console projects showing the public API in use.
- `tools/interop-peer`: native C++ test peer.

### Ordered event stream

Connection events (accepted, disconnected, lost, receipts) and user messages are delivered in one ordered stream per peer, as RakNet does. This preserves ordering guarantees applications depend on (the last message arrives before the disconnect) and keeps a `Receive()`-style compatibility facade a thin adapter.

## Wire format notes

Things that are easy to get wrong, from the C++ source:

- Offline magic: `00 FF FF 00 FE FE FE FE FD FD FD FD 12 34 56 78` (`RakPeer.cpp`).
- `BitStream` writes bits MSB-first within each byte. Multi-byte values are big-endian and are not byte-aligned first. `uint24` is the exception: it aligns to a byte boundary, then writes little-endian.
- IPv6 `SystemAddress` is serialised as a raw platform `sockaddr_in6`, whose layout and `AF_INET6` value differ between Windows, Linux and macOS. IPv6 interop needs per-platform captures before it can be supported.
- Encapsulated message length is a `u16` in bits, not bytes.
- IPv4 `SystemAddress` is serialised as a version byte, the bitwise-NOT of the address, then the port.
- `OPEN_CONNECTION_REQUEST_1` is padded so the whole datagram equals the MTU being probed, minus UDP/IP headers (28 bytes). MTUs tried: 1492, 1200, 576.
- Ack-receipt reliability types are mapped to their base types before going on the wire.
- Sequenced messages carry a separate sequencing index in addition to the ordering index and channel.
- Datagram header flags: valid, ACK, NAK, packet pair, continuous send, needs B and AS. Parse the bits; don't match on byte values.
- RakNet rewrites `ID_UNCONNECTED_PONG` before handing it to the application: the application sees the ID, a 4-byte big-endian time and the response data, with the GUID and magic removed (`RakPeer.cpp:4682-4700`). Code that presents packets the way RakNet does must apply the same rewrite.
- RakNet joins a split message's parts in the order they arrived, not by split index (`InsertIntoSplitPacketList` appends, `ReliabilityLayer.cpp:3141`; `BuildPacketFromSplitPacketList` copies in list order, `ReliabilityLayer.cpp:3197-3211`; `PREALLOCATE_LARGE_MESSAGES` is 0). A native receiver therefore corrupts a split message whose parts the network reordered. This is receive-side behaviour, not wire format: RakDotNet joins parts by split index.

## Hardening requirements

Every reference port missed at least one of these. Each needs a test.

- Unsigned 24-bit sequence numbers with wraparound-aware comparison.
- Bounded ACK/NAK range decoding (no expansion into per-number sets).
- Limits on split count, split size and concurrent split IDs, plus reassembly timeouts.
- Bounded ordering and receive buffers.
- RTT measured against the acknowledged datagram's own send time.
- Out-of-order datagrams within the window are accepted, not dropped.
- Every send's receipt or task completes or faults.
- Parse failures return a result; no exceptions on the hot path.
- Pooled buffers with clear ownership.

## Interop test harness

RakNet's own tests (`Samples/Tests`) run both peers in one process against RakNet itself, so they cannot test another implementation. The harness splits each scenario into two roles in separate processes.

- `tools/interop-peer`: a small C++ program linking RakNet's static library, controlled by JSON Lines on stdin and stdout as specified in [interop-protocol.md](interop-protocol.md). Every received packet is reported as an event.
- `IPeerDriver`: one C# interface with `NativePeerDriver` (spawns `interop-peer`) and `RakDotNetPeerDriver`.
- Every scenario runs as a matrix: native-native (validates the harness and gives the baseline), C#-native, native-C#, C#-C#.
- A C# UDP proxy sits between the peers. It records datagrams (golden vectors, see [golden-vectors.md](golden-vectors.md)) and injects seeded loss, reordering, duplication and delay.
- A C# NAT emulator puts peers behind full cone, address restricted, port restricted and symmetric routers on loopback, so NAT type detection and punchthrough run deterministically on one machine. A peer reaches its router through a gateway header ([interop-protocol.md](interop-protocol.md)): `interop-peer` replaces `sendto` and `recvfrom` for it, a RakDotNet peer uses a transport that adds it.
- RakNet is fetched by CMake `FetchContent` pinned to upstream commit `1a169895a900c9fc4841c556e16514182b75faf8`, since `reference/` is not committed. Our own CMake compiles `Source/*.cpp` directly; RakNet's CMake files are never configured.
- RakNet needs two compiler workarounds under modern clang, applied as flags without patching its sources: it is compiled as `gnu++98`, and with `-fno-strict-float-cast-overflow` because `CCRakNetSlidingWindow` casts `-1.0` to an unsigned integer as an "RTT unknown" sentinel, which clang otherwise compiles to a trap.

### Pass criterion

Interop is judged by behaviour, not by identical traffic. Every datagram must be wire-exact, but timing, batching and congestion-control decisions may differ from RakNet. A scenario passes when both peers observe the same message stream and connection events.

### Scenarios derived from RakNet samples

| Source | Scenario |
|---|---|
| Reliable Ordered Test, `ReliableOrderedConvertedTest` | Ordered and sequenced delivery on all channels under loss and reordering |
| BigPacketTest, MessageSizeTest | Split packets up to megabytes; payloads at the MTU boundary |
| BurstTest, Flow Control Test | Burst throughput, congestion window, bandwidth limit |
| Dropped Connection Test, `DroppedConnectionConvertTest` | Timeout detection and disconnect notification |
| OfflineMessagesTest, Ping, LANServerDiscovery | Unconnected ping/pong, offline ping response, `AdvertiseSystem` |
| Timestamping | `ID_TIMESTAMP` and clock differential |
| CrossConnectionTest, `CrossConnectionConvertTest` | Simultaneous connect in both directions |
| `MaximumConnectTest`, `ManyClientsOneServer*`, `PeerConnectDisconnect*` | Connection limits, `ID_NO_FREE_INCOMING_CONNECTIONS`, connect/cancel churn |
| `SystemAddressAndGuidTest`, `PacketAndLowLevelTestsTest` | Address and GUID handling, low-level packet cases |
| `SecurityFunctionsTest`, Encryption | Secure handshake, identity keys, security exceptions, encrypted delivery |
| MessageFilter | Filter sets on the server: allowed IDs, a change of set, kick and ban on a disallowed message, time limits, timestamped and RPC4 messages |
| ReadyEvent | Two and three systems waiting on each other: set, unset, forced completion, status by query, a disconnect leaving the wait list |
| TwoWayAuthentication | Challenges for a shared password in either direction, for a password the other system lacks, and with a different password |
| NATCompleteClient, NATCompleteServer: NAT type detection | A client behind no router and behind each kind of router is told its kind; a client that never runs the test is asked twice, then told symmetric |
| NATCompleteClient, NATCompleteServer: NAT punchthrough | Punchthrough between routers of each kind that allow it, then a connection through both; failure where they do not, with and without a retry; an unknown recipient, an unresponsive one, one that leaves the facilitator |
| NATCompleteClient, NATCompleteServer: UDP proxy | Clients behind symmetric routers, after punchthrough fails, connect through a proxy server that logs into a coordinator on the same peer; logins without, with the wrong and with the right password; no servers, an unknown recipient, a request in progress, every server busy |
| NATCompleteClient: Router2, Router2 | Routing through a router both systems are connected to, then a connection through it; rerouting through another when the router drops; no router connected to the endpoint |
| UDPForwarder | A connection through the forwarder's port; RakNet's results for invalid, existing and excess entries; an entry removed after its timeout |
| RelayPluginTest | Participants added on request when the server accepts them; a message to a participant by name; groups joined, messaged, listed and left, and left on disconnection |

## Milestones

Each milestone has an exit criterion that is a passing test. Milestones build a thin path through every layer first, then deepen it, so C# talks to native RakNet over a real socket as early as possible.

1. **Harness.** Native `interop-peer`, C# driver, recording and fault-injecting proxy, scenario matrix. Exit: native-native scenarios pass through the proxy, and golden vectors are captured.
2. **Wire layer.** `UInt24`, `BitStream`, message IDs, offline and connected control messages, datagram header, encapsulated message, ACK ranges. Exit: every golden vector decodes and re-encodes byte-for-byte, and decoded fields match the captured peers' values.
3. **Thin end-to-end path.** Offline handshake at a fixed MTU, connected handshake, reliable ordered delivery on one channel without loss, ACKs, disconnect. Minimal Session and Peer layers, a thin internal transport, and `RakDotNetPeerDriver`. Built serially with classic TDD. Exit: unconnected ping and connect/send/disconnect scenarios pass in all four matrix combinations.
4. **Deepening.** Three tracks run in parallel, each extending the working core and turning on scenarios in all four combinations through the lossy proxy:
   - **Session track**, serial in one branch because every item changes `ReliabilityLayer`: resends, NAKs, RTO and sliding-window congestion control (including sending ACKs immediately while RTT is unknown); then split messages and reassembly; then the hardening limits from the list above, choosing the `PeerLimits` values and bounding the receive ordering buffers.
   - **Peer track**: MTU discovery and rejection replies; connected pings, `ID_TIMESTAMP` and clock differential, timeout detection; ban list, `AdvertiseSystem`, connection limits and cross-connection.
   - **Scenarios track**: the remaining sample-derived scenarios in the harness, passing native-native first, each turned on for RakDotNet when its feature lands.

   Sequenced delivery, all 32 channels, priorities and send receipts are built in the core; their scenarios are turned on with the others. Exit: every non-security scenario in the table passes in all four matrix combinations through the lossy proxy.
5. **Public API.** The public API is designed as compiling code: [api-design.md](api-design.md) holds the rules, mappings and decisions, and the source and its XML comments hold the members. Implementation runs in parallel with milestone 4, on the stable core interfaces: `RakPeer` and the event stream, test-first against the stubs; then `RakDotNetPeerDriver` moves onto the public API, the internal transport is retired and command batching is added; then the compatibility facade, `ConnectionDemultiplexer` and `InMemoryNetwork`. Exit: every non-security scenario in the table passes against the public API.
6. **Performance.** BenchmarkDotNet suite and an allocation budget per datagram on the hot path, compared with RakNet's LoopbackPerformanceTest, as described in [performance.md](performance.md). Exit: benchmarks run in CI and the allocation budget is enforced by a test.
7. **Security.** RakNet's secure connection: cookie and public key in the offline handshake, challenge and answer, client identity keys, the security exception list, and authenticated encryption of every datagram. Port the parts of `DependentExtensions/cat` that RakNet uses (EasyHandshake, Tunnel key agreement, AuthenticatedEncryption and the primitives beneath them) so output is bit-exact. `interop-peer` gains a build with `LIBCAT_SECURITY=1`. Primitive tests use known-answer vectors computed by the native `cat` code; handshakes are randomised, so interop is checked by scenarios. Exit: `SecurityFunctionsTest`-derived and Encryption scenarios pass in all four matrix combinations.
8. **Plugins and utilities.** Each is ported on the public API, and `interop-peer` gains commands to drive the native counterpart. Exit for each: its sample-derived scenarios pass in all four matrix combinations. Order follows dependencies:
   1. Diagnostics, small plugins and utilities: PacketLogger, MessageFilter, StatisticsHistory, ReadyEvent, TwoWayAuthentication, RandSync.
   2. Object replication and RPC: NetworkIDManager, ReplicaManager3, RPC4Plugin.
   3. Topology and teams: ConnectionGraph2, FullyConnectedMesh, FullyConnectedMesh2, TeamManager, TeamBalancer.
   4. NAT traversal and relaying: NatPunchthroughClient/Server, NatTypeDetectionClient/Server, UDPProxyClient/Server/Coordinator, UDPForwarder, Router2, RelayPlugin.
   5. Files and cloud: FileOperations, FileListTransfer, DirectoryDeltaTransfer, CloudClient, CloudServer.
   6. TCP, HTTP and console: TCPInterface, PacketizedTCP, HTTPConnection, HTTPConnection2, ConsoleServer, RakNetTransport, TelnetTransport, LogCommandParser, RakNetCommandParser.
   7. External services: EmailSender, DynDNS, Rackspace.

## Platforms and CI

- Golden vectors must be captured on Linux as well as macOS before release, and on Windows if IPv6 is in scope (IPv6 addresses are serialised in a platform-specific layout).
- CI builds the native peer, which needs network access to fetch RakNet or a cached copy, then runs the full matrix.

## Working in parallel

Parallel work runs as waves of agents, each in its own git worktree and branch:

1. Before a wave, contracts are written and committed: interfaces, formats, and an ownership list naming which item creates each shared type, so no two items define the same concept.
2. Each item is built, then independently reviewed against the C++ source and the contracts. Reviewers report only confirmed findings with evidence.
3. Every finding is triaged: fixed on the branch, or declined with a reason. None are dropped by rule.
4. Branches are merged one at a time, and the full suite including native scenarios runs after each merge.

Work that is tightly coupled or design-heavy (such as the first pass through the Session layer) is done serially rather than split across agents.
