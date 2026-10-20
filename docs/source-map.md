# RakNet source map

This page maps every unit of RakNet 4.081 `Source/` (a `.h`/`.cpp` pair, a lone `.cpp`, or a lone `.h`) to where it lives in RakDotNet. The C++ is in `reference/RakNet/Source`.

## How to read it

- Each row is one unit. The columns are the C++ files, the C# location, the owning milestone from [plan.md](plan.md), and a one-line note.
- The C# column holds repo-relative links, or a .NET BCL type when RakNet's own implementation is replaced by the framework. RakNet containers, threads, mutexes, allocators, string helpers and platform socket variants are replaced by BCL types rather than ported, and the row names the type.
- `n/a` in the C# column means the unit has no C# counterpart: it is compiled out by default, is platform-specific, or only declares things .NET provides natively.
- `M2`, `M4`, `M8.2` and so on are the milestones in [plan.md](plan.md). A unit owned by a milestone 8 group lives in that group's plugin code, built on the public API.
- C# comments cite the C++ as `File.cpp:line`, relative to `reference/RakNet/Source`. Search the repository for the C++ file name to find the code that ports it.
- Assembly and layer boundaries are in [api-design.md](api-design.md) and [conventions.md](conventions.md).

## Wire and types

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `BitStream` (`BitStream.cpp`, `BitStream.h`) | [`BitStream.cs`](../src/RakDotNet.Protocol/Wire/BitStream.cs), [`BitStreamWriter.cs`](../src/RakDotNet.Protocol/Wire/BitStreamWriter.cs), [`BitStreamReader.cs`](../src/RakDotNet.Protocol/Wire/BitStreamReader.cs), [`BitStreamEncodings.cs`](../src/RakDotNet.Protocol/Wire/BitStreamEncodings.cs), [`BitStreamEncodings.Delta.cs`](../src/RakDotNet.Protocol/Wire/BitStreamEncodings.Delta.cs), [`BitStreamEncodings.Strings.cs`](../src/RakDotNet.Protocol/Wire/BitStreamEncodings.Strings.cs), [`BitSerializer.cs`](../src/RakDotNet.Protocol/Wire/BitSerializer.cs) | M2 | Pooled writer with a settable write offset, ref-struct writer and reader with a settable read position, extension encodings (big-endian integers, uint24, compressed and delta values, ranges, vectors, quaternions, floats, strings), and `BitSerializer` for the `Serialize*` overloads. |
| `RakNetTypes` (`RakNetTypes.cpp`, `RakNetTypes.h`) | [`SystemAddress.cs`](../src/RakDotNet.Protocol/Wire/SystemAddress.cs), [`RakNetGuid.cs`](../src/RakDotNet.Protocol/Wire/RakNetGuid.cs), [`UInt24.cs`](../src/RakDotNet.Protocol/Wire/UInt24.cs), [`RakNetConstants.cs`](../src/RakDotNet.Protocol/Wire/Messages/RakNetConstants.cs), [`AddressOrGuid.cs`](../src/RakDotNet/AddressOrGuid.cs), [`RakNetTypes.cs`](../src/RakDotNet.Compatibility/RakNetTypes.cs), [`ConnectSecurity.cs`](../src/RakDotNet/Security/ConnectSecurity.cs), [`PeerEvent.cs`](../src/RakDotNet/PeerEvent.cs) | M2 | Address, GUID and uint24 value types are readonly structs with RakNet's ordering and text forms; `NetworkID` and `SystemIndex` are `ulong` and `ushort` with their unassigned constants; `Packet` maps to `PeerEvent` and the compatibility `Packet`. |
| `MessageIdentifiers` (`MessageIdentifiers.h`) | [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | M2 | `DefaultMessageIDTypes` as a byte enum with the same positional values. |
| `PacketPriority` (`PacketPriority.h`) | [`Reliability.cs`](../src/RakDotNet.Protocol/Wire/Reliability.cs) | M2 | `PacketPriority` becomes `Priority` and `PacketReliability` becomes `Reliability`, with extension helpers. |
| `MTUSize` (`MTUSize.h`) | [`DatagramLimits.cs`](../src/RakDotNet.Protocol/Wire/Datagrams/DatagramLimits.cs), [`RakNetConstants.cs`](../src/RakDotNet.Protocol/Wire/Messages/RakNetConstants.cs) | M2 | `MAXIMUM_MTU_SIZE` and `MINIMUM_MTU_SIZE` constants. |
| `RakNetVersion` (`RakNetVersion.h`) | [`RakNetConstants.cs`](../src/RakDotNet.Protocol/Wire/Messages/RakNetConstants.cs) | M2 | The version, version numbers, date and `RAKNET_PROTOCOL_VERSION` are `RakNetConstants` members. |
| `RakNetTime` (`RakNetTime.h`) | `ulong`, `uint`; [`UnconnectedPing.cs`](../src/RakDotNet.Protocol/Wire/Messages/UnconnectedPing.cs) | M2 | The `Time`, `TimeMS` and `TimeUS` typedefs are `ulong`, `uint` and `ulong`. |
| `NativeTypes` (`NativeTypes.h`) | `System.Byte`, `System.UInt16`, `System.UInt32`, `System.UInt64` and signed equivalents | n/a | Fixed-width integer typedefs are the .NET primitives. |
| `StringCompressor` (`StringCompressor.cpp`, `StringCompressor.h`) | [`StringCompressor.cs`](../src/RakDotNet.Protocol/Wire/StringCompressor.cs), [`BitStreamEncodings.Strings.cs`](../src/RakDotNet.Protocol/Wire/BitStreamEncodings.Strings.cs) | M2 | A tree per language id with the English table as language 0; `Instance` is the singleton; `WriteCompressedString` and `TryReadCompressedString` are the RakString forms of `EncodeString` and `DecodeString`. |
| `DS_HuffmanEncodingTree` (`DS_HuffmanEncodingTree.cpp`, `DS_HuffmanEncodingTree.h`) | [`HuffmanEncodingTree.cs`](../src/RakDotNet.Protocol/Wire/HuffmanEncodingTree.cs) | M2 | Immutable tree built from a frequency table, with `EncodeArray` and both `DecodeArray` forms; `FreeMemory` is garbage collection. |
| `DS_HuffmanEncodingTreeFactory` (`DS_HuffmanEncodingTreeFactory.h`) | n/a | M2 | Declaration-only helper with no `.cpp` and no references. |
| `DS_HuffmanEncodingTreeNode` (`DS_HuffmanEncodingTreeNode.h`) | [`HuffmanEncodingTree.cs`](../src/RakDotNet.Protocol/Wire/HuffmanEncodingTree.cs) | M2 | The node struct is replaced by parallel `int[]` and `byte[]` arrays; weights are `uint` and wrap as the 32-bit `unsigned` does. |
| `RakString` (`RakString.cpp`, `RakString.h`) | `System.String`, `System.Text.StringBuilder`; [`BitStreamEncodings.Strings.cs`](../src/RakDotNet.Protocol/Wire/BitStreamEncodings.Strings.cs), [`SecurityExceptionList.cs`](../src/RakDotNet.Protocol/Peer/SecurityExceptionList.cs) | M2 | The shared string is `string` or bytes; the wire forms carry raw bytes (`WriteRakString`, `TryReadRakStringBytes`, the compressed pair with an optional language id), with UTF-8 `string` overloads. `IPAddressMatch` matches the security exception list. |
| `RakWString` (`RakWString.cpp`, `RakWString.h`) | `System.String` (UTF-16); [`BitStreamEncodings.Strings.cs`](../src/RakDotNet.Protocol/Wire/BitStreamEncodings.Strings.cs) | M2 | The wire form is a `u16` character count then big-endian `u16` characters: `WriteRakWString` and `TryReadRakWString`. |
| `StringTable` (`StringTable.cpp`, `StringTable.h`) | [`StringTable.cs`](../src/RakDotNet.Protocol/Wire/StringTable.cs) | M2 | Registered strings travel as their index in `strcmp` order; others fall back to `StringCompressor`. `Instance` is the process-wide table. |
| `DataCompressor` (`DataCompressor.cpp`, `DataCompressor.h`) | owned by M8 utilities; tree in [`HuffmanEncodingTree.cs`](../src/RakDotNet.Protocol/Wire/HuffmanEncodingTree.cs) | M8 | Static Huffman block compressor built on `HuffmanEncodingTree`. |

## Reliability

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `ReliabilityLayer` (`ReliabilityLayer.cpp`, `ReliabilityLayer.h`) | [`ReliabilityLayer.cs`](../src/RakDotNet.Protocol/Session/ReliabilityLayer.cs), [`SendQueue.cs`](../src/RakDotNet.Protocol/Session/SendQueue.cs), [`DatagramHistory.cs`](../src/RakDotNet.Protocol/Session/DatagramHistory.cs), [`ReceivedMessageNumbers.cs`](../src/RakDotNet.Protocol/Session/ReceivedMessageNumbers.cs), [`OrderingChannels.cs`](../src/RakDotNet.Protocol/Session/OrderingChannels.cs), [`SplitMessageAssembler.cs`](../src/RakDotNet.Protocol/Session/SplitMessageAssembler.cs), [`BpsTracker.cs`](../src/RakDotNet.Protocol/Session/BpsTracker.cs), [`DatagramHeader.cs`](../src/RakDotNet.Protocol/Wire/Datagrams/DatagramHeader.cs), [`NetworkSimulator.cs`](../src/RakDotNet/Transport/NetworkSimulator.cs), [`INetworkObserver.cs`](../src/RakDotNet/INetworkObserver.cs) | M4 | Sans-IO per-connection state machine. `DS_Heap` becomes `PriorityQueue`, the intrusive lists become `LinkedList<T>`, `DS_Queue` becomes `Queue<T>`/`RingQueue`. With `useSecurity` every datagram goes through the connection's `AuthenticatedEncryption`, and the datagram budget loses its overhead twice as in the C++ (M7). |
| `CCRakNetSlidingWindow` (`CCRakNetSlidingWindow.cpp`, `CCRakNetSlidingWindow.h`) | [`SlidingWindow.cs`](../src/RakDotNet.Protocol/Session/SlidingWindow.cs), [`UInt24.cs`](../src/RakDotNet.Protocol/Wire/UInt24.cs) | M4 | Default congestion manager (`USE_SLIDING_WINDOW_CONGESTION_CONTROL=1`), in microseconds. |
| `CCRakNetUDT` (`CCRakNetUDT.cpp`, `CCRakNetUDT.h`) | n/a | M4 | Compiled out by `USE_SLIDING_WINDOW_CONGESTION_CONTROL=1` (`RakNetDefines.h:122`); `SlidingWindow` is the congestion manager. |
| `InternalPacket` (`InternalPacket.h`) | [`EncapsulatedMessage.cs`](../src/RakDotNet.Protocol/Wire/Datagrams/EncapsulatedMessage.cs), [`SplitHeader.cs`](../src/RakDotNet.Protocol/Wire/Datagrams/SplitHeader.cs), [`ReceivedMessage.cs`](../src/RakDotNet.Protocol/Session/ReceivedMessage.cs), [`ReliabilityLayer.cs`](../src/RakDotNet.Protocol/Session/ReliabilityLayer.cs) | M2 | Wire fields are `EncapsulatedMessage` and `SplitHeader`; send-side state is the private `OutgoingMessage`; payloads are `ReadOnlyMemory<byte>` slices. |
| `DS_RangeList` (`DS_RangeList.h`) | [`RangeList.cs`](../src/RakDotNet.Protocol/Session/RangeList.cs), [`AckRangeList.cs`](../src/RakDotNet.Protocol/Wire/Datagrams/AckRangeList.cs), [`AckRange.cs`](../src/RakDotNet.Protocol/Wire/Datagrams/AckRange.cs) | M2 | ACK and NAK range lists; `Serialize` and `Deserialize` are `AckRangeList.Write` and `TryRead`. `Insert` keeps RakNet's lookup by minimum, probe order and uint24_t wrap of `minIndex-1`, so out-of-order and duplicate numbers give the same, possibly overlapping or repeated, ranges on the wire. |
| `RakNetStatistics` (`RakNetStatistics.cpp`, `RakNetStatistics.h`) | [`RakNetStatistics.cs`](../src/RakDotNet/RakNetStatistics.cs), [`SessionStatistics.cs`](../src/RakDotNet.Protocol/Session/SessionStatistics.cs), [`BpsTracker.cs`](../src/RakDotNet.Protocol/Session/BpsTracker.cs) | M5 | Statistics struct with `ToString(StatisticsVerbosity)` for the three verbosity levels. |

## Peer and configuration

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `RakPeer` (`RakPeer.cpp`, `RakPeer.h`) | [`PeerCore.cs`](../src/RakDotNet.Protocol/Peer/PeerCore.cs), [`BanTable.cs`](../src/RakDotNet.Protocol/Peer/BanTable.cs), [`LocalAddressList.cs`](../src/RakDotNet.Protocol/Peer/LocalAddressList.cs), [`RakPeer.cs`](../src/RakDotNet/RakPeer.cs), [`RakPeer.Cycle.cs`](../src/RakDotNet/RakPeer.Cycle.cs), [`RakConnection.cs`](../src/RakDotNet/RakConnection.cs), [`RakPeerLowLevel.cs`](../src/RakDotNet/RakPeerLowLevel.cs), [`PluginCollection.cs`](../src/RakDotNet/PluginCollection.cs), [`PeerCommand.cs`](../src/RakDotNet/Transport/PeerCommand.cs) | M4 | Sans-IO `PeerCore` (handshakes, slots, pings, bans, `activeSystemList` order) plus the public `RakPeer` (commands, I/O cycle, events, plugin dispatch). Every active slot has a `RakConnection`, so lookups, sends and broadcasts follow RakNet's slot semantics; `ID_TIMESTAMP` is shifted at the read, as in `Receive`. `ConcurrentQueue`, `Thread` with `ManualResetEventSlim`, `Lock`, `ArrayPool` and `Dns` replace the queues, threads, mutexes, pools and resolver. The `LIBCAT_SECURITY` paths (cookie and key in the offline handshake, challenge and answer, proof and identity in the connection request, the security exception list in [`SecurityExceptionList.cs`](../src/RakDotNet.Protocol/Peer/SecurityExceptionList.cs) and `AddressPatternCollection`) are ported with their quirks (M7). |
| `RakPeerInterface` (`RakPeerInterface.h`) | [`RakPeer.cs`](../src/RakDotNet/RakPeer.cs), [`RakConnection.cs`](../src/RakDotNet/RakConnection.cs), [`ConnectOptions.cs`](../src/RakDotNet/ConnectOptions.cs), [`ConnectionState.cs`](../src/RakDotNet/ConnectionState.cs), [`RakPeerInterface.cs`](../src/RakDotNet.Compatibility/RakPeerInterface.cs) | M5 | Mapped onto the public API ([api-design.md](api-design.md) section 10) and mirrored by name in the compatibility facade, which keeps RakNet's quirks (decision 11.1) and adds `ConnectWithSocket`, `GetSocket`/`GetSockets`/`ReleaseSockets`, `AllocatePacket`, `SetUserUpdateThread` and `RunUpdateCycle` over `RakSocket`, `Inject`, `INetworkObserver.OnUpdateCycle` and `Update`. |
| `PluginInterface2` (`PluginInterface2.cpp`, `PluginInterface2.h`) | [`IRakPlugin.cs`](../src/RakDotNet/IRakPlugin.cs), [`IPluginHost.cs`](../src/RakDotNet/IPluginHost.cs), [`PluginCollection.cs`](../src/RakDotNet/PluginCollection.cs), [`INetworkObserver.cs`](../src/RakDotNet/INetworkObserver.cs), [`ConnectResult.cs`](../src/RakDotNet/ConnectResult.cs) | M5 | Hooks are `IRakPlugin`, the `*Unified` helpers are `IPluginHost`, and `UsesReliabilityLayer` is implementing `INetworkObserver`. |
| `RakNetDefines` (`RakNetDefines.h`) | [`RakNetConstants.cs`](../src/RakDotNet.Protocol/Wire/Messages/RakNetConstants.cs), [`PeerLimits.cs`](../src/RakDotNet/PeerLimits.cs), [`DatagramHistory.cs`](../src/RakDotNet.Protocol/Session/DatagramHistory.cs) | M2 | Behaviour-bearing defaults become constants; allocator, threading and assert macros are replaced by `ArrayPool`, `TimeProvider`, `Debug.Assert` and the peer threading options. |
| `RakNetDefinesOverrides` (`RakNetDefinesOverrides.h`) | `PeerLimits`, `RakPeerOptions` ([`PeerLimits.cs`](../src/RakDotNet/PeerLimits.cs), [`RakPeerOptions.cs`](../src/RakDotNet/RakPeerOptions.cs)) | M2 | Empty user-edit header; overrides are options objects. |
| `NativeFeatureIncludes` (`NativeFeatureIncludes.h`) | n/a | M8 | Compile-time feature switches; each plugin is an ordinary class and inclusion is by referencing its assembly. The default-enabled features are listed under milestone 8. |
| `NativeFeatureIncludesOverrides` (`NativeFeatureIncludesOverrides.h`) | n/a | M8 | Empty user-edit override header. |
| `GetTime` (`GetTime.cpp`, `GetTime.h`) | `System.TimeProvider`; [`RakPeer.cs`](../src/RakDotNet/RakPeer.cs), [`IPluginHost.cs`](../src/RakDotNet/IPluginHost.cs) | M5 | Monotonic clock is `TimeProvider.GetElapsedTime`; `RakPeer.GetTime()` returns milliseconds and plugins read it through `IPluginHost`. The origin is taken once per `TimeProvider`, as `initialTime` is once per process, so peers sharing a provider share a clock. |
| `PacketPool` (`PacketPool.h`) | `System.Buffers.ArrayPool<byte>`; [`OwnedPeerEvent.cs`](../src/RakDotNet/OwnedPeerEvent.cs) | M5 | Empty header; packet pooling is `PeerEvent`/`OwnedPeerEvent` over pooled buffers. |

## Sockets and platform

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `SocketLayer` (`SocketLayer.cpp`, `SocketLayer.h`) | `System.Net.Sockets.Socket`, `System.Net.NetworkInformation.NetworkInterface`, `System.Net.Dns`; [`UdpDatagramTransport.cs`](../src/RakDotNet/Transport/UdpDatagramTransport.cs), [`SocketBinding.cs`](../src/RakDotNet/SocketBinding.cs), [`LocalAddressList.cs`](../src/RakDotNet.Protocol/Peer/LocalAddressList.cs) | M5 | Static Berkeley helper class; `GetMyIP` is `Dns` plus `LocalAddressList.Build`, `GetLocalPort` is `Socket.LocalEndPoint`. |
| `RakNetSocket2` (`RakNetSocket2.cpp`, `RakNetSocket2.h`) | [`IDatagramTransport.cs`](../src/RakDotNet/IDatagramTransport.cs), [`UdpDatagramTransport.cs`](../src/RakDotNet/Transport/UdpDatagramTransport.cs), [`RakSocket.cs`](../src/RakDotNet/RakSocket.cs), [`SocketBinding.cs`](../src/RakDotNet/SocketBinding.cs), [`InMemoryNetwork.cs`](../src/RakDotNet.Testing/InMemoryNetwork.cs) | M5 | `RNS2_*` sockets become `IDatagramTransport` wrapped by `RakSocket`; the receive polling thread is the async receive loop in `RakPeer.Cycle`, which drops zero-length datagrams as `RecvFromLoopInt` does. `BindShared`'s 4-byte test send to the bound address is in `UdpDatagramTransport.TryBind` (`StartupError.SocketFailedTestSend`). The wire is IPv4 only (`RAKNET_SUPPORT_IPV6` is 0). |
| `RakNetSocket2_Berkley` (`RakNetSocket2_Berkley.cpp`) | `System.Net.Sockets.Socket`; [`UdpDatagramTransport.cs`](../src/RakDotNet/Transport/UdpDatagramTransport.cs), [`SocketBinding.cs`](../src/RakDotNet/SocketBinding.cs) | M5 | Socket options, bind and receive on `Socket`: 256 KiB receive and 16 KiB send buffers by default (`SocketBinding`), `SO_BROADCAST` on every socket, don't-fragment around MTU probes (`TrySendDoNotFragment`, which on Linux restores the socket's own `IP_MTU_DISCOVER` mode afterwards), and a bound 0.0.0.0 reported as 127.0.0.1 (`RakSocket.LocalAddress`). |
| `RakNetSocket2_Windows_Linux` (`RakNetSocket2_Windows_Linux.cpp`) | `System.Net.Dns`; [`LocalAddressList.cs`](../src/RakDotNet.Protocol/Peer/LocalAddressList.cs), [`RakPeer.cs`](../src/RakDotNet/RakPeer.cs) | M5 | `GetMyIP` is `Dns.GetHostAddresses` feeding `LocalAddressList.Build`. |
| `RakNetSocket2_Windows_Linux_360` (`RakNetSocket2_Windows_Linux_360.cpp`) | [`UdpDatagramTransport.cs`](../src/RakDotNet/Transport/UdpDatagramTransport.cs) | M5 | Send path with per-send TTL is `TrySend`, which sets and restores `Socket.Ttl`. |
| `RakNetSocket` (`RakNetSocket.cpp`, `RakNetSocket.h`) | [`RakSocket.cs`](../src/RakDotNet/RakSocket.cs), `System.Net.Sockets.Socket` | M5 | Entirely commented out in the C++; the live wrapper is `RakNetSocket2`. |
| `RakNetSocket2_Berkley_NativeClient` (`RakNetSocket2_Berkley_NativeClient.cpp`) | `System.Net.Dns` | n/a | Native Client domain-name helpers; resolution is `Dns.GetHostAddressesAsync` in `RakPeer`. |
| `RakNetSocket2_NativeClient` (`RakNetSocket2_NativeClient.cpp`) | n/a | n/a | Google Native Client socket variant. |
| `RakNetSocket2_WindowsStore8` (`RakNetSocket2_WindowsStore8.cpp`) | n/a | n/a | WinRT datagram socket variant. |
| `RakNetSocket2_360_720` (`RakNetSocket2_360_720.cpp`) | n/a | n/a | Xbox 360/720 socket variant; defines no symbols. |
| `RakNetSocket2_PS3_PS4` (`RakNetSocket2_PS3_PS4.cpp`) | n/a | n/a | PS3/PS4 socket variant; defines no symbols. |
| `RakNetSocket2_PS4` (`RakNetSocket2_PS4.cpp`) | n/a | n/a | PS4 socket variant; defines no symbols. |
| `RakNetSocket2_Vita` (`RakNetSocket2_Vita.cpp`) | n/a | n/a | PS Vita socket variant; defines no symbols. |
| `SocketDefines` (`SocketDefines.h`) | `System.Net.Sockets.Socket`, `System.Net.Sockets.SocketException` | M5 | Macro layer over the platform socket API. |
| `SocketIncludes` (`SocketIncludes.h`) | `System.Net.Sockets.Socket` | M5 | Per-platform socket header shims. |
| `WSAStartupSingleton` (`WSAStartupSingleton.cpp`, `WSAStartupSingleton.h`) | n/a | n/a | Windows socket-stack startup; .NET initialises it. |
| `WindowsIncludes` (`WindowsIncludes.h`) | `System.Net.Sockets` | n/a | Windows header ordering shim. |
| `PS3Includes` (`PS3Includes.h`) | n/a | n/a | PS3 include shim. |
| `PS4Includes` (`PS4Includes.cpp`, `PS4Includes.h`) | n/a | n/a | PS4 include shim. |
| `VitaIncludes` (`VitaIncludes.cpp`, `VitaIncludes.h`) | n/a | n/a | PS Vita include shim. |
| `XBox360Includes` (`XBox360Includes.h`) | n/a | n/a | Xbox 360 include shim. |
| `EmptyHeader` (`EmptyHeader.h`) | n/a | n/a | Comment-only placeholder header. |
| `Export` (`Export.h`) | n/a | n/a | `RAK_DLL_EXPORT` macros; replaced by C# access modifiers and assembly visibility. |
| `gettimeofday` (`gettimeofday.cpp`, `gettimeofday.h`) | `System.TimeProvider`, `System.DateTimeOffset` | n/a | Windows `gettimeofday` shim; the clock is the injected `TimeProvider`. |
| `RakThread` (`RakThread.cpp`, `RakThread.h`) | `System.Threading.Thread`; [`RakPeer.Cycle.cs`](../src/RakDotNet/RakPeer.Cycle.cs) | M5 | Thread creation; the I/O cycle runs on a named background thread, with no priority argument. |
| `SendToThread` (`SendToThread.cpp`, `SendToThread.h`) | `System.Threading.Thread`; [`RakPeer.Cycle.cs`](../src/RakDotNet/RakPeer.Cycle.cs) | n/a | Optional threaded send pool (`USE_THREADED_SEND`, disabled by default); sends run on the peer's I/O thread. |
| `SimpleTCPServer` (`SimpleTCPServer.h`) | n/a | n/a | Empty file holding only a comment; `System.Net.Sockets.TcpListener` is the .NET counterpart for the TCP work in M8.6. |

