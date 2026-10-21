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

### Replication commands

The object replication and RPC commands live in `tools/interop-peer/src/ReplicationCommands.cpp`. RandSync, the NetworkIDManager, the GridSectorizer and the VariableDeltaSerializer are created on first use and need no attach; every RPC4 command but `rpc4Attach` fails with `ok:false` until RPC4 is attached. Bit strings are hex with a bit count, most significant bit of each byte first, as a BitStream holds them; a missing count means every bit of the bytes. NetworkIDs are decimal strings, as GUIDs are.

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `rakNetRandom` | `seed`, `count` (0), `floatCount` (0), `fillBytes` (absent) | `values` (the `RandomMT` results), `floatBits` (the `FrandomMT` results as their 32 bits), and with `fillBytes` `fill` (hex) and `afterFill` (the next `RandomMT`) | a new `RakNetRandom`: `SeedMT`, then `RandomMT`, `FrandomMT` and `FillBufferMT` in that order |
| `randSyncSeed` | `seed`, `skip` (absent) | | `RakNetRandomSync::SeedMT`, with `skip` its two-argument form |
| `randSyncNext` | `count` (1), `float` (false) | `values` (numbers, or float bits with `float`) | `RandomMT` or `FrandomMT` |
| `randSyncSerializeConstruction` | | `data` (hex) | `SerializeConstruction` |
| `randSyncDeserializeConstruction` | `data` (hex) | `result` (bool) | `DeserializeConstruction` |
| `randSyncSerialize` | | `data` (hex) | `Serialize` |
| `randSyncDeserialize` | `data` (hex, at least 4 bytes) | | `Deserialize` |
| `randSyncSetCallCount` | `callCount` | | `SetCallCount` |
| `randSyncInfo` | | `seed`, `callCount` | `GetSeed`, `GetCallCount` |
| `networkIdObjectNew` | `handle` (an integer naming the object) | | a `NetworkIDObject` with no manager |
| `networkIdObjectSetManager` | `handle`, `attach` (true) | | `SetNetworkIDManager` of the peer's manager, or of none |
| `networkIdObjectSetId` | `handle`, `networkId` | | `SetNetworkID` |
| `networkIdObjectSetParent` | `handle`, `parent` (a handle; absent for none) | | `SetParent` |
| `networkIdObjectDelete` | `handle` | | the destructor |
| `networkIdObjectInfo` | `handle` | `networkId`, `hasManager` | `GetNetworkID`, `GetNetworkIDManager` |
| `networkIdLookup` | `networkId` | `handle` and `object` (handles or null) | `GET_BASE_OBJECT_FROM_ID`, `GET_OBJECT_FROM_ID` |
| `networkIdClear` | | | `Clear` |
| `gridInit` | `maxCellWidth`, `maxCellHeight` (positive), `minX`, `minY`, `maxX`, `maxY` | | `GridSectorizer::Init` |
| `gridAdd` | `entry` (a positive integer), `minX`, `minY`, `maxX`, `maxY` (a non-empty rectangle) | | `AddEntry` |
| `gridGet` | `minX`, `minY`, `maxX`, `maxY` | `entries` (in the order returned) | `GetEntries` |
| `gridClear` | | | `Clear` |
| `deltaSerialize` | `groups` (array, below), `preSerializeTick` (false) | `groups`, an array of `{data,bits,receipt,sent}` | `OnPreSerializeTick`, then per group `Begin*Serialize`, `SerializeVariable` of each int and `EndSerialize`, all on one `SerializationContext` |
| `deltaPreSerializeTick` | | | `OnPreSerializeTick` |
| `deltaReceipt` | `target` (guid), `receipt`, `arrived` | | `OnMessageReceipt` |
| `deltaRemoveSystem` | `target` (guid) | | `RemoveRemoteSystemVariableHistory` |
| `deltaDeserialize` | `data` (hex), `count` | `variables`, an array of `{changed,value}` (`value` null when unchanged) | `DeserializeVariable` of an int, `count` times |
| `rpc4Attach` | | | `AttachPlugin` of an `RPC4`; again is a no-op |
| `rpc4RegisterFunction` | `name` | `result` (bool), `handler` (null when refused) | `RegisterFunction` |
| `rpc4RegisterSlot` | `name`, `priority` (0), `interrupt` (false) | `handler` | `RegisterSlot`; with `interrupt` the slot calls `InterruptSignal` |
| `rpc4RegisterBlocking` | `name`, `returnData` (hex, absent for none), `returnBits` | `result` (bool), `handler` (null when refused) | `RegisterBlockingFunction` of a function that writes `returnData` |
| `rpc4RegisterLocalCallback` | `name`, `messageId` | | `RegisterLocalCallback` |
| `rpc4UnregisterFunction`, `rpc4UnregisterBlocking`, `rpc4UnregisterSlot` | `name` | `result` (bool) | `UnregisterFunction`, `UnregisterBlockingFunction`, `UnregisterSlot` |
| `rpc4UnregisterLocalCallback` | `name`, `messageId` | `result` (bool) | `UnregisterLocalCallback` |
| `rpc4Call` | `name`, `parameters` (hex, absent for none), `bits`, `target` (guid; may be omitted with `broadcast`), `broadcast` (false), `reliability` (`RELIABLE_ORDERED`), `priority` (`HIGH_PRIORITY`), `channel` (0) | | `Call` |
| `rpc4Signal` | the fields of `rpc4Call`, `invokeLocal` (false) | | `Signal` |
| `rpc4CallLoopback` | `name`, `parameters`, `bits` | | `CallLoopback` |
| `rpc4CallBlocking` | the fields of `rpc4Call` but `broadcast`; `target` required | `result` (bool), `returnData` (hex), `returnBits` | `CallBlocking`, which reads packets itself until the return arrives, holding the peer's lock |
| `rpc4Info` | | `invocations`, an array of `{handler,name,guid,address,local,parameters,bits}` in call order | what the registered functions recorded |

