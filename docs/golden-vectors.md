# Golden vectors

Golden vectors are real datagrams captured between native RakNet peers. Wire-layer tests decode them and re-encode them byte-for-byte, so the C# codecs are checked against what RakNet actually sends rather than against our reading of its source.

## Location

`tests/vectors/<scenario>.json`, one file per captured scenario. Test projects reference the folder as content copied to the output directory.

## Format

```json
{
  "scenario": "unconnected-ping",
  "description": "Native client pings a native server with an offline ping response of 'hello'.",
  "source": "interop-peer, RakNet 1a169895a900c9fc4841c556e16514182b75faf8",
  "captured": "2026-10-18T19:00:00Z",
  "peers": {
    "a": { "role": "client", "guid": "1520003954185392640" },
    "b": { "role": "server", "guid": "98765" }
  },
  "datagrams": [
    { "index": 0, "timeMs": 0, "from": "a", "to": "b", "hex": "01..." },
    { "index": 1, "timeMs": 1, "from": "b", "to": "a", "hex": "1c..." }
  ]
}
```

- `timeMs` is milliseconds since the first captured datagram.
- `from` and `to` are keys of `peers`; endpoints are not recorded because ports change between runs. Where an address appears inside a payload, tests compare it to whatever the decoder reads rather than to a fixed value.
- `hex` is lowercase with no separators.
- Captures are produced by the C# UDP proxy (`tests/RakDotNet.Interop.Tests`) and committed. Recapturing is an explicit act, never part of a normal test run.

## Using vectors in tests

Each test names the vector and datagram index it checks, and asserts on decoded fields that are stable across captures (message IDs, magic, protocol version, MTU, payload text). Values that vary per run (GUIDs, times, ports) are checked against the `peers` block or for shape only.

## Known-answer vectors for the cat primitives

The secure handshake is randomised, so it cannot be captured as datagrams and replayed. The cat code RakNet compiles with `LIBCAT_SECURITY=1` is checked against known answers instead, computed by native cat.

- `tools/cat-vectors` is a C++ program built with CMake against the pinned RakNet, compiling cat through `Source/SecureHandshake.cpp` exactly as RakNet does. `tools/cat-vectors/build.sh` builds it and writes `tests/vectors/cat/<primitive>.json`. Set `RAKNET_SOURCE_DIR` to build from a local checkout. Regenerating is an explicit act, and the output is deterministic, so a rerun leaves the files unchanged.
- Inputs come from SplitMix64 streams with fixed seeds and are written into the vectors. Where cat draws from its CSPRNG, the tool substitutes a recording `FortunaOutput`, and the vector holds every byte drawn, in order; the C# tests replay them through `ISecureRandom`.
- The files sit in a subfolder so the scenario tests, which read every `tests/vectors/*.json`, do not see them. `tests/RakDotNet.Protocol.Tests/Security` reads them from the source tree.

| File | Contents |
|---|---|
| `skein.json` | Scripts of Skein-256 calls (key, MAC, KDF, chained keys, split crunches) and every output. |
| `hmac-md5.json` | HMAC-MD5 keyed from Skein: cached inner and outer states and MACs over message lengths around the block boundaries. |
| `chacha.json` | ChaCha keystreams XORed into inputs of 0 to 300 bytes. |
| `cookie-jar.json` | Cookie hashes of addresses of 0 to 48 bytes. |
| `curve.json` | Curve constants, key pairs, scalar multiplication with 6- and 8-bit windows, simultaneous multiplication, cofactor doubling, point validation, multiplication modulo q and field inversion. |
| `handshake.json` | Complete anonymous and two-way handshakes with the derived keys of both sides and encrypted traffic (in-order, replayed, reordered, tampered, beyond the replay window); 103 challenges to one server across an ephemeral rekey, rejected challenges and answers; IV reconstruction. |