## Containers and utilities replaced by .NET

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `DS_BytePool` (`DS_BytePool.cpp`, `DS_BytePool.h`) | `System.Buffers.ArrayPool<byte>` | M6 | Pooled byte buffers. |
| `DS_Heap` (`DS_Heap.h`) | `System.Collections.Generic.PriorityQueue<TElement,TPriority>` | M4 | Used in [`OrderingChannels.cs`](../src/RakDotNet.Protocol/Session/OrderingChannels.cs) and [`SendQueue.cs`](../src/RakDotNet.Protocol/Session/SendQueue.cs). |
| `DS_List` (`DS_List.h`) | `System.Collections.Generic.List<T>` | n/a | Dynamic array. |
| `DS_Map` (`DS_Map.h`) | `SortedList<TKey,TValue>` (positional access), `SortedDictionary<TKey,TValue>`, `Dictionary<TKey,TValue>` | M2, M8 | Sorted map; `SortedList` is the one with index access. |
| `DS_QueueLinkedList` (`DS_QueueLinkedList.h`) | `Queue<T>`, `LinkedList<T>` | M8 | FIFO queue over a linked list. |
| `DS_Multilist` (`DS_Multilist.h`) | `List<T>`, `LinkedList<T>`, `Dictionary<TKey,TValue>` | n/a | Multi-list template. |
| `DS_OrderedList` (`DS_OrderedList.h`) | `SortedList<TKey,TValue>`, `SortedSet<T>`, `PriorityQueue<TElement,TPriority>` | n/a | Sorted array list; each use picks a BCL sorted collection. |
| `DS_Hash` (`DS_Hash.h`) | `System.Collections.Generic.Dictionary<TKey,TValue>` | M8.1 | Chained hash map. |
| `DS_LinkedList` (`DS_LinkedList.h`) | `LinkedList<T>`, `List<T>` | M4 | Circular doubly-linked list. |
| `DS_MemoryPool` (`DS_MemoryPool.h`) | `System.Buffers.ArrayPool<T>`; [`EventSegment.cs`](../src/RakDotNet/EventSegment.cs) | M6 | Fixed-block page allocator; pooled segments with generation handles. |
| `DS_Queue` (`DS_Queue.h`) | [`RingQueue.cs`](../src/RakDotNet.Protocol/Session/RingQueue.cs), `Queue<T>` | M4 | Growable array queue. |
| `DS_OrderedChannelHeap` (`DS_OrderedChannelHeap.h`) | [`OrderingChannels.cs`](../src/RakDotNet.Protocol/Session/OrderingChannels.cs), [`SendQueue.cs`](../src/RakDotNet.Protocol/Session/SendQueue.cs), `PriorityQueue<TElement,TPriority>` | M4 | Weighted per-channel heap; ordering and sending live in the two session types. |
| `DS_BinarySearchTree` (`DS_BinarySearchTree.h`) | `SortedSet<T>`, `SortedDictionary<TKey,TValue>` | n/a | Binary search tree. |
| `Rand` (`Rand.cpp`, `Rand.h`) | `System.Random`, `System.Security.Cryptography.RandomNumberGenerator`; [`RandomSource.cs`](../src/RakDotNet/RandomSource.cs), [`NetworkSimulator.cs`](../src/RakDotNet/Transport/NetworkSimulator.cs) | M5 | MT19937 is not wire-visible; GUID derivation and the network simulator use .NET randomness. |
| `RakSleep` (`RakSleep.cpp`, `RakSleep.h`) | `Task.Delay`, `Thread.Sleep` | n/a | Millisecond sleep; peer waits go through `TimeProvider`. |
| `SimpleMutex` (`SimpleMutex.cpp`, `SimpleMutex.h`) | `System.Threading.Lock` | M3 | Mutex wrapper. |
| `SignaledEvent` (`SignaledEvent.cpp`, `SignaledEvent.h`) | `System.Threading.ManualResetEventSlim`; [`RakPeer.Cycle.cs`](../src/RakDotNet/RakPeer.Cycle.cs) | n/a | Wake signal; the event is reset on wake, before the loop checks for shutdown, to match `WaitOnEvent`. |
| `LocklessTypes` (`LocklessTypes.cpp`, `LocklessTypes.h`) | `System.Threading.Interlocked` | M4 | Atomic increment and decrement on fields. |
| `RakAlloca` (`RakAlloca.h`) | `stackalloc`, `System.Span<T>` | n/a | Stack allocation. |
| `RakAssert` (`RakAssert.h`) | `System.Diagnostics.Debug.Assert`, `ArgumentException.ThrowIf*` | n/a | Assert macros. |
| `RakMemoryOverride` (`RakMemoryOverride.cpp`, `RakMemoryOverride.h`) | `System.GC`, `ArrayPool<T>`, `MemoryPool<T>` | n/a | Allocator hooks, off by default (`_USE_RAK_MEMORY_OVERRIDE`). |
| `RefCountedObj` (`RefCountedObj.h`) | `System.Object` (GC lifetime), `IDisposable` | n/a | Intrusive reference counter. |
| `RakNetSmartPtr` (`RakNetSmartPtr.h`) | managed references (GC) | M3 | Intrusive reference-counted pointer. |
| `ThreadPool` (`ThreadPool.h`) | `System.Threading.Tasks.Task`, `System.Threading.Channels.Channel<T>` | M8.5 | Worker pool used by `FileListTransfer` and `SendToThread`. |
| `SingleProducerConsumer` (`SingleProducerConsumer.h`) | `ConcurrentQueue<T>`, `Channel<T>` | M8 | Lock-free SPSC ring; users are `RakPeer`, `TCPInterface` and `ThreadsafePacketLogger`. |
| `FormatString` (`FormatString.cpp`, `FormatString.h`) | `string.Format`, interpolated strings | M8 | printf-style formatting into static buffers. |
| `Itoa` (`Itoa.cpp`, `Itoa.h`) | `int.ToString`, `Convert.ToString(int, toBase)` | M8 | Integer-to-string in bases 2 to 16. |
| `DR_SHA1` (`DR_SHA1.cpp`, `DR_SHA1.h`) | `System.Security.Cryptography.SHA1` | M4 | SHA-1 class; its live users in Source are unreferenced. |
| `CheckSum` (`CheckSum.cpp`, `CheckSum.h`) | n/a | n/a | Standalone checksum class that no other unit includes. |
| `LinuxStrings` (`LinuxStrings.cpp`, `LinuxStrings.h`) | `StringComparer.OrdinalIgnoreCase`, `string.ToLowerInvariant` | n/a | `_stricmp`, `_strnicmp` and `_strlwr` shims. |
| `Getche` (`Getche.cpp`, `Getche.h`) | `System.Console.ReadKey` | n/a | Unbuffered console character input for console tooling. |
| `Kbhit` (`Kbhit.h`) | `System.Console.KeyAvailable`, `System.Console.ReadKey` | n/a | Terminal `kbhit` and `getch` for samples. |
| `Gets` (`Gets.cpp`, `Gets.h`) | `System.Console.ReadLine` | n/a | `fgets` wrapper for console samples. |

