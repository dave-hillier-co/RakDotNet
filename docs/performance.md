# Performance

How RakDotNet's performance is measured: the BenchmarkDotNet suite, the side-by-side comparison with native RakNet, the allocation budget tests, and what CI runs. This is milestone 6 of [plan.md](plan.md). Numbers depend on the machine, so none are kept here; run the commands to get them.

## Benchmark suite

`benchmarks/RakDotNet.Benchmarks` is a BenchmarkDotNet console project. Every class has `[MemoryDiagnoser]`, so each result shows the bytes allocated per operation next to the time.

```sh
dotnet run -c Release --project benchmarks/RakDotNet.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/RakDotNet.Benchmarks -- --filter '*ReliabilityLayer*' --job short
```

Without `--filter` it asks which benchmarks to run. Any BenchmarkDotNet option works after `--`; results are written to `BenchmarkDotNet.Artifacts/`. The process exits with a nonzero code when any benchmark fails to build or run.

| Class | Operation | Layer |
|---|---|---|
| `BitStreamBenchmarks` | Write or read one message of mixed-width fields, byte-aligned or one bit off | Wire |
| `DatagramBenchmarks` | Encode or decode one data datagram of 1 or 16 RELIABLE_ORDERED messages | Wire |
| `ReliabilityLayerBenchmarks` | `Send`: queue, update, frame and take the ACKs; `Receive`: take a datagram, deliver, update and frame the ACKs. One step of 1 message, or of 16 messages sharing a datagram | Session |
| `PeerCoreBenchmarks` | `ClientSendAndUpdate` and `ServerReceiveAndUpdate` for a connected pair, per step as above | Peer |
| `LoopbackBenchmarks` | One RELIABLE_ORDERED message from a client `RakPeer` to a server `RakPeer` over UDP on 127.0.0.1, in a burst of 256, in both threading modes, for 32 and 400 bytes (one datagram each) and 16000 bytes (split) | Transport and API |

`LoopbackBenchmarks` adds `Messages/s` and `MB/s` columns, computed from the mean time per message and the message size (1 MB is 10^6 bytes of payload).

The session and peer benchmarks measure one side of a connection at a time by replay. `SessionRecording` and `PeerRecording` run a live pair once and record every datagram each side sent at each step. A benchmark then drives one fresh side with the same sends and times, feeding it the other side's recorded datagrams. The layers are deterministic, so the replayed side does exactly what the live one did; the global setup checks this byte for byte and fails the benchmark if a replay diverges. When a recording runs out, the replayed side is replaced and the replay starts again, a cost amortised over 16384 operations.

## Comparison with native RakNet

RakNet's `Samples/LoopbackPerformanceTest` is interactive and runs three processes with a 100 ms sleep in its loop. `tools/interop-peer` builds `loopback-bench`, the same logic reduced to one process and a fixed burst: a client and a server `RakPeerInterface` connect over 127.0.0.1, the client sends `--messages` copies of a `--bytes` message at `HIGH_PRIORITY` on channel 0, and each round ends when the server has received the last one. It is compiled by the same CMake, against the same pinned RakNet and with the same flags as `interop-peer`.

`RakDotNet.Benchmarks loopback` runs the same test on two `RakPeer`s with the same arguments and prints the same line, so the two can be run one after the other:

```sh
RAKNET_SOURCE_DIR=reference/RakNet tools/interop-peer/build.sh
tools/interop-peer/build/loopback-bench --messages 100000 --bytes 400 --rounds 3
dotnet run -c Release --project benchmarks/RakDotNet.Benchmarks -- loopback --messages 100000 --bytes 400 --rounds 3
```

```text
RakNet loopback round=1 messages=100000 bytes=400 reliability=RELIABLE_ORDERED seconds=… messages/s=… MB/s=…
RakDotNet loopback round=1 messages=100000 bytes=400 reliability=RELIABLE_ORDERED seconds=… messages/s=… MB/s=…
```

| Argument | Default | Meaning |
|---|---|---|
| `--messages` | 100000 | Messages per round |
| `--bytes` | 400 | Bytes per message, the message ID included; above the MTU the message is split |
| `--reliability` | `RELIABLE_ORDERED` | `UNRELIABLE`, `UNRELIABLE_SEQUENCED`, `RELIABLE`, `RELIABLE_ORDERED` or `RELIABLE_SEQUENCED` |
| `--rounds` | 3 | Rounds on the same connection; the first includes warm-up |
| `--threading` | `background` | RakDotNet only: `background` or `manual` (the calling thread runs both I/O cycles) |

