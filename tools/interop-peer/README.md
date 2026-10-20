# interop-peer

A native process wrapping one C++ RakNet 4.081 `RakPeerInterface`, driven by JSON Lines on stdin and stdout. Interop tests use it as the reference peer. The protocol is specified in [docs/interop-protocol.md](../../docs/interop-protocol.md).

## Build

Requires CMake 3.24 or later and a C++17 compiler.

```sh
tools/interop-peer/build.sh
```

The binary is written to `tools/interop-peer/build/interop-peer`. RakNet is fetched from GitHub at a pinned commit and nlohmann/json from a pinned release. To build from a local RakNet checkout without fetching it:

```sh
RAKNET_SOURCE_DIR=/path/to/RakNet tools/interop-peer/build.sh
```

RakNet's own CMake files are not used; `CMakeLists.txt` compiles `Source/*.cpp` into a static library. Build fixes for RakNet are compiler flags only, each explained in `CMakeLists.txt`.

## Tests

```sh
ctest --test-dir tools/interop-peer/build
```

These cover the translation logic (hex, enum names, packet events). The full peer is exercised by `RakDotNet.Interop.Tests`.

## Usage

```text
$ tools/interop-peer/build/interop-peer
{"event":"ready","raknetVersion":"4.081","protocolVersion":6}
{"seq":1,"cmd":"startup","port":0}
{"seq":1,"ok":true,"port":61234,"guid":"1520003954185392640"}
```

- stdout carries only protocol lines. Diagnostics, including anything RakNet prints, go to stderr.
- `timeMs` in packet events is `RakNet::GetTimeMS()`, which counts from process start, so it is only comparable within one peer.
- Closing stdin shuts the peer down (blocking 100 ms) and exits with code 0.

## loopback-bench

The same build produces `tools/interop-peer/build/loopback-bench`, RakNet's `Samples/LoopbackPerformanceTest` as one unattended process, used to compare native RakNet with RakDotNet. Its arguments and output are described in [docs/performance.md](../../docs/performance.md).

```sh
tools/interop-peer/build/loopback-bench --messages 100000 --bytes 400 --rounds 3
```
