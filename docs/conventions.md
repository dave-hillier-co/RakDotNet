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
- `src/RakDotNet/`: layer 3, public API.
- Test folders mirror source folders.

## Tests

- xUnit v3. Test names are sentences with underscores.
- Classic TDD: real collaborators, no mocking frameworks. Fakes are hand-written and only stand in for I/O or time.
- Expected bytes either come from a golden vector or are derived from the C++ source, with a comment saying which.
- Time is injected (`TimeProvider`, or an explicit `long nowMs` parameter in sans-IO code), never read from the system clock in layers 0 to 2.