Compare like with like: run both on an idle machine, use the later rounds (the first ones include JIT tiering on the C# side; `--rounds 10` gives split messages time to settle), and compare small messages (`--bytes 400`) and split messages (`--bytes 16000 --messages 500`) separately. Unreliable modes may drop messages under load, and a round then never ends, so throughput comparisons use the reliable modes.

A native split-message round can also never end. A burst much larger than the socket receive buffer loses datagrams, the resent parts arrive out of order, and RakNet joins a split message's parts in arrival order ([plan.md](plan.md), wire format notes), so the reassembled message is corrupt and its message ID is not the one counted. Keep split bursts small enough to fit, around 500 messages of 16000 bytes, and stop a native run that stalls.

## Allocation budget

[api-design.md](api-design.md) section 1 requires the hot path to allocate nothing per message in steady state. Tests enforce it with a budget constant, `BudgetBytesPerMessage`, of 0 bytes per non-split message after warm-up, and a failure message that names the path and the bytes per message measured:

| Test | Path measured | Counter |
|---|---|---|
| `ReliabilityLayerAllocationTests` (`RakDotNet.Protocol.Tests`) | Two `ReliabilityLayer`s: send, update, receive, deliver and the ACK path, with and without an ack receipt | `GC.GetAllocatedBytesForCurrentThread` |
| `HotPathAllocationTests`, Manual mode (`RakDotNet.Tests`) | Two `RakPeer`s over loopback UDP, both cycles run on the test thread, with and without `ID_SND_RECEIPT_ACKED` | `GC.GetAllocatedBytesForCurrentThread` |
| `HotPathAllocationTests`, Background mode (`RakDotNet.Tests`) | Two `RakPeer`s over loopback UDP, each on its own loop thread | `GC.GetTotalAllocatedBytes(precise: true)`, in a collection that runs alone |

Each test charges everything allocated during the measured messages to those messages, idle cycles included, and divides by the message count. The Background test warms up and measures over ten times as many messages as the others: on the real clock the number of messages awaiting an ACK at once depends on thread scheduling, and the pools below grow to the largest number seen, so it passes once one of three measured windows is within the budget. The Manual tests run on a fake clock but over loopback UDP, where that number still depends on when each datagram becomes readable, so their warm-up includes a burst of 64 messages sent while the server is held back, which grows the pools well past the highest number the one-at-a-time transfer reaches. To find where a path allocates, run the matching benchmark: the `Allocated` column splits the cost by layer and by send or receive side.

### How the hot path avoids allocating

| Where | What is reused |
|---|---|
| `ReliabilityLayer.Send` | A pool of `OutgoingMessage`s (RakNet's `internalPacketPool`), each keeping its payload buffer and its resend and unreliable list nodes. A message returns to the pool when it is acknowledged, or once sent if unreliable. A split message's parts share one new copy of the message. |
| Framing (`WriteDatagram`, ACKs, NAKs) | Datagrams are written in place into a `DatagramQueue`, one buffer reused whenever the queue empties. `EncapsulatedMessage` and `AckRangeList` have allocation-free static writers and readers for the session's use. The datagram history keeps its message-number lists for reuse. |
| `ReliabilityLayer.Receive` | The datagram body is copied into a buffer reused per datagram and read without making `EncapsulatedMessage`s; the ACK and NAK ranges go into a reused list. Ordering and split reassembly copy what they hold. A delivered message is copied into a `MessageDelivered` that the consumer hands back with `Recycle`; a reassembled split message is passed on without a copy. Receipts are `ReceiptAcknowledged`s handed back the same way. |
| `PeerCore` | Datagrams for the transport go into its own `DatagramQueue` and are taken without a copy (`TryDequeueDatagram(out IPEndPoint, out ReadOnlySpan<byte>, out bool)`). Message and receipt events are `MessageReceived`s and `SendReceiptAcked`s that the consumer hands back with `Recycle`; the core recycles the session's events itself and copies the payloads of the connection events it keeps. Connected pings and pongs are encoded on the stack. |
| Statistics | `BpsTracker` adds pushes within 10 ms of each other to one entry, so its small preallocated buffer holds a second at any message rate. |

A consumer that never recycles keeps what it was given: an event that is not handed back is simply not reused. The tests in `RakDotNet.Protocol.Tests` read the layers this way, with the allocating `TryDequeueDatagram(out ...)` overloads that return arrays.

## Continuous integration

`.github/workflows/ci.yml` runs on `ubuntu-latest` and `macos-latest`:

1. Sets up .NET from `global.json`.
2. Fetches RakNet at the commit pinned in [plan.md](plan.md), builds `tools/interop-peer` (the peer, its `LIBCAT_SECURITY` twin `interop-peer-secure`, its logic tests and `loopback-bench`) and caches both.
3. Builds the solution and runs every test, with `RAKDOTNET_INTEROP_PEER` and `RAKDOTNET_INTEROP_PEER_SECURE` pointing at the native peers so the interop matrix, security scenarios included, runs.
4. Runs every benchmark with `--job short` and the loopback comparison with a small burst. These fail the build only when something errors; times are not checked.
5. Publishes `samples/RakDotNet.Samples.Loopback` with NativeAOT and runs it: the binary connects a client to a server over loopback, sends a message, echoes it and exits 0 once the echo arrives ([api-design.md](api-design.md) principle 9).

To run the NativeAOT step locally:

```sh
dotnet publish samples/RakDotNet.Samples.Loopback -c Release -p:PublishAot=true -o artifacts/aot
artifacts/aot/RakDotNet.Samples.Loopback
```