## Security

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `SecureHandshake` (`SecureHandshake.cpp`, `SecureHandshake.h`) | [`ConnectSecurity.cs`](../src/RakDotNet/Security/ConnectSecurity.cs), [`ServerSecurity.cs`](../src/RakDotNet/Security/ServerSecurity.cs), [`RakKeyPair.cs`](../src/RakDotNet/Security/RakKeyPair.cs), [`RakPublicKey.cs`](../src/RakDotNet/Security/RakPublicKey.cs), [`PeerTypes.cs`](../src/RakDotNet.Protocol/Peer/PeerTypes.cs), [`Security/`](../src/RakDotNet.Protocol/Security/) | M7 | `LIBCAT_SECURITY` handshake over the `DependentExtensions/cat` primitives, ported bit-exact; entropy, threads and aligned allocation are `RandomNumberGenerator`, `Lock` and the GC. `ClientSecurity` is RequestedConnectionStruct's handshake state and `ServerSecurityOptions` InitializeSecurity's arguments; `PeerCore` runs them. `SecureHandshake.cpp` is the unit that compiles cat; the cat sources it includes are mapped below. |

### DependentExtensions/cat

`SecureHandshake.cpp` compiles these parts of `reference/RakNet/DependentExtensions/cat`, which C# comments cite relative to that folder. They live in `RakDotNet.Protocol.Security`, internal, and are checked against known answers from [`tools/cat-vectors`](../tools/cat-vectors/) ([golden-vectors.md](golden-vectors.md)). Units RakNet compiles but never calls (`Passwords`, `MurmurHash2`, `Strings`, `BigMontgomery`) have no counterpart.

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `EasyHandshake` (`crypt/tunnel/EasyHandshake.hpp`, `.cpp`) | [`EasyHandshake.cs`](../src/RakDotNet.Protocol/Security/EasyHandshake.cs) | M7 | `EasyHandshake`, `ServerEasyHandshake` and `ClientEasyHandshake` over the 256-bit curve; each object owns its curve registers and takes an `ISecureRandom` in place of its `FortunaOutput`. `IsValidPublicKey` is the point check the responder and initiator make of a key, which `RakPublicKey` and `RakKeyPair` apply on creation; `RakKeyPair.Generate` is `GenerateServerKey`. |
| `KeyAgreement` (`crypt/tunnel/KeyAgreement.hpp`, `.cpp`) | [`KeyAgreementCommon.cs`](../src/RakDotNet.Protocol/Security/KeyAgreementCommon.cs) | M7 | Curve constants and key generation; only the 256-bit curve is instantiated. |
| `KeyMaker` (`crypt/tunnel/KeyMaker.hpp`, `.cpp`) | [`KeyAgreementCommon.cs`](../src/RakDotNet.Protocol/Security/KeyAgreementCommon.cs) | M7 | Long-term key pair generation. |
| `KeyAgreementResponder` (`crypt/tunnel/KeyAgreementResponder.hpp`, `.cpp`) | [`KeyAgreementResponder.cs`](../src/RakDotNet.Protocol/Security/KeyAgreementResponder.cs) | M7 | Server side: challenge answer, ephemeral rekey after 100 challenges, initiator identity verification. `Sign` is not called by RakNet. |
| `KeyAgreementInitiator` (`crypt/tunnel/KeyAgreementInitiator.hpp`, `.cpp`) | [`KeyAgreementInitiator.cs`](../src/RakDotNet.Protocol/Security/KeyAgreementInitiator.cs) | M7 | Client side: challenge, answer check and identity proof. `Verify` is not called by RakNet. |
| `AuthenticatedEncryption` (`crypt/tunnel/AuthenticatedEncryption.hpp`, `.cpp`) | [`AuthenticatedEncryption.cs`](../src/RakDotNet.Protocol/Security/AuthenticatedEncryption.cs) | M7 | Key derivation, proofs, allocation-free datagram encrypt and decrypt with the 1024-IV replay window. A whole-word window shift gives 0 for the bits shifted in, as the native build does. |
| `CookieJar` (`crypt/cookie/CookieJar.hpp`, `.cpp`) | [`CookieJar.cs`](../src/RakDotNet.Protocol/Security/CookieJar.cs) | M7 | Salsa6 address cookies; the millisecond clock is a parameter. |
| `Skein` (`crypt/hash/Skein.hpp`, `Skein.cpp`, `Skein256.cpp`, `Skein512.cpp`) | [`Skein.cs`](../src/RakDotNet.Protocol/Security/Skein.cs) | M7 | Skein-256 in key, MAC and KDF modes with cat's per-crunch tweak counting. The 512-bit state and PRNG mode serve only Fortuna. |
| `HMAC_MD5` (`crypt/hash/HMAC_MD5.hpp`, `.cpp`) | [`HmacMd5.cs`](../src/RakDotNet.Protocol/Security/HmacMd5.cs) | M7 | cat's little-endian HMAC-MD5 with its own initial state and padding; a stack value type. |
| `ChaCha` (`crypt/symmetric/ChaCha.hpp`, `privatekey/ChaCha.cpp`) | [`ChaCha.cs`](../src/RakDotNet.Protocol/Security/ChaCha.cs) | M7 | 12-round ChaCha with cat's key schedule; stack value types. |
| `SecureCompare` (`crypt/SecureCompare.hpp`, `.cpp`) | `System.Security.Cryptography.CryptographicOperations.FixedTimeEquals` | M7 | Constant-time equality of equal-length buffers. |
| `Fortuna` (`crypt/rand/Fortuna.hpp`, `.cpp`, `Entropy*.cpp`), `IRandom` (`rand/IRandom.hpp`) | `System.Security.Cryptography.RandomNumberGenerator`; [`ISecureRandom.cs`](../src/RakDotNet.Protocol/Security/ISecureRandom.cs) | M7 | The CSPRNG's output is never on the wire. |
| `BigRTL` (`math/BigRTL.hpp`, `src/math/BigRTL.cpp`, `src/math/rtl/`) | [`BigRtl.cs`](../src/RakDotNet.Protocol/Security/BigRtl.cs) | M7 | Register file of 64-bit legs; add, subtract, multiply and `MulMod` division as cat computes them. |
| `BigPseudoMersenne` (`math/BigPseudoMersenne.hpp`, `src/math/BigPseudoMersenne.cpp`, `src/math/mersenne/`) | [`BigPseudoMersenne.cs`](../src/RakDotNet.Protocol/Security/BigPseudoMersenne.cs) | M7 | Arithmetic modulo 2^256 - 435 with cat's partial reductions. `MrSquareRoot` serves only `PtGenerate`, which RakNet does not call. |
| `BigTwistedEdwards` (`math/BigTwistedEdwards.hpp`, `src/math/BigTwistedEdwards.cpp`, `src/math/edward/`) | [`BigTwistedEdwards.cs`](../src/RakDotNet.Protocol/Security/BigTwistedEdwards.cs) | M7 | Extended twisted Edwards point arithmetic, validation and w-MOF single and simultaneous scalar multiplication. |
| `Platform`, `EndianNeutral`, `AlignedAlloc`, `BitMath`, `Legs`, `Clock`, `Mutex`, `Thread`, `WaitableFlag`, `Atomic`, `Singleton` | `System.Buffers.Binary.BinaryPrimitives`, `System.UInt128`, `System.Numerics.BitOperations`, the GC; [`AuthenticatedEncryption.cs`](../src/RakDotNet.Protocol/Security/AuthenticatedEncryption.cs) | M7 | Portability and threading support; `ReconstructCounter` from `BitMath.hpp` is `AuthenticatedEncryption.ReconstructCounter`. |

