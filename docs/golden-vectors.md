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
