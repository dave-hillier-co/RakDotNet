# Interop peer protocol

`tools/interop-peer` is a native process wrapping one C++ `RakPeerInterface`. Test drivers control it over stdin and stdout using JSON Lines: one UTF-8 JSON object per line, `\n` terminated. stderr is free-form diagnostic logging and is never parsed.

## Conventions

- Every command carries an integer `seq` chosen by the driver. The peer answers each command with exactly one reply carrying the same `seq`.
- Replies: `{"seq":1,"ok":true, ...result fields}` or `{"seq":1,"ok":false,"error":"<message>"}`.
- Asynchronous events never carry `seq`; they carry `event`.
- Replies and events are written atomically per line (the peer serialises stdout writes), and their relative order on stdout is the order they happened.
- Binary data is lowercase hex with no separators (`"data":"8601ff"`).
- GUIDs (`RakNetGUID.g`) are decimal strings, because JSON numbers lose precision above 2^53.
- Addresses are `"a.b.c.d:port"`.
- Enum values use RakNet's C++ names without prefixes: reliability `UNRELIABLE`, `UNRELIABLE_SEQUENCED`, `RELIABLE`, `RELIABLE_ORDERED`, `RELIABLE_SEQUENCED`, `UNRELIABLE_WITH_ACK_RECEIPT`, `RELIABLE_WITH_ACK_RECEIPT`, `RELIABLE_ORDERED_WITH_ACK_RECEIPT`; priority `IMMEDIATE_PRIORITY`, `HIGH_PRIORITY`, `MEDIUM_PRIORITY`, `LOW_PRIORITY`.
- Unknown fields are ignored. Missing optional fields take the defaults listed.
- On stdin EOF the peer shuts down (blocking 100 ms) and exits with code 0.

## Lifecycle

On launch the peer prints one event before reading commands:

```json
{"event":"ready","raknetVersion":"4.081","protocolVersion":6}
```

## Commands

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `startup` | `port` (0), `host` ("127.0.0.1"), `maxConnections` (8), `maxIncoming` (8) | `port` (actually bound), `guid` | `Startup`, `SetMaximumIncomingConnections` |
| `connect` | `host`, `port`, `password` (hex, ""), `attempts` (12), `attemptIntervalMs` (500), `timeoutMs` (0) | `result` (`ConnectionAttemptResult` name, e.g. `CONNECTION_ATTEMPT_STARTED`) | `Connect` |
| `send` | `target` (guid), `data` (hex, includes the message ID byte), `reliability` (`RELIABLE_ORDERED`), `priority` (`HIGH_PRIORITY`), `channel` (0), `broadcast` (false) | `receipt` (uint32) | `Send` |
| `close` | `target`, `notify` (true), `channel` (0) | | `CloseConnection` |
| `ping` | `host`, `port`, `onlyIfAccepting` (false) | `sent` (bool) | `Ping(host, port, ...)` (unconnected) |
| `pingConnected` | `target` | | `Ping(SystemAddress)` |
| `setOfflinePingResponse` | `data` (hex) | | `SetOfflinePingResponse` |
| `advertise` | `host`, `port`, `data` (hex, "") | `sent` (bool) | `AdvertiseSystem` |
| `setTimeout` | `ms`, `target` (optional; all systems when omitted) | | `SetTimeoutTime` |
| `setMaxIncoming` | `count` | | `SetMaximumIncomingConnections` |
| `ban` | `ip`, `ms` (0 = forever) | | `AddToBanList` |
| `connectionState` | `target` | `state` (`ConnectionState` name, e.g. `IS_CONNECTED`) | `GetConnectionState` |
| `stats` | `target` | `stats` object (see below) | `GetStatistics` |
| `info` | | `guid`, `boundAddress`, `mtu` per connection (`connections` array of `{guid,address,mtu,averagePing}`) | `GetMyGUID`, `GetMyBoundAddress`, `GetConnectionList`, `GetMTUSize`, `GetAveragePing` |
| `shutdown` | `blockMs` (100) | | `Shutdown`, then the process exits 0 after the reply |

`stats` object: `messagesInResendBuffer`, `bytesInResendBuffer`, `packetlossLastSecond`, `packetlossTotal`, `isLimitedByCongestionControl`, `bpsLimitByCongestionControl`, and `runningTotal` and `valueOverLastSecond` objects keyed by the `RNSPerSecondMetrics` names (`USER_MESSAGE_BYTES_PUSHED`, `USER_MESSAGE_BYTES_SENT`, `USER_MESSAGE_BYTES_RESENT`, `USER_MESSAGE_BYTES_RECEIVED_PROCESSED`, `USER_MESSAGE_BYTES_RECEIVED_IGNORED`, `ACTUAL_BYTES_SENT`, `ACTUAL_BYTES_RECEIVED`).

## Events

Every packet returned by `Receive()` is reported, including RakNet's internal notifications:

```json
{"event":"packet","messageId":134,"messageName":"ID_USER_PACKET_ENUM","guid":"1234567890","address":"127.0.0.1:61234","data":"86010203","timeMs":12345}
```

- `messageId` is `data[0]`, or `data[1+8]` when `data[0]` is `ID_TIMESTAMP` (then `timestamp` is also present, the big-endian `RakNet::Time` that follows it, as a decimal string).
- `messageName` is the C++ enum name for IDs below `ID_USER_PACKET_ENUM`, otherwise `"ID_USER_PACKET_ENUM+<n>"`.
- `data` is the whole packet, including the ID byte.
- `timeMs` is `RakNet::GetTimeMS()` when the packet was received.

Other events:

| `event` | Fields | When |
|---|---|---|
| `ready` | `raknetVersion`, `protocolVersion` | process start |
| `log` | `message` | optional diagnostics the driver may display |

## Example

```text
> {"seq":1,"cmd":"startup","port":0}
< {"seq":1,"ok":true,"port":61234,"guid":"1520003954185392640"}
> {"seq":2,"cmd":"setOfflinePingResponse","data":"68656c6c6f"}
< {"seq":2,"ok":true}
> {"seq":3,"cmd":"ping","host":"127.0.0.1","port":19132}
< {"seq":3,"ok":true,"sent":true}
< {"event":"packet","messageId":28,"messageName":"ID_UNCONNECTED_PONG","guid":"98765","address":"127.0.0.1:19132","data":"1c...","timeMs":4021}
```