## Plugins

### 8.1 Diagnostics and small utilities

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `PacketLogger` (`PacketLogger.cpp`, `PacketLogger.h`) | per milestone 8.1 | M8.1 | Logs packets and connection events through a virtual `AddToLog`; base of the other loggers. |
| `ThreadsafePacketLogger` (`ThreadsafePacketLogger.cpp`, `ThreadsafePacketLogger.h`) | per milestone 8.1 | M8.1 | Logger with a locked sink; field list in [`INetworkObserver.cs`](../src/RakDotNet/INetworkObserver.cs). |
| `PacketFileLogger` (`PacketFileLogger.cpp`, `PacketFileLogger.h`) | per milestone 8.1 | M8.1 | Logger that writes to files. |
| `PacketOutputWindowLogger` (`PacketOutputWindowLogger.cpp`, `PacketOutputWindowLogger.h`) | per milestone 8.1 | M8.1 | Windows debugger sink (`OutputDebugString`); `Debug.WriteLine` is the .NET counterpart. |
| `ReadyEvent` (`ReadyEvent.cpp`, `ReadyEvent.h`) | per milestone 8.1 | M8.1 | Ready-state synchronisation plugin. |
| `MessageFilter` (`MessageFilter.cpp`, `MessageFilter.h`) | per milestone 8.1 | M8.1 | Per-connection message ID filtering plugin. |
| `StatisticsHistory` (`StatisticsHistory.cpp`, `StatisticsHistory.h`) | per milestone 8.1 | M8.1 | Time-series statistics plugin. |
| `TwoWayAuthentication` (`TwoWayAuthentication.cpp`, `TwoWayAuthentication.h`) | per milestone 8.1; wire IDs in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | M8.1 | Shared-key challenge plugin. Distinct from `ConnectSecurity.TwoWayAuthentication`, the M7 public-key mode. |
| `RandSync` (`RandSync.cpp`, `RandSync.h`) | per milestone 8.1 | M8.1 | Synchronised MT19937 generator with `Serialize` and `Deserialize`. |

