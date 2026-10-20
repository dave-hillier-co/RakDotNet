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
| `sendList` | `parts` (array of non-empty hex strings, sent joined as one message), then the fields of `send` except `data` | `receipt` (uint32) | `SendList` |
| `close` | `target`, `notify` (true), `channel` (0) | | `CloseConnection` |
| `cancelConnect` | `host`, `port` | | `CancelConnectionAttempt` |
| `ping` | `host`, `port`, `onlyIfAccepting` (false) | `sent` (bool) | `Ping(host, port, ...)` (unconnected) |
| `pingConnected` | `target` | | `Ping(SystemAddress)` |
| `setOfflinePingResponse` | `data` (hex) | | `SetOfflinePingResponse` |
| `advertise` | `host`, `port`, `data` (hex, "") | `sent` (bool) | `AdvertiseSystem` |
| `setTimeout` | `ms`, `target` (optional; all systems when omitted) | | `SetTimeoutTime` |
| `setMaxIncoming` | `count` | | `SetMaximumIncomingConnections` |
| `setOccasionalPing` | `enabled` | | `SetOccasionalPing` |
| `setSplitMessageProgressInterval` | `interval` (0 = never) | | `SetSplitMessageProgressInterval` |
| `setOutgoingBandwidthLimit` | `bitsPerSecond` (0 = unlimited) | | `SetPerConnectionOutgoingBandwidthLimit` |
| `ban` | `ip`, `ms` (0 = forever) | | `AddToBanList` |
| `connectionState` | `target` | `state` (`ConnectionState` name, e.g. `IS_CONNECTED`) | `GetConnectionState` |
| `stats` | `target` | `stats` object (see below) | `GetStatistics` |
| `info` | | `guid`, `boundAddress`, and `connections`, an array of `{guid,address,mtu,averagePing,lastPing,lowestPing,externalAddress}` | `GetMyGUID`, `GetMyBoundAddress`, `GetConnectionList`, `GetGuidFromSystemAddress`, `GetMTUSize`, `GetAveragePing`, `GetLastPing`, `GetLowestPing`, `GetExternalID` |
| `time` | | `time` (decimal string) | `RakNet::GetTime`, the clock `ID_TIMESTAMP` values are written in |
| `shutdown` | `blockMs` (100) | | `Shutdown`, then the process exits 0 after the reply |

`stats` object: `messagesInResendBuffer`, `bytesInResendBuffer`, `packetlossLastSecond`, `packetlossTotal`, `isLimitedByCongestionControl`, `bpsLimitByCongestionControl`, `isLimitedByOutgoingBandwidthLimit`, `bpsLimitByOutgoingBandwidthLimit`, and `runningTotal` and `valueOverLastSecond` objects keyed by the `RNSPerSecondMetrics` names (`USER_MESSAGE_BYTES_PUSHED`, `USER_MESSAGE_BYTES_SENT`, `USER_MESSAGE_BYTES_RESENT`, `USER_MESSAGE_BYTES_RECEIVED_PROCESSED`, `USER_MESSAGE_BYTES_RECEIVED_IGNORED`, `ACTUAL_BYTES_SENT`, `ACTUAL_BYTES_RECEIVED`).

### Plugin commands

Plugin commands attach a RakNet plugin to the peer and drive it. They live in `tools/interop-peer/src/PluginCommands.cpp`. Every command of a plugin except its attach command fails with `ok:false` until the plugin is attached.

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `messageFilterAttach` | | | `AttachPlugin` of a `MessageFilter`, with `SetAutoAddNewConnectionsToFilter(-1)` because its constructor leaves that unset; again is a no-op |
| `messageFilterSetAutoAdd` | `filterSetId` (-1 adds none) | | `SetAutoAddNewConnectionsToFilter` |
| `messageFilterAllow` | `start`, `end` (inclusive, 0-255), `filterSetId`, `allow` (true) | | `SetAllowMessageID` |
| `messageFilterAllowRpc4` | `uniqueId`, `filterSetId`, `allow` (true) | | `SetAllowRPC4` |
| `messageFilterSetActions` | `filterSetId`, `kick` (false), `ban` (false), `banMs` (0 = forever) | | `SetActionOnDisallowedMessage` |
| `messageFilterSetMaxTime` | `filterSetId`, `ms` (0 = no limit), `ban` (false), `banMs` (0) | | `SetFilterMaxTime` |
| `messageFilterSetSystem` | `target` (guid), `filterSetId` (-1 removes it) | | `SetSystemFilterSet` |
| `messageFilterGetSystem` | `target` (guid) | `filterSetId` (-1 for none) | `GetSystemFilterSet` |
| `messageFilterDeleteSet` | `filterSetId` | | `DeleteFilterSet` |
| `messageFilterInfo` | | `filterSetIds` (ascending), `systemCount`, `disallowed` (array of `{guid,address,filterSetId,messageId}`), `timeouts` (array of `{guid,address,filterSetId}`) | `GetFilterSetCount`, `GetFilterSetIDByIndex`, `GetSystemCount(-1)`, and what the callbacks reported |

Every filter set that `messageFilterAllow`, `messageFilterAllowRpc4`, `messageFilterSetActions`, `messageFilterSetMaxTime` or `messageFilterSetSystem` names gets `SetDisallowedMessageCallback` and `SetTimeoutCallback`, which record what `messageFilterInfo` reports. A recorded `guid` is `"0"` and `address` empty when the filter knew only the other.

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `readyEventAttach` | `channel` (0) | | `AttachPlugin` of a `ReadyEvent` after `SetSendChannel`; again is a no-op |
| `readyEventSet` | `eventId`, `isReady` (true) | `result` (bool) | `SetEvent` |
| `readyEventForceCompletion` | `eventId` | | `ForceCompletion` |
| `readyEventDelete` | `eventId` | `result` (bool) | `DeleteEvent` |
| `readyEventAddToWaitList` | `eventId`, `target` (guid; omitted is `UNASSIGNED_RAKNET_GUID`, every connected system) | `result` (bool) | `AddToWaitList` |
| `readyEventRemoveFromWaitList` | `eventId`, `target` (guid; omitted is `UNASSIGNED_RAKNET_GUID`, everyone) | `result` (bool) | `RemoveFromWaitList` |
| `readyEventInfo` | `eventId` | `eventIds` (ascending), `hasEvent`, `isSet`, `isCompleted`, `isCompletionProcessing`, `waitList` (array of `{guid,status}` in wait-list order, `status` a `ReadyEventSystemStatus` name such as `RES_READY`) | `GetEventListSize`, `GetEventAtIndex`, `HasEvent`, `IsEventSet`, `IsEventCompleted`, `IsEventCompletionProcessing`, `GetRemoteWaitListSize`, `GetFromWaitListAtIndex`, `GetReadyStatus` |
| `twoWayAuthAttach` | | | `AttachPlugin` of a `TwoWayAuthentication`, created with `OP_NEW` because its `GetInstance` is never defined; again is a no-op |
| `twoWayAuthAddPassword` | `identifier`, `password` | `result` (bool) | `AddPassword` |
| `twoWayAuthChallenge` | `identifier`, `target` (guid) | `result` (bool) | `Challenge` |
| `twoWayAuthClear` | | | `Clear` |

TwoWayAuthentication's results reach the driver as `packet` events.

## Events

Every packet returned by `Receive()` is reported, including RakNet's internal notifications:

```json
{"event":"packet","messageId":134,"messageName":"ID_USER_PACKET_ENUM+0","guid":"1234567890","address":"127.0.0.1:61234","data":"86010203","timeMs":12345}
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
