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
| 4 | Extras | Compatibility facade, plugins, protocol profiles (e.g. Bedrock) | via layer 3 |

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
- `BitStream` writes bits MSB-first within each byte. Multi-byte values are big-endian, except `uint24`, which is little-endian.
- Encapsulated message length is a `u16` in bits, not bytes.
- IPv4 `SystemAddress` is serialised as a version byte, the bitwise-NOT of the address, then the port.
- `OPEN_CONNECTION_REQUEST_1` is padded so the whole datagram equals the MTU being probed, minus UDP/IP headers (28 bytes). MTUs tried: 1492, 1200, 576.
- Ack-receipt reliability types are mapped to their base types before going on the wire.
- Sequenced messages carry a separate sequencing index in addition to the ordering index and channel.
- Datagram header flags: valid, ACK, NAK, packet pair, continuous send, needs B and AS. Parse the bits; don't match on byte values.

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

- `interop-peer`: a small C++ program linking RakNet's static library, controlled by line-delimited JSON on stdin and stdout. Commands: `startup`, `connect`, `send`, `close`, `ping`, `set_offline_ping_response`, `advertise`, `stats`, `shutdown`. Every received packet is reported as an event.
- `IPeerDriver`: one C# interface with `NativePeerDriver` (spawns `interop-peer`) and `RakDotNetPeerDriver`.
- Every scenario runs as a matrix: native-native (validates the harness and gives the baseline), C#-native, native-C#, C#-C#.
- A C# UDP proxy sits between the peers. It records datagrams (golden vectors for layer 0 and 1 tests) and injects seeded loss, reordering, duplication and delay.
- RakNet is fetched by CMake `FetchContent` pinned to upstream commit `1a169895a900c9fc4841c556e16514182b75faf8`, since `reference/` is not committed. RakNet's `cmake_minimum_required(VERSION 2.6)` needs `-DCMAKE_POLICY_VERSION_MINIMUM=3.5` under CMake 4, or a minimal CMake file of our own that compiles `Source/*.cpp`.

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

Each milestone has an exit criterion that is a passing test.

1. **Harness skeleton.** RakNet static library builds; `interop-peer` supports `startup`, `ping`, `set_offline_ping_response`; C# driver and proxy exist. Exit: unconnected ping/pong passes native-native, and the captured datagrams are saved as golden vectors.
2. **Wire layer.** `UInt24`, `BitStream`, offline messages, datagram header, encapsulated message, ACK ranges. Exit: every golden vector decodes and re-encodes byte-for-byte.
3. **Offline handshake.** OCR1/2 and replies, MTU discovery, error replies. Exit: C# client completes the offline handshake with a native server and vice versa.
4. **Session layer.** Reliability, ordering, sequencing, split, ACK/NAK, connected handshake, pings, sliding-window congestion control. Exit: the reliable ordered, big packet and timestamping scenarios pass all four matrix combinations through the lossy proxy.
5. **Transport and public API.** Socket loops, `RakPeer`, `RakConnection`, event stream, options, statistics. Exit: the full scenario table (excluding security) passes the matrix.
6. **Extras, as needed.** Compatibility facade, protocol profiles, plugins in priority order: ReplicaManager3, RPC4, FullyConnectedMesh2 + ConnectionGraph2, NatPunchthrough, FileListTransfer.

## Open questions

- Is a source-compatible facade for existing RakNet or Brutal.RakNet users needed? If so, it should be designed alongside milestone 5.
- Should layer 2 (Peer) be public for consumers with their own I/O, or internal until requested?
- Is RakNet security (`cat` library) wire compatibility required, or is a modern AEAD/DTLS acceptable?
- Which protocol profiles beyond stock RakNet 6? Bedrock 10/11 has no complete reference implementation and would need captures.
