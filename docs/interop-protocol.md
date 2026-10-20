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

#### Topology commands

ConnectionGraph2 and FullyConnectedMesh2 commands live in `tools/interop-peer/src/TopologyCommands.cpp`, with the same rules: every command of a plugin except its attach command fails until it is attached. Commands that take a packet rebuild it from the fields of the `packet` event the driver received: `data` (hex), `guid` and `address`.

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `cg2Attach` | `autoProcess` (true) | | `AttachPlugin` of a `ConnectionGraph2` after `SetAutoProcessNewConnections`; again is a no-op |
| `cg2SetAutoProcess` | `enabled` | | `SetAutoProcessNewConnections` |
| `cg2AddParticipant` | `target` (guid) | | `AddParticipant` with `GetSystemAddressFromGuid(target)` |
| `cg2Info` | | `participants` (array of `{guid,connections}` in `GetParticipantList` order, `connections` an array of `{guid,address,ping}`), `lowestAveragePingSystem` | `GetParticipantList`, `GetConnectionListForRemoteSystem` (up to 64 per participant), `GetPingBetweenSystems`, `GetLowestAveragePingSystem` |
| `fcm2Attach` | `autoParticipate` (true), `connectOnNewRemote` (true), `password` (hex, ""), `vjsUserData` (hex, ""), `vjcUserData` (hex, "") | | `AttachPlugin` of a `FullyConnectedMesh2` subclass whose `WriteVJSUserData` and `WriteVJCUserData` write the given bytes (nothing when empty), after `SetAutoparticipateConnections` and `SetConnectOnNewRemoteConnection`; again is a no-op |
| `fcm2AddParticipant` | `target` (guid) | | `AddParticipant` |
| `fcm2ResetHostCalculation` | | | `ResetHostCalculation` |
| `fcm2Clear` | | | `Clear` |
| `fcm2Info` | | `hostSystem`, `isHostSystem`, `connectedHost` (`UNASSIGNED_RAKNET_GUID` as `"18446744073709551615"`), `isConnectedHost`, `connectedHostAddress` (empty when unassigned), `participants`, `hostOrder`, `totalConnectionCount` | `GetHostSystem`, `IsHostSystem`, `GetConnectedHost`, `IsConnectedHost`, `GetConnectedHostAddr`, `GetParticipantList`, `GetHostOrder`, `GetTotalConnectionCount` |
| `fcm2StartVerifiedJoin` | `target` (guid) | | `StartVerifiedJoin` |
| `fcm2RespondOnVerifiedJoinCapable` | `data`, `guid`, `address`, `accept`, `additionalData` (hex, ""; none when empty) | | `RespondOnVerifiedJoinCapable` |
| `fcm2VerifiedJoinRequired` | `host` (guid) | `systems` (array of `{guid,address,userData,userDataBits}`, `userData` the hex of the user data bytes) | `GetVerifiedJoinRequiredProcessingList` |
| `fcm2VerifiedJoinAccepted` | `data`, `guid`, `address` | `thisSystemAccepted`, `systemsAccepted`, `additionalData` (hex) | `GetVerifiedJoinAcceptedAdditionalData` |
| `fcm2ConnectToRemoteNewIncomingConnections` | `data`, `guid`, `address` | | `ConnectToRemoteNewIncomingConnections` |

The plugins' messages, ID_FCM2_NEW_HOST and the verified join messages that reach the application included, reach the driver as `packet` events.

#### Team commands

TeamManager and TeamBalancer commands live in `tools/interop-peer/src/TeamCommands.cpp`, with the same rules. A world is named by `worldId` (0 to 254), and teams and members by their NetworkID as a decimal string in `team`, `teamToJoin`, `teamToLeave` and `member`. The peer makes and owns a `TM_Team` or `TM_TeamMember` for each one it references or deserialises; a command naming one the world does not have fails. TeamBalancer team numbers are integers, 255 being `UNASSIGNED_TEAM_ID`.

