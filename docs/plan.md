# RakDotNet plan

A modern C# implementation of the RakNet protocol, wire-compatible with the original C++ RakNet.

## Decisions

| Area | Decision |
|---|---|
| Wire protocol | Exact compatibility with C++ RakNet 4.081 (protocol version 6). Verified by interop tests, not by reading code. |
| Semantics | Keep RakNet's concepts: 8 reliability types, 4 priorities, 32 ordering channels, sequenced messages sharing the channel's ordering index, send receipts, message IDs with user IDs from `ID_USER_PACKET_ENUM`, `ID_TIMESTAMP`, offline ping response. |
| API shape | Modern .NET first. No 1:1 mirror of `RakPeerInterface`. A compatibility facade is optional and layered on top. |
| Target framework | net10.0. No netstandard, no Mono/legacy Unity. |
| Core design | Sans-IO: protocol and reliability logic take bytes and time as input and produce bytes, messages and events as output. No sockets or clocks inside. |
| Testing | Classic TDD, sociable tests. Golden vectors captured from native RakNet. Cross-implementation interop matrix. |
| Port source | C++ RakNet (BSD-3 + PATENTS) is the specification and the code we port from. |
| Other references | whirvis/jraknet (MIT): small helpers may be borrowed. RakNetty (GPLv3): design ideas only, no code. luyinquan/JRakNet: ignored (an early snapshot of JRakNet, superseded). Brutal.RakNet: API surface checklist and a second native peer. |

## Architecture

Each layer depends only on the layers below it.

| # | Layer | Responsibility | I/O |
|---|---|---|---|
| 0 | Wire | `BitStream` reader/writer, `UInt24`, offline magic, message IDs, per-message encode/decode, datagram header, encapsulated message, ACK/NAK range lists | none |
| 1 | Session | Per-connection state machine: reliability, ordering and sequencing, split and reassembly, ACK/NAK, sliding-window congestion control, RTO, connected handshake, pings | none; time is an input |
| 2 | Peer | Offline handshake, MTU discovery, connection table, ban list and rate limits, timer wheel; routes `(endpoint, bytes)` | none |
| 3 | Transport + API | `Socket` loops, `TimeProvider`, `Channel<T>`, `RakPeer`, `RakConnection`, `ConnectAsync`, ordered per-peer event stream, options, statistics | yes |
| 4 | Extras | Compatibility facade, plugins | via layer 3 |

Projects:

- `RakDotNet.Protocol`: layers 0 to 2, no dependencies. Layer boundaries are namespaces with `internal` visibility; split into assemblies only when needed.
- `RakDotNet`: layer 3.
- A test project per library, plus `RakDotNet.Interop.Tests`.
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
| `SecurityFunctionsTest`, Encryption | Only if RakNet security support is in scope |

## Milestones

Each milestone has an exit criterion that is a passing test. Milestones build a thin path through every layer first, then deepen it, so C# talks to native RakNet over a real socket as early as possible.

1. **Harness.** Native `interop-peer`, C# driver, recording and fault-injecting proxy, scenario matrix. Exit: native-native scenarios pass through the proxy, and golden vectors are captured.
2. **Wire layer.** `UInt24`, `BitStream`, message IDs, offline and connected control messages, datagram header, encapsulated message, ACK ranges. Exit: every golden vector decodes and re-encodes byte-for-byte, and decoded fields match the captured peers' values.
3. **Thin end-to-end path.** Offline handshake at a fixed MTU, connected handshake, reliable ordered delivery on one channel without loss, ACKs, disconnect. Minimal Session and Peer layers, a thin internal transport, and `RakDotNetPeerDriver`. Built serially with classic TDD. Exit: unconnected ping and connect/send/disconnect scenarios pass in all four matrix combinations.
4. **Deepening.** Independent items, each extending the working core and turning on its scenarios in all four combinations through the lossy proxy:
   - Loss: resends, NAKs, RTO and sliding-window congestion control (including sending ACKs immediately while RTT is unknown).
   - Split messages and reassembly.
   - Sequenced delivery, all 32 channels, priorities, send receipts.
   - Hardening limits and timeouts from the list above.
   - MTU discovery and rejection replies.
   - Connected pings, `ID_TIMESTAMP` and clock differential, timeout detection.
5. **Public API.** Designed alongside milestones 3 and 4 (several independent proposals judged against the Brutal.RakNet surface and the ordered event stream), implemented once the core is stable. `RakDotNetPeerDriver` moves onto it. Exit: the full scenario table (excluding security) passes against the public API.
6. **Performance.** BenchmarkDotNet suite and an allocation budget per datagram on the hot path, compared with RakNet's LoopbackPerformanceTest. Exit: benchmarks run in CI and the allocation budget is enforced by a test.
7. **Extras, as needed.** Compatibility facade, plugins in priority order: ReplicaManager3, RPC4, FullyConnectedMesh2 + ConnectionGraph2, NatPunchthrough, FileListTransfer.

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

## Open questions

- Is a source-compatible facade for existing RakNet or Brutal.RakNet users needed? If so, it should be designed alongside the public API.
- Should layer 2 (Peer) be public for consumers with their own I/O, or internal until requested?
- Is RakNet security (`cat` library) wire compatibility required, or is a modern AEAD/DTLS acceptable?