### 8.2 Object replication and RPC

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `NetworkIDManager` (`NetworkIDManager.cpp`, `NetworkIDManager.h`) | per milestone 8.2 | M8.2 | Network object ID allocator. |
| `NetworkIDObject` (`NetworkIDObject.cpp`, `NetworkIDObject.h`) | per milestone 8.2 | M8.2 | Base class that carries a `NetworkID`. |
| `ReplicaManager3` (`ReplicaManager3.cpp`, `ReplicaManager3.h`) | per milestone 8.2; wire IDs in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | M8.2 | Object replication plugin with worlds, connections and `Replica3`. |
| `ReplicaEnums` (`ReplicaEnums.h`) | per milestone 8.2 | M8.2 | `REPLICA_*` flag values and `ReplicaReturnResult`. |
| `RPC4Plugin` (`RPC4Plugin.cpp`, `RPC4Plugin.h`) | per milestone 8.2 | M8.2 | Remote procedure calls by name. |
| `VariableListDeltaTracker` (`VariableListDeltaTracker.cpp`, `VariableListDeltaTracker.h`) | per milestone 8.2 | M8.2 | Dirty-flag tracker for delta serialisation. |
| `VariableDeltaSerializer` (`VariableDeltaSerializer.cpp`, `VariableDeltaSerializer.h`) | per milestone 8.2 | M8.2 | Per-variable delta serialiser. |
| `GridSectorizer` (`GridSectorizer.cpp`, `GridSectorizer.h`) | per milestone 8.2 | M8.2 | Grid spatial partitioner. |

