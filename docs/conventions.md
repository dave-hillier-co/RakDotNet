# Code conventions

## Wire codecs

- Bit-level encoding goes through `BitStreamWriter` and `BitStreamReader` in `RakDotNet.Protocol.Wire`. Do not hand-roll bit packing elsewhere.
- Each wire structure is a `readonly record struct` (or a record class if it holds variable-length data) with a pair of members:

  ```csharp
  public void Write(ref BitStreamWriter writer);
  public static bool TryRead(ref BitStreamReader reader, out T value);
  ```

  `TryRead` returns false on malformed or truncated input and never throws for bad data. Argument exceptions are reserved for programming errors.
- Structures that start with a message ID byte write and check it themselves; `TryRead` returns false if the ID does not match.
- Every field cites its C++ source as a comment where the encoding is non-obvious, e.g. `// RakPeer.cpp:5164 - server GUID follows the magic`.
- Variable-length payloads are exposed as `ReadOnlyMemory<byte>` on decoded records; `TryRead` may slice the input buffer rather than copying.
- Limits from the hardening requirements in [plan.md](plan.md) are enforced in `TryRead` and each has a test.

## Layout

- `src/RakDotNet.Protocol/Wire/`: layer 0. Public.
- `src/RakDotNet.Protocol/Session/`: layer 1. Internal, visible to tests and `RakDotNet`.
- `src/RakDotNet.Protocol/Peer/`: layer 2. Internal, visible to tests and `RakDotNet`.
- `src/RakDotNet/`: layer 3, public API, namespace `RakDotNet`.
- `src/RakDotNet/Security/`: layer 3 key and connect-security types, namespace `RakDotNet.Security`.
- `src/RakDotNet/Transport/`: the socket and update loops that run a `PeerCore`. Internal, visible to tests.
- `src/RakDotNet.Testing/`: test support for applications, such as `InMemoryNetwork`. Public.
- `src/RakDotNet.Connections/`: layer 4 per-connection demultiplexing. Public.
- `src/RakDotNet.Compatibility/`: layer 4 `RakPeerInterface`-shaped facade. Public; no plugin depends on it.
- `samples/`: console projects showing the public API in use, one per usage pattern, built with the solution. They reference only public assemblies.
- `benchmarks/`: the BenchmarkDotNet suite and the loopback comparison with native RakNet ([performance.md](performance.md)).
- Test folders mirror source folders.

## Shared types

Each concept has one definition. RakNet enums and wire types (`Reliability`, `Priority`, `MessageId`, `UInt24`) live in `RakDotNet.Protocol.Wire` and are reused by the harness and every layer rather than redeclared.

## Tests

- xUnit v3. Test names are sentences with underscores.
- Classic TDD: real collaborators, no mocking frameworks. Fakes are hand-written and only stand in for I/O or time.
- Expected bytes come from a golden vector, from known answers printed by native RakNet code (`tools/bitstream-vectors` for the BitStream encodings), or are derived from the C++ source, with a comment saying which.
- Round-trip tests only prove nothing is lost. Every codec also needs tests asserting decoded field values against known inputs: exact bytes derived from the C++, or the peers' values recorded in a golden vector.
- Time is injected (`TimeProvider`, or an explicit `long nowUs` parameter in sans-IO code), never read from the system clock in layers 0 to 2. Sans-IO time is in microseconds because RakNet's congestion control works in microseconds (`CCTimeType`); `RakNet::Time` values written on the wire are that time in milliseconds.

## Source map

- New ported code updates [source-map.md](source-map.md): add or adjust the row for each RakNet unit the code ports, with its C# location, milestone and a one-line note.