| `cmd` | Fields (defaults) | Reply fields | RakNet call |
|---|---|---|---|
| `tmAttach` | `topology` (`"TM_PEER_TO_PEER"`, or `"TM_CLIENT_SERVER"`) | | `AttachPlugin` of a `TeamManager` after `SetTopology`; again is a no-op |
| `tmSetAutoManageConnections` | `enabled` | | `TeamManager::SetAutoManageConnections`, which reaches only the worlds already added |
| `tmAddWorld` | `worldId` | | `AddWorld`; fails for a world in use |
| `tmSetHost` | `worldId`, `host` (guid) | | `TM_World::SetHost` |
| `tmAddParticipant` / `tmRemoveParticipant` | `worldId`, `target` (guid) | | `AddParticipant` / `RemoveParticipant` |
| `tmReferenceTeam` | `worldId`, `team`, `applyBalancing` (true) | | `ReferenceTeam` of a new `TM_Team` |
| `tmReferenceTeamMember` | `worldId`, `member` | | `ReferenceTeamMember` of a new `TM_TeamMember` |
| `tmDereferenceTeam` | `worldId`, `team`, `noTeamId` | | `DereferenceTeam` |
| `tmDereferenceTeamMember` | `worldId`, `member` | | `DereferenceTeamMember` |
| `tmRequestTeam` | `worldId`, `member`, `selection` (a `JoinTeamType` name such as `JOIN_SPECIFIC_TEAM`), `team` for a specific team, `noTeamId` for none | `result` (bool) | `RequestTeam` with `TeamSelection::AnyAvailable`, `SpecificTeam` or `NoTeam` |
| `tmRequestTeamSwitch` | `worldId`, `member`, `teamToJoin`, `teamToLeave` (omitted is every team) | `result` (bool) | `RequestTeamSwitch` |
| `tmCancelTeamRequest` | `worldId`, `member`, `team` (omitted is every team) | `result` (bool) | `CancelTeamRequest` |
| `tmLeaveTeam` | `worldId`, `member`, `team`, `noTeamId` | `result` (bool) | `TM_TeamMember::LeaveTeam` |
| `tmLeaveAllTeams` | `worldId`, `member`, `noTeamId` | `result` (bool) | `LeaveAllTeams` |
| `tmSetMemberLimit` | `worldId`, `team`, `limit`, `noTeamId` | `result` (bool) | `TM_Team::SetMemberLimit` |
| `tmSetJoinPermissions` | `worldId`, `team`, `permissions` (the `ALLOW_JOIN_*` bits) | `result` (bool) | `SetJoinPermissions` |
| `tmSetBalanceTeams` | `worldId`, `enabled`, `noTeamId` | `result` (bool) | `SetBalanceTeams` |
| `tmSerializeTeam` | `worldId`, `team` | `data` (hex) | `TM_Team::SerializeConstruction` |
| `tmSerializeTeamMember` | `worldId`, `member` | `data` (hex) | `TM_TeamMember::SerializeConstruction` |
| `tmDeserializeTeam` | `data` (hex) | `result` (bool) | `TM_Team::DeserializeConstruction` into the team its world already has with the construction's NetworkID, otherwise a new one |
| `tmDeserializeTeamMember` | `data` (hex) | `result` (bool) | `TM_TeamMember::DeserializeConstruction` into a new member |
| `tmInfo` | `worldId` | `host`, `balanceTeams`, `participants`, `teams` (array of `{team,members,memberLimit,memberLimitSetting,joinPermissions,balancingApplies}` in world order), `members` (array of `{member,teams,lastTeams,noTeamId,requestedTeams,requestedTeam}` in world order, `requestedTeam` the `JoinTeamType` name of `GetRequestedTeam`) | `GetHost`, `GetBalanceTeams`, `GetParticipantList`, `GetTeamByIndex`, `GetTeamMembers`, `GetMemberLimit`, `GetMemberLimitSetting`, `GetJoinPermissions`, `GetBalancingApplies`, `GetTeamMemberByIndex`, `GetCurrentTeams`, `GetLastTeams`, `GetNoTeamId`, `GetRequestedSpecificTeams`, `GetRequestedTeam` |
| `tbAttach` | | | `AttachPlugin` of a `TeamBalancer`; again is a no-op |
| `tbSetTeamSizeLimit` | `team` (0 to 254), `limit` | | `SetTeamSizeLimit` |
| `tbSetDefaultAssignmentAlgorithm` | `algorithm` (`"SMALLEST_TEAM"` or `"FILL_IN_ORDER"`) | | `SetDefaultAssignmentAlgorithm` |
| `tbSetForceEvenTeams` | `force` | | `SetForceEvenTeams` |
| `tbSetLockTeams` | `lock` | | `SetLockTeams` |
| `tbRequestSpecificTeam` | `member`, `team` | | `RequestSpecificTeam` |
| `tbCancelRequestSpecificTeam` | `member` | | `CancelRequestSpecificTeam` |
| `tbRequestAnyTeam` | `member` | | `RequestAnyTeam` |
| `tbGetMyTeam` | `member` | `team` | `GetMyTeam` |
| `tbDeleteMember` | `member` | | `DeleteMember` |

The ID_TEAM_BALANCER_* messages that reach the application, including those a plugin pushes itself, reach the driver as `packet` events.

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