### 8.3 Topology and teams

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `ConnectionGraph2` (`ConnectionGraph2.cpp`, `ConnectionGraph2.h`) | per milestone 8.3 | M8.3 | Participant and connection tracking; `OrderedList<RakNetGUID,...>` becomes a BCL keyed collection. |
| `FullyConnectedMesh2` (`FullyConnectedMesh2.cpp`, `FullyConnectedMesh2.h`) | per milestone 8.3 | M8.3 | Host election and participant list. |
| `TeamBalancer` (`TeamBalancer.cpp`, `TeamBalancer.h`) | per milestone 8.3; wire IDs in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | M8.3 | Team balancing plugin. |
| `TeamManager` (`TeamManager.cpp`, `TeamManager.h`) | per milestone 8.3 | M8.3 | Teams, members and join rules. |
| `DS_WeightedGraph` (`DS_WeightedGraph.h`) | per milestone 8.3 | M8.3 | Weighted graph used by `ConnectionGraph2`. |
| `DS_Tree` (`DS_Tree.h`) | per milestone 8.3 | M8.3 | N-ary tree included by `DS_WeightedGraph`. |

### 8.4 NAT traversal and relaying

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `NatPunchthroughClient` (`NatPunchthroughClient.cpp`, `NatPunchthroughClient.h`) | per milestone 8.4 | M8.4 | Punchthrough client plugin. |
| `NatPunchthroughServer` (`NatPunchthroughServer.cpp`, `NatPunchthroughServer.h`) | per milestone 8.4 | M8.4 | Punchthrough coordinator plugin. |
| `NatTypeDetectionClient` (`NatTypeDetectionClient.cpp`, `NatTypeDetectionClient.h`) | per milestone 8.4 | M8.4 | NAT type detection client. |
| `NatTypeDetectionServer` (`NatTypeDetectionServer.cpp`, `NatTypeDetectionServer.h`) | per milestone 8.4 | M8.4 | NAT type detection server. |
| `NatTypeDetectionCommon` (`NatTypeDetectionCommon.cpp`, `NatTypeDetectionCommon.h`) | per milestone 8.4 | M8.4 | Result enum, string helpers and non-blocking socket creation (`System.Net.Sockets.Socket`). |
| `UDPProxyClient` (`UDPProxyClient.cpp`, `UDPProxyClient.h`) | per milestone 8.4 | M8.4 | Proxy client plugin. |
| `UDPProxyCoordinator` (`UDPProxyCoordinator.cpp`, `UDPProxyCoordinator.h`) | per milestone 8.4 | M8.4 | Proxy coordinator plugin. |
| `UDPProxyServer` (`UDPProxyServer.cpp`, `UDPProxyServer.h`) | per milestone 8.4 | M8.4 | Proxy server plugin. |
| `UDPProxyCommon` (`UDPProxyCommon.h`) | per milestone 8.4; general ID in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | M8.4 | Proxy sub-message enum and forwarder timeout constant. |
| `UDPForwarder` (`UDPForwarder.cpp`, `UDPForwarder.h`) | per milestone 8.4 | M8.4 | Forwards UDP between addresses; mutexes and threads are `Lock` and `Thread`. |
| `Router2` (`Router2.cpp`, `Router2.h`) | per milestone 8.4 | M8.4 | Relays connections through a forwarder when punchthrough fails. |
| `RelayPlugin` (`RelayPlugin.cpp`, `RelayPlugin.h`) | per milestone 8.4; ID in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | M8.4 | Relay plugin. |
| `DS_ThreadsafeAllocatingQueue` (`DS_ThreadsafeAllocatingQueue.h`) | `System.Collections.Concurrent.ConcurrentQueue<T>`, `System.Threading.Channels.Channel<T>` | M8.4 | Mutex-guarded queue used by `UDPForwarder`. |

