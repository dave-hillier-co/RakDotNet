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
{"event":"ready","raknetVersion":"4.081","protocolVersion":6,"security":false}
```

`security` is true when the peer's RakNet was compiled with `LIBCAT_SECURITY=1` (see [Secure build](#secure-build)).

## Commands

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `startup` | `port` (0), `host` ("127.0.0.1"), `maxConnections` (8), `maxIncoming` (8) | `port` (actually bound), `guid` | `Startup`, `SetMaximumIncomingConnections` |
| `connect` | `host`, `port`, `password` (hex, ""), `attempts` (12), `attemptIntervalMs` (500), `timeoutMs` (0), and the `PublicKey` fields: `publicKeyMode` (`INSECURE_CONNECTION`), `serverPublicKey`, `clientPublicKey`, `clientPrivateKey` (hex, absent) | `result` (`ConnectionAttemptResult` name, e.g. `CONNECTION_ATTEMPT_STARTED`) | `Connect` |
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

Security commands, also in the plain build, where `generateKeys` fails and `initSecurity` reports `initialized: false`
because RakNet has no secure connections there:

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `generateKeys` | | `publicKey` (64 bytes hex), `privateKey` (32 bytes hex) | `cat::EasyHandshake::GenerateServerKey`, as Samples/Encryption makes server and client keys |
| `initSecurity` | `publicKey`, `privateKey` (hex), `requireClientKey` (false) | `initialized` (bool) | `InitializeSecurity`; only before `startup` |
| `disableSecurity` | | | `DisableSecurity` |
| `addSecurityException` | `ip` | | `AddToSecurityExceptionList` |
| `removeSecurityException` | `ip` (absent clears the list) | | `RemoveFromSecurityExceptionList` |
| `securityException` | `ip` | `listed` (bool) | `IsInSecurityExceptionList` |
| `clientPublicKey` | `target` | `publicKey` (hex, or null when the system proved no identity) | `GetClientPublicKeyFromSystemAddress` |
| `setIncomingPassword` | `data` (hex, "") | | `SetIncomingPassword` |
| `incomingPassword` | | `data` (hex) | `GetIncomingPassword` |
| `unban` | `ip` | | `RemoveFromBanList` |
| `clearBans` | | | `ClearBanList` |
| `isBanned` | `ip` | `banned` (bool) | `IsBanned` |

`publicKeyMode` takes RakNet's `PublicKeyMode` names without the `PKM_` prefix: `INSECURE_CONNECTION`,
`ACCEPT_ANY_PUBLIC_KEY`, `USE_KNOWN_PUBLIC_KEY` (needs `serverPublicKey`) and `USE_TWO_WAY_AUTHENTICATION` (needs
`serverPublicKey`, `clientPublicKey` and `clientPrivateKey`). A key of the wrong length fails the command; a missing one
is passed as a null pointer, for which `Connect` returns `SECURITY_INITIALIZATION_FAILED`.

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

### NAT commands

The NAT commands live in `tools/interop-peer/src/NatCommands.cpp`. `natGateway` puts the process behind a NAT router of
the harness (see [NAT gateway](#nat-gateway)); the others attach and drive RakNet's NAT plugins, and every one but its
plugin's attach command fails with `ok:false` until that plugin is attached. Addresses are `host` ("127.0.0.1") and
`port` fields.

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `natGateway` | `host` ("127.0.0.1"), `port` (0 leaves the router) | | puts the process behind the router whose gateway is at the address |
| `natTypeDetectionClientAttach` | | | `AttachPlugin` of a `NatTypeDetectionClient`; again is a no-op |
| `natTypeDetectionClientDetect` | `host`, `port` (the connected server) | | `DetectNATType` |
| `natTypeDetectionServerAttach` | | | `AttachPlugin` of a `NatTypeDetectionServer`; again is a no-op |
| `natTypeDetectionServerStartup` | `ip2`, `ip3`, `ip4` (each "127.0.0.1") | `addresses`: where S1P2, S2P3, S3P4 and S4P5 are bound, in that order | `Startup`, then the plugin's protected sockets' `GetBoundAddress` |
| `natPunchthroughServerAttach` | | | `AttachPlugin` of a `NatPunchthroughServer`; again is a no-op |
| `natPunchthroughClientAttach` | | | `AttachPlugin` of a `NatPunchthroughClient`; again is a no-op |
| `natPunchthroughClientConfigure` | any of `timeBetweenPunchAttemptsInternal`, `timeBetweenPunchAttemptsExternal`, `udpSendsPerPortInternal`, `udpSendsPerPortExternal`, `internalIpWaitAfterAttempts`, `maxPredictivePortRange`, `externalIpWaitBetweenPorts`, `externalIpWaitAfterFirstTtl`, `externalIpWaitAfterAllAttempts`, `maximumNumberOfInternalIdsToCheck` (milliseconds or counts), `retryOnFailure` (bool) | | sets those fields of `GetPunchthroughConfiguration()` |
| `natPunchthroughOpen` | `target` (guid), `host`, `port` (the facilitator) | `result` (bool) | `OpenNAT` |
| `natPunchthroughFindPortStride` | `host`, `port` (the facilitator) | | `FindRouterPortStride` |

The plugins' results reach the driver as `packet` events. The peer sets no `NatPunchthroughDebugInterface`:
`NatPunchthroughClient` formats some of its messages with `%I64d`, which only Windows' `printf` takes, and elsewhere
they read the wrong arguments. A `NatTypeDetectionServer` or `NatTypeDetectionClient` is never freed, because
`NatTypeDetectionServer::Shutdown` deletes sockets whose receive threads still run.

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

## NAT gateway

`tests/RakDotNet.Interop.Tests/Harness/NatEmulator.cs` runs NAT routers on 127.0.0.1. A host behind one sends every
datagram to the router's gateway address, prefixed with a 7-byte header, and receives from the gateway datagrams with
the same header:

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | The IP time-to-live the datagram was sent with, or 0 for the default. Host to router only; 0 from the router. |
| 1 | 4 | Host to router: the destination's IPv4 address. Router to host: the source's. Network order. |
| 5 | 2 | The destination's or source's port, big-endian. |
| 7 | | The datagram. |

The host reports a datagram from the gateway as coming from the header's address and drops everything else it
receives, as nothing outside routes to a private address; a socket's datagrams to its own address pass untouched,
because RakNet sends to itself to test a socket and to unblock its receive thread. The router maps the destination,
dropping a datagram with a time-to-live of 1 and mapping without forwarding one of 2, as `SendTTL` intends.

After `natGateway`, `interop-peer` does this for every UDP socket in the process: `src/NatGateway.cpp` replaces
`sendto` and `recvfrom`, which RakNet calls through `sendto__` and `recvfrom__`, and reads the time-to-live with
`getsockopt(IP_TTL)`. A RakDotNet peer does it through `NatGatewayTransportFactory`, given as its transport; the
`RakDotNetPeerDriver` takes `natGateway` only before `startup`, as the transport is chosen when the peer starts.

## Secure build

`tools/interop-peer/build.sh` builds a second binary, `tools/interop-peer/build/interop-peer-secure`, from the same
sources against RakNet compiled with `LIBCAT_SECURITY=1`, which builds `DependentExtensions/cat` through
`Source/SecureHandshake.cpp`. The CMake option `LIBCAT_SECURITY` (on by default) controls it; configure with
`-DLIBCAT_SECURITY=OFF` to build only the plain peer. Drivers find the secure binary at
`RAKDOTNET_INTEROP_PEER_SECURE`, or at its build path when that is unset; scenarios that need secure connections skip
when it is missing.

A secure session generates keys, initialises the server before starting it, and connects with the server's key:

```text
> {"seq":1,"cmd":"generateKeys"}
< {"seq":1,"ok":true,"publicKey":"e5ab...2931","privateKey":"6bec...e819"}
> {"seq":2,"cmd":"initSecurity","publicKey":"e5ab...2931","privateKey":"6bec...e819","requireClientKey":false}
< {"seq":2,"ok":true,"initialized":true}
> {"seq":3,"cmd":"startup","port":0}
< {"seq":3,"ok":true,"port":61234,"guid":"1520003954185392640"}
```

and, on the client:

```text
> {"seq":1,"cmd":"connect","host":"127.0.0.1","port":61234,"publicKeyMode":"USE_KNOWN_PUBLIC_KEY","serverPublicKey":"e5ab...2931"}
< {"seq":1,"ok":true,"result":"CONNECTION_ATTEMPT_STARTED"}
```

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