A `deltaSerialize` group has `mode` (`UNRELIABLE_ACKED`, `UNIQUE` or `IDENTICAL`), `values` (ints), `target` (guid, for `UNRELIABLE_ACKED` and `UNIQUE`), `receipt` (for `UNRELIABLE_ACKED`), `first` (`BeginIdenticalSerialize`'s first-serialize flag, false) and optionally `sendTo` (guid). With `sendTo` an `UNRELIABLE_ACKED` group takes its receipt from `IncrementNextSendReceipt`, and a group that wrote anything is sent as `messageId` (`ID_USER_PACKET_ENUM`) then its bits, `HIGH_PRIORITY` on channel 0, with `reliability` (by default `UNRELIABLE_WITH_ACK_RECEIPT` for `UNRELIABLE_ACKED` and `RELIABLE_ORDERED` otherwise) and that receipt.

Each RPC4 registration is a function of its own, numbered by `handler` in registration order, which records the packet's GUID, its address (empty when unassigned) and `wasGeneratedLocally`, and the unread bits of the parameter stream; the stream's read position is left as it was.

### ReplicaManager3 commands

The ReplicaManager3 commands live in `tools/interop-peer/src/ReplicaManager3Commands.cpp`. They drive a ReplicaManager3
with the replicas of `Samples/ReplicaManager3` (`ClientCreatible_ClientSerialized`, `ServerCreated_ClientSerialized`,
`ClientCreatible_ServerSerialized`, `ServerCreated_ServerSerialized`, `P2PReplica`, each with the sample's four int
variables and VariableDeltaSerializer) and the `User` of `Samples/ReplicatedLogin`. The topology replaces the samples'
global: a sample replica is on a server unless the topology is `CLIENT`, and a User only when it is `SERVER`. Every
command but `rm3Attach` fails with `ok:false` until the plugin is attached. NetworkIDs and GUIDs are decimal strings.

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `rm3Attach` | `topology` (`SERVER`; `CLIENT`, `SERVER` or `P2P`), `autoSerializeMs` (30; negative is never), `groupDownloads` (true) | | `AttachPlugin` of the sample's ReplicaManager3 with a NetworkIDManager of its own, `SetNetworkIDManager`, `SetAutoSerializeInterval`; its connections return `groupDownloads` from `QueryGroupDownloadMessages`. A second plugin passes ID_SND_RECEIPT_ACKED and ID_SND_RECEIPT_LOSS to the replicas this system created, as the sample's loop does. Again is a no-op |
| `rm3Create` | `kinds` (array of class names), `username` (""), `userGuid` (for a User) | `networkIds` | `Reference` of a new replica of each kind, all in this one call, as the sample's 'C' does |
| `rm3Set` | `networkId`, and any of `var1` to `var4` (sample replicas), `score` and `username` (a User) | | sets the variables, as the sample's 'R' does with random values |
| `rm3DestroyMine` | | `destroyed` | the sample's 'D': `GetReplicasCreatedByMe`, `BroadcastDestructionList`, then deletes each |
| `rm3Destroy` | `networkId` | | ReplicatedLogin's `DeleteUserWithGuid`: `BroadcastDestruction`, `Dereference`, then deletes it |
| `rm3Info` | | `replicas` (below), `connections`, an array of `{guid,validated,downloadComplete,constructed}` (`constructed` the NetworkIDs of `GetConstructedReplicas`), `destructions`, an array of `{networkId,kind,text,deletingGuid}` that `DeserializeDestruction` read (`text` empty for a User), `allDownloadsCompleted` | `GetReferencedReplicaList`, `GetConnectionAtIndex`, `GetAllConnectionDownloadsCompleted` |

Each of `replicas`, in `GetReferencedReplicaList` order, has `networkId`, `creatingGuid` and `kind`; a sample replica
adds `var1` to `var4` and `constructionText`, the string its `DeserializeConstruction` read (empty for a replica created
here); a User adds `score`, `username` and `userGuid`.

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