### 8.5 Files and cloud

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `FileList` (`FileList.cpp`, `FileList.h`) | per milestone 8.5 | M8.5 | File set with content hashes. |
| `FileListTransfer` (`FileListTransfer.cpp`, `FileListTransfer.h`) | per milestone 8.5 | M8.5 | Sends file lists and contents. |
| `FileListNodeContext` (`FileListNodeContext.h`) | per milestone 8.5 | M8.5 | Packet context struct with `BitStream` operators. |
| `FileListTransferCBInterface` (`FileListTransferCBInterface.h`) | per milestone 8.5 | M8.5 | Transfer progress and completion callbacks. |
| `DirectoryDeltaTransfer` (`DirectoryDeltaTransfer.cpp`, `DirectoryDeltaTransfer.h`) | per milestone 8.5 | M8.5 | Transfers changed files of a directory. |
| `IncrementalReadInterface` (`IncrementalReadInterface.cpp`, `IncrementalReadInterface.h`) | per milestone 8.5; `System.IO.FileStream` | M8.5 | File slice reader used by `FileListTransfer`. |
| `FileOperations` (`FileOperations.cpp`, `FileOperations.h`) | `System.IO.File`, `System.IO.Directory`, `System.IO.Path` | M8.5 | File and directory helpers. |
| `_FindFirst` (`_FindFirst.cpp`, `_FindFirst.h`) | `System.IO.Directory`, `System.IO.DirectoryInfo`, `System.IO.FileSystemName` | M8.5 | POSIX `_findfirst` shim; directory enumeration with simple-expression matching. |
| `SuperFastHash` (`SuperFastHash.cpp`, `SuperFastHash.h`) | per milestone 8.5; `System.HashCode` for address hashing | M8.5 | File hashes travel on the wire in `FileListTransfer` and are bit-exact; `SystemAddress` hashing uses `HashCode.Combine`. |
| `CloudCommon` (`CloudCommon.cpp`, `CloudCommon.h`) | per milestone 8.5 | M8.5 | Shared cloud key, query and result types. |
| `CloudClient` (`CloudClient.cpp`, `CloudClient.h`) | per milestone 8.5; wire IDs in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | M8.5 | Cloud client plugin. |
| `CloudServer` (`CloudServer.cpp`, `CloudServer.h`) | per milestone 8.5; wire IDs in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | M8.5 | Cloud server plugin. |

