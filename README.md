# RakDotNet

A modern C# implementation of the RakNet protocol, wire-compatible with RakNet 4.081.

RakDotNet implements RakNet's wire protocol and its plugins for .NET 10, without the C++ library. A RakDotNet peer and a native RakNet peer can connect to each other, exchange every reliability type and run the same plugins. The protocol, reliability and peer layers are sans-IO: bytes and time go in, bytes, messages and events come out. Sockets and clocks are supplied by the host, so the protocol is deterministic in tests and applications can drive the loop themselves.

## Features

- RakNet 4.081 wire format: offline and connected handshakes, MTU discovery, the eight reliability types, 32 ordering channels, sequenced messages, split messages, ACK/NAK, sliding-window congestion control, send receipts, connected pings, bans, connection limits and `ID_TIMESTAMP`.
- Secure connections: the `LIBCAT_SECURITY` handshake, identity keys, cookie jar and authenticated encryption, ported bit-exactly from the cat code RakNet compiles.
- One ordered event stream per peer: connection events, messages, receipts and offline replies in RakNet's order. `RakPeer` runs a background loop or updates on the calling thread.
- A `RakPeerInterface`-shaped compatibility facade in `RakDotNet.Compatibility`.
- Every plugin enabled by default in RakNet's `NativeFeatureIncludes.h`, including loggers and diagnostics, message filtering, ready events, two-way authentication, random synchronisation, object replication and RPC, connection graphs and meshes, teams, NAT traversal and relaying, files and cloud, TCP and HTTP, the console and its parsers, and the external services.
- No allocations per message in the steady-state hot path; trimming and NativeAOT compatible. CI publishes a sample with NativeAOT.
- Tested against the C++ implementation: golden vectors captured from native RakNet, and an interop matrix that runs each scenario native-native, C#-native, native-C# and C#-C# through a lossy proxy and a NAT emulator.

## Getting started

RakDotNet targets .NET 10. Clone the repository and build the solution:

```sh
git clone https://github.com/dave-hillier-co/RakDotNet.git
cd RakDotNet
dotnet build RakDotNet.slnx -c Release
```

Run the managed tests:

```sh
dotnet test --solution RakDotNet.slnx -c Release
```

The interop tests run a native RakNet peer as a separate process. Build it first; the native build needs CMake and a C++ compiler. The first build fetches RakNet at the commit pinned in [docs/plan.md](docs/plan.md) and its JSON dependency, or uses a local RakNet checkout when `RAKNET_SOURCE_DIR` is set:

```sh
tools/interop-peer/build.sh

RAKDOTNET_INTEROP_PEER=$PWD/tools/interop-peer/build/interop-peer \
RAKDOTNET_INTEROP_PEER_SECURE=$PWD/tools/interop-peer/build/interop-peer-secure \
dotnet test --solution RakDotNet.slnx -c Release
```

Without the native binaries, the native half of the interop matrix skips.

## Example

This starts a server and a client in one process over loopback UDP, sends a message and prints the echo. Add a project reference to `src/RakDotNet/RakDotNet.csproj` to run it.

```csharp
using System.Net;
using System.Text;
using RakDotNet;
using RakDotNet.Protocol.Wire;

// Start a server and a client in one process, over loopback UDP.
await using var server = RakPeer.Start(new RakPeerOptions
{
    MaxConnections = 1,
    MaxIncomingConnections = 1,
    Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
});
await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 1 });

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var serving = RunServerAsync(server, stop.Token);

var connection = (await client.ConnectAsync(server.Sockets[0].LocalAddress)).EnsureConnected();

var payload = new byte[1 + Encoding.UTF8.GetByteCount("hello")];
payload[0] = (byte)MessageId.UserPacketEnum;
Encoding.UTF8.GetBytes("hello", payload.AsSpan(1));
connection.Send(payload, Reliability.ReliableOrdered);

while (await client.Events.WaitToReadAsync(stop.Token))
{
    while (client.Events.TryRead(out var e))
    {
        if (e.Kind == PeerEventKind.Message && e.Id == MessageId.UserPacketEnum)
        {
            Console.WriteLine($"Echo: {Encoding.UTF8.GetString(e.Body)}");
            await stop.CancelAsync();
            try { await serving; } catch (OperationCanceledException) { }
            return;
        }
    }
}

static async Task RunServerAsync(RakPeer server, CancellationToken cancellationToken)
{
    while (await server.Events.WaitToReadAsync(cancellationToken))
    {
        while (server.Events.TryRead(out var e))
        {
            if (e.Kind == PeerEventKind.Message && e.Id == MessageId.UserPacketEnum)
                e.Connection!.Send(e.Data, Reliability.ReliableOrdered);
        }
    }
}
```

The full samples are in [`samples/`](samples): a server loop, a client, manual update mode, and the loopback example above with cancellation and logging.

## Projects

| Project | Contents |
|---|---|
| `RakDotNet.Protocol` | Layers 0 to 2: wire types and codecs, the reliability layer and the peer core. The wire layer is public; session and peer are internal. |
| `RakDotNet` | Layer 3: `RakPeer`, `RakConnection`, the event stream, options, statistics and security types. |
| `RakDotNet.Plugins` | RakNet's plugins and utilities, built on the public API. |
| `RakDotNet.Compatibility` | A `RakPeerInterface`-shaped facade. |
| `RakDotNet.Connections` | Per-connection demultiplexing. |
| `RakDotNet.Testing` | `InMemoryNetwork`, for multi-peer tests without sockets. |
| `samples/` | Runnable console samples. |
| `benchmarks/` | BenchmarkDotNet suite and the native loopback comparison. |
| `tools/interop-peer/` | The native C++ peer used by the interop tests. |

## Documentation

- [docs/plan.md](docs/plan.md) — scope, architecture and the interop harness.
- [docs/api-design.md](docs/api-design.md) — the public API's principles and decisions.
- [docs/source-map.md](docs/source-map.md) — every RakNet `Source/` unit and its C# counterpart.
- [docs/interop-protocol.md](docs/interop-protocol.md) — the native peer's JSON Lines protocol.
- [docs/performance.md](docs/performance.md) — benchmarks and the allocation budget.
- [docs/golden-vectors.md](docs/golden-vectors.md) — the captured wire vectors and known-answer tests.
- [docs/conventions.md](docs/conventions.md) — code and test conventions.

## Provenance

RakDotNet is a port of RakNet 4.081. RakNet is distributed under the BSD 3-Clause license with the PATENTS file; the C++ source is the specification for this port. [docs/source-map.md](docs/source-map.md) maps every `Source/` unit to its C# location.