### 8.6 TCP, HTTP and console

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `TCPInterface` (`TCPInterface.cpp`, `TCPInterface.h`) | per milestone 8.6; `System.Net.Sockets` | M8.6 | TCP server and client. |
| `PacketizedTCP` (`PacketizedTCP.cpp`, `PacketizedTCP.h`) | per milestone 8.6 | M8.6 | Length-prefixed packets over TCP. |
| `DS_ByteQueue` (`DS_ByteQueue.cpp`, `DS_ByteQueue.h`) | `System.IO.Pipelines` or a byte ring over `ArrayPool<byte>` | M8.6 | Byte ring buffer used by the TCP classes. |
| `HTTPConnection` (`HTTPConnection.cpp`, `HTTPConnection.h`) | per milestone 8.6 | M8.6 | HTTP client over `TCPInterface`. |
| `HTTPConnection2` (`HTTPConnection2.cpp`, `HTTPConnection2.h`) | per milestone 8.6 | M8.6 | HTTP/1.1 client with chunked responses. |
| `ConsoleServer` (`ConsoleServer.cpp`, `ConsoleServer.h`) | per milestone 8.6 | M8.6 | Remote console over pluggable transports. |
| `CommandParserInterface` (`CommandParserInterface.cpp`, `CommandParserInterface.h`) | per milestone 8.6 | M8.6 | Base class for console command parsers. |
| `LogCommandParser` (`LogCommandParser.cpp`, `LogCommandParser.h`) | per milestone 8.6 | M8.6 | Log channel command parser. |
| `RakNetCommandParser` (`RakNetCommandParser.cpp`, `RakNetCommandParser.h`) | per milestone 8.6 | M8.6 | Peer command parser. |
| `TelnetTransport` (`TelnetTransport.cpp`, `TelnetTransport.h`) | per milestone 8.6 | M8.6 | Telnet transport for `ConsoleServer`. |
| `RakNetTransport2` (`RakNetTransport2.cpp`, `RakNetTransport2.h`) | per milestone 8.6 | M8.6 | Runs a `RakPeer` as a console transport. |
| `TransportInterface` (`TransportInterface.h`) | per milestone 8.6 | M8.6 | Abstract transport for `ConsoleServer`; distinct from `IDatagramTransport`. |
| `PacketConsoleLogger` (`PacketConsoleLogger.cpp`, `PacketConsoleLogger.h`) | per milestone 8.6 | M8.6 | `PacketLogger` that writes to a `LogCommandParser` channel. |

### 8.7 External services

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `DynDNS` (`DynDNS.cpp`, `DynDNS.h`) | per milestone 8.7 | M8.7 | Dynamic DNS updater over `TCPInterface`. |
| `EmailSender` (`EmailSender.cpp`, `EmailSender.h`) | per milestone 8.7 | M8.7 | SMTP client over `TCPInterface`. |
| `Rackspace` (`Rackspace.cpp`, `Rackspace.h`) | per milestone 8.7 | M8.7 | Rackspace Cloud Files client over `HTTPConnection`. |
| `Base64Encoder` (`Base64Encoder.cpp`, `Base64Encoder.h`) | `System.Convert.ToBase64String` | M8.7 | Base64 with CRLF line layout, used by `EmailSender`. |

## Units used only by DependentExtensions

These units have no consumer in `Source/` apart from each other; they serve the `DependentExtensions` and `Samples` trees.

| C++ unit (files) | C# location | Milestone | Note |
|---|---|---|---|
| `AutopatcherPatchContext` (`AutopatcherPatchContext.h`) | wire IDs in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | n/a | `PatchContext` enum for the Autopatcher. |
| `AutopatcherRepositoryInterface` (`AutopatcherRepositoryInterface.h`) | wire IDs in [`MessageId.cs`](../src/RakDotNet.Protocol/Wire/MessageId.cs) | n/a | Autopatcher repository interface. |
| `TableSerializer` (`TableSerializer.cpp`, `TableSerializer.h`) | n/a | n/a | `BitStream` serialiser for `DS_Table`. |
| `DS_Table` (`DS_Table.cpp`, `DS_Table.h`) | n/a | n/a | In-memory typed table with filter queries and sorting. |
| `DS_BPlusTree` (`DS_BPlusTree.h`) | `System.Collections.Generic.SortedDictionary<TKey,TValue>` | n/a | B+ tree used by `DS_Table`. |
| `VariadicSQLParser` (`VariadicSQLParser.cpp`, `VariadicSQLParser.h`) | n/a | n/a | printf-style SQL argument extraction; C# uses `params` arrays. |
| `EpochTimeToString` (`EpochTimeToString.cpp`, `EpochTimeToString.h`) | `System.DateTimeOffset`, `System.TimeZoneInfo` | n/a | Epoch time formatting for sample database code. |
