using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.FullyConnectedMesh2;

public sealed class FullyConnectedMesh2Tests : IDisposable
{
    private static readonly byte[] Internal =
    [
        (byte)MessageId.Fcm2RequestFcmguid,
        (byte)MessageId.Fcm2RespondConnectionCount,
        (byte)MessageId.Fcm2InformFcmguid,
        (byte)MessageId.Fcm2UpdateMinTotalConnectionCount,
    ];

    private readonly PluginMesh _mesh = new();
    private readonly List<(RakPeer Peer, Plugins.FullyConnectedMesh2 Mesh)> _systems = [];

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void Before_connecting_this_system_is_the_host_but_there_is_no_connected_host()
    {
        var (peer, mesh) = AddSystem();

        Assert.Equal(peer.RakNetGuid, mesh.HostSystem);
        Assert.True(mesh.IsHostSystem);
        Assert.Equal(RakNetGuid.Unassigned, mesh.ConnectedHost);
        Assert.False(mesh.IsConnectedHost);
        Assert.Equal(SystemAddress.Unassigned, mesh.ConnectedHostAddress);
        Assert.Equal([peer.RakNetGuid], mesh.GetHostOrder());
        Assert.Equal(0, mesh.ParticipantCount);
    }

    [Fact]
    public void The_system_running_longest_becomes_the_host_of_two()
    {
        var (older, olderMesh) = AddSystem();
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (newer, newerMesh) = AddSystem();

        _mesh.Connect(newer, older);
        _mesh.Settle();

        Assert.Equal(older.RakNetGuid, olderMesh.ConnectedHost);
        Assert.Equal(older.RakNetGuid, newerMesh.ConnectedHost);
        Assert.True(olderMesh.IsConnectedHost);
        Assert.False(newerMesh.IsHostSystem);
        Assert.Equal([older.RakNetGuid, newer.RakNetGuid], olderMesh.GetHostOrder());
        Assert.Equal([older.RakNetGuid, newer.RakNetGuid], newerMesh.GetHostOrder());
        Assert.Equal([newer.RakNetGuid], olderMesh.GetParticipantList());
        Assert.Equal([older.RakNetGuid], newerMesh.GetParticipantList());
        Assert.Equal(older.Sockets[0].LocalAddress, newerMesh.ConnectedHostAddress);
        // The older system answered 2 to the newer, which made its GUID's high half 2 (FullyConnectedMesh2.cpp:561-571).
        Assert.Equal(2u, olderMesh.TotalConnectionCount);
        Assert.Equal(2u, newerMesh.TotalConnectionCount);
    }

    [Fact]
    public void The_first_host_is_reported_once_from_the_host_with_an_unassigned_old_host()
    {
        var (older, _) = AddSystem();
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (newer, _) = AddSystem();

        _mesh.Connect(newer, older);
        _mesh.Settle();

        // FullyConnectedMesh2.cpp:437-451: ID_FCM2_NEW_HOST, then the old host, from the new host, generated locally, in
        // a packet of sizeof(MessageID)+sizeof(RakNetGUID) bytes.
        foreach (var peer in new[] { older, newer })
        {
            var newHost = Assert.Single(_mesh.Seen(peer), e => e.Id == (byte)MessageId.Fcm2NewHost);
            Assert.Equal([(byte)MessageId.Fcm2NewHost, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0], newHost.Data);
            Assert.Equal(older.RakNetGuid, newHost.RakNetGuid);
            Assert.True(newHost.IsLocal);
            Assert.DoesNotContain(_mesh.Seen(peer), e => Internal.Contains(e.Id));
        }
    }

    [Fact]
    public void A_chain_of_connections_becomes_a_full_mesh_with_one_host()
    {
        // Samples/Fully Connected Mesh: each peer connects to the prior one; ConnectionGraph2 tells each new system about
        // the others, and FullyConnectedMesh2 connects to them.
        var (first, firstMesh) = AddSystem(withGraph: true);
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (second, secondMesh) = AddSystem(withGraph: true);
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (third, thirdMesh) = AddSystem(withGraph: true);

        _mesh.Connect(second, first);
        _mesh.Settle();
        _mesh.Connect(third, second);
        _mesh.RunUntil(() => first.ConnectionCount == 2 && second.ConnectionCount == 2 && third.ConnectionCount == 2, "a full mesh");
        _mesh.Settle();

        var order = new[] { first.RakNetGuid, second.RakNetGuid, third.RakNetGuid };
        foreach (var mesh in new[] { firstMesh, secondMesh, thirdMesh })
        {
            Assert.Equal(first.RakNetGuid, mesh.ConnectedHost);
            Assert.Equal(order, mesh.GetHostOrder());
            Assert.Equal(2, mesh.ParticipantCount);
        }
    }

    [Fact]
    public void When_the_host_leaves_the_next_oldest_system_becomes_host()
    {
        var (first, _) = AddSystem(withGraph: true);
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (second, secondMesh) = AddSystem(withGraph: true);
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (third, thirdMesh) = AddSystem(withGraph: true);
        _mesh.Connect(second, first);
        _mesh.Settle();
        _mesh.Connect(third, second);
        _mesh.RunUntil(() => third.ConnectionCount == 2 && first.ConnectionCount == 2, "a full mesh");
        _mesh.Settle();

        first.Close(second.RakNetGuid, notify: true);
        first.Close(third.RakNetGuid, notify: true);
        _mesh.Settle();

        // FullyConnectedMesh2.cpp:363-375: the lost host's successor is pushed with the lost host as the old one.
        foreach (var (peer, mesh) in new[] { (second, secondMesh), (third, thirdMesh) })
        {
            Assert.Equal(second.RakNetGuid, mesh.ConnectedHost);
            var newHost = _mesh.Seen(peer).Last(e => e.Id == (byte)MessageId.Fcm2NewHost);
            Assert.Equal(second.RakNetGuid, newHost.RakNetGuid);
            Assert.Equal(first.RakNetGuid.Value, BinaryPrimitives.ReadUInt64BigEndian(newHost.Data.AsSpan(1)));
        }
        Assert.True(secondMesh.IsHostSystem);
    }

    [Fact]
    public void The_last_participant_leaving_makes_this_system_the_host()
    {
        var (older, _) = AddSystem();
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (newer, newerMesh) = AddSystem();
        _mesh.Connect(newer, older);
        _mesh.Settle();

        newer.Close(older.RakNetGuid, notify: false);
        _mesh.Settle();

        Assert.Equal(newer.RakNetGuid, newerMesh.ConnectedHost);
        var newHost = _mesh.Seen(newer).Last(e => e.Id == (byte)MessageId.Fcm2NewHost);
        Assert.Equal(newer.RakNetGuid, newHost.RakNetGuid);
        Assert.Equal(older.RakNetGuid.Value, BinaryPrimitives.ReadUInt64BigEndian(newHost.Data.AsSpan(1)));
    }

    [Fact]
    public void Without_auto_participation_a_connection_is_not_a_participant_until_added()
    {
        var (older, olderMesh) = AddSystem(autoParticipate: false);
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (newer, newerMesh) = AddSystem(autoParticipate: false);
        _mesh.Connect(newer, older);
        _mesh.Settle();
        Assert.Equal(0, olderMesh.ParticipantCount);
        Assert.Equal(RakNetGuid.Unassigned, newerMesh.ConnectedHost);

        // The request makes the other side add this system too (FullyConnectedMesh2.cpp:554).
        olderMesh.AddParticipant(newer.RakNetGuid);
        _mesh.Settle();

        Assert.True(newerMesh.HasParticipant(older.RakNetGuid));
        Assert.Equal(older.RakNetGuid, newerMesh.ConnectedHost);
        Assert.Equal(older.RakNetGuid, olderMesh.ConnectedHost);
    }

    [Fact]
    public void A_system_that_is_not_connected_is_not_added()
    {
        var (_, mesh) = AddSystem();

        mesh.AddParticipant(new RakNetGuid(42));

        Assert.Equal(0, mesh.ParticipantCount);
    }

    [Fact]
    public void Resetting_the_host_calculation_asks_the_participants_again()
    {
        var (older, olderMesh) = AddSystem();
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (newer, newerMesh) = AddSystem();
        _mesh.Connect(newer, older);
        _mesh.Settle();

        olderMesh.ResetHostCalculation();
        Assert.Equal(RakNetGuid.Unassigned, olderMesh.ConnectedHost);
        Assert.Equal(TimeSpan.Zero, olderMesh.ElapsedRuntime);
        _mesh.Settle();

        // The reset system now has the newer one's GUID with a higher count, so the newer one is host.
        Assert.Equal(newer.RakNetGuid, olderMesh.ConnectedHost);
        Assert.Equal(newer.RakNetGuid, newerMesh.ConnectedHost);
        Assert.Equal(3u, olderMesh.TotalConnectionCount);
    }

    [Fact]
    public void Only_the_first_system_of_a_remote_connection_list_is_connected_to()
    {
        var (peer, mesh) = AddSystem();
        var (target, _) = AddSystem();
        var (other, _) = AddSystem();
        mesh.ConnectOnNewRemoteConnection = false;

        // FullyConnectedMesh2.cpp:766-772 reads address and GUID without the ping ConnectionGraph2 writes, so the second
        // address is read from the ping's high byte (0, not version 4) and the first address is connected to again.
        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.RemoteNewIncomingConnection);
        writer.WriteUInt32(2);
        writer.WriteAddress(target.Sockets[0].LocalAddress);
        writer.WriteRakNetGuid(target.RakNetGuid);
        writer.WriteUInt16(0);
        writer.WriteAddress(other.Sockets[0].LocalAddress);
        writer.WriteRakNetGuid(other.RakNetGuid);
        writer.WriteUInt16(0);
        mesh.ConnectToRemoteNewIncomingConnections(writer.Written);
        _mesh.Settle();

        Assert.Equal([target.RakNetGuid], peer.Connections.Select(c => c.RakNetGuid));
    }

    [Fact]
    public void A_verified_join_admits_a_client_that_connects_to_every_participant()
    {
        var (host, hostMesh, member, memberMesh) = AddMesh();
        var (client, clientMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        hostMesh.VerifiedJoinStartUserData = (bits, guid) => bits.WriteUInt32((uint)guid.Value);
        clientMesh.VerifiedJoinCapableUserData = bits => bits.WriteUInt16(0xABCD);
        _mesh.Connect(client, host);
        _mesh.Settle();

        hostMesh.StartVerifiedJoin(client.RakNetGuid);
        _mesh.Settle();

        // ID_FCM2_VERIFIED_JOIN_START reaches the client, which connects to what is left to process.
        var start = Assert.Single(_mesh.Seen(client), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinStart);
        var required = Assert.Single(clientMesh.GetVerifiedJoinRequiredProcessingList(start.RakNetGuid));
        Assert.Equal(member.RakNetGuid, required.RakNetGuid);
        Assert.Equal(AddressAt(host, member), required.Address);
        Assert.Equal(32, required.UserDataBitLength);
        Assert.Equal(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((uint)member.RakNetGuid.Value)), required.UserData.ToArray());
        Assert.Equal(StartBytes(host, (member, [.. required.UserData.Span])), start.Data);
        client.Connect(required.Address);
        _mesh.Settle();

        // ID_FCM2_VERIFIED_JOIN_CAPABLE: GUID, address and JIPS_CONNECTED (2), then the user data.
        var capable = Assert.Single(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable);
        Assert.Equal(client.RakNetGuid, capable.RakNetGuid);
        var reader = new BitStreamReader(capable.Data);
        Assert.True(Plugins.FullyConnectedMesh2.SkipToVerifiedJoinCapableUserData(ref reader));
        Assert.True(reader.TryReadUInt16(out var userData));
        Assert.Equal(0xABCD, userData);
        Assert.Equal((byte)VerifiedJoinState.Connected, capable.Data[1 + 2 + 8 + 7]);

        hostMesh.RespondOnVerifiedJoinCapable(capable.Data, capable.Address, capable.RakNetGuid, accept: true, [7, 8]);
        _mesh.Settle();

        Assert.Equal(new[] { member.RakNetGuid, client.RakNetGuid }.Order(), hostMesh.GetParticipantList().Order());
        Assert.Equal(new[] { host.RakNetGuid, client.RakNetGuid }.Order(), memberMesh.GetParticipantList().Order());
        Assert.Equal(new[] { host.RakNetGuid, member.RakNetGuid }.Order(), clientMesh.GetParticipantList().Order());
        var accepted = Assert.Single(_mesh.Seen(client), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinAccepted);
        var read = clientMesh.GetVerifiedJoinAcceptedAdditionalData(accepted.Data, accepted.RakNetGuid);
        Assert.True(read.ThisSystemAccepted);
        Assert.Equal([member.RakNetGuid, host.RakNetGuid], read.SystemsAccepted);
        Assert.Equal([7, 8], read.AdditionalData.ToArray());
        // ID, client GUID, no systems to disconnect, the member reached, the additional data.
        Assert.Equal(AcceptedBytes(client, [], [member], [7, 8]), accepted.Data);
        var atMember = Assert.Single(_mesh.Seen(member), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinAccepted);
        Assert.Equal(accepted.Data, atMember.Data);
        var otherRead = memberMesh.GetVerifiedJoinAcceptedAdditionalData(atMember.Data, atMember.RakNetGuid);
        Assert.False(otherRead.ThisSystemAccepted);
        Assert.Equal([client.RakNetGuid], otherRead.SystemsAccepted);
        // The host gets the same message injected locally (FullyConnectedMesh2.cpp:881-887).
        var atHost = Assert.Single(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinAccepted);
        Assert.True(atHost.IsLocal);
        Assert.Equal(accepted.Data, atHost.Data);
        Assert.Equal(host.RakNetGuid, clientMesh.ConnectedHost);
    }

    [Fact]
    public void A_client_already_connected_to_every_participant_answers_at_once_and_the_start_is_consumed()
    {
        var (host, hostMesh, member, _) = AddMesh();
        var (client, clientMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(client, host);
        _mesh.Connect(client, member);
        _mesh.Settle();

        hostMesh.StartVerifiedJoin(client.RakNetGuid);
        _mesh.Settle();

        Assert.DoesNotContain(_mesh.Seen(client), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinStart);
        Assert.Single(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable);
        Assert.Empty(clientMesh.GetVerifiedJoinRequiredProcessingList(host.RakNetGuid));
    }

    [Fact]
    public void A_rejected_client_closes_the_connections_it_made_for_the_join()
    {
        var (host, hostMesh, member, _) = AddMesh();
        var (client, _) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(client, host);
        hostMesh.StartVerifiedJoin(client.RakNetGuid);
        _mesh.Settle();
        client.Connect(member.Sockets[0].LocalAddress);
        _mesh.Settle();
        var capable = Assert.Single(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable);

        hostMesh.RespondOnVerifiedJoinCapable(capable.Data, capable.Address, capable.RakNetGuid, accept: false, [9]);
        _mesh.Settle();

        var rejected = Assert.Single(_mesh.Seen(client), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinRejected);
        Assert.Equal([(byte)MessageId.Fcm2VerifiedJoinRejected, 9], rejected.Data);
        Assert.Equal([9], Plugins.FullyConnectedMesh2.GetVerifiedJoinRejectedAdditionalData(rejected.Data).ToArray());
        // FullyConnectedMesh2.cpp:1176-1188: the member is closed; the host is not.
        Assert.Equal([host.RakNetGuid], client.Connections.Select(c => c.RakNetGuid));
        Assert.Contains(_mesh.Seen(member), e => e.Id == (byte)MessageId.DisconnectionNotification && e.RakNetGuid == client.RakNetGuid);
        Assert.Equal(1, hostMesh.ParticipantCount);
    }

    [Fact]
    public void A_client_that_cannot_reach_a_participant_fails_the_join()
    {
        var (host, hostMesh, member, _) = AddMesh();
        member.MaxIncomingConnections = 0;
        var (client, _) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(client, host);
        hostMesh.StartVerifiedJoin(client.RakNetGuid);
        _mesh.Settle();

        client.Connect(member.Sockets[0].LocalAddress);
        _mesh.Settle();

        // ID_NO_FREE_INCOMING_CONNECTIONS marks the member failed; the host consumes the client's answer and replies
        // ID_FCM2_VERIFIED_JOIN_FAILED (FullyConnectedMesh2.cpp:1125-1132).
        Assert.Single(_mesh.Seen(client), e => e.Id == (byte)MessageId.NoFreeIncomingConnections);
        Assert.DoesNotContain(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable);
        var failed = Assert.Single(_mesh.Seen(client), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinFailed);
        Assert.Equal([(byte)MessageId.Fcm2VerifiedJoinFailed], failed.Data);
        Assert.Equal(host.RakNetGuid, failed.RakNetGuid);
        Assert.Equal(1, hostMesh.ParticipantCount);
    }

    [Fact]
    public void Losing_the_host_mid_join_fails_it_locally()
    {
        var (host, hostMesh, _, _) = AddMesh();
        var (client, clientMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(client, host);
        hostMesh.StartVerifiedJoin(client.RakNetGuid);
        _mesh.Settle();
        Assert.Single(clientMesh.GetVerifiedJoinRequiredProcessingList(host.RakNetGuid));

        client.Close(host.RakNetGuid, notify: false);
        _mesh.Settle();

        // FullyConnectedMesh2.cpp:324-332: two bytes, pushed at the head after the lost connection.
        var events = _mesh.Seen(client);
        var lost = events.FindIndex(e => e.Id == (byte)MessageId.ConnectionLost);
        var failed = events.FindIndex(e => e.Id == (byte)MessageId.Fcm2VerifiedJoinFailed);
        Assert.Equal(lost + 1, failed);
        Assert.Equal([(byte)MessageId.Fcm2VerifiedJoinFailed, 0], events[failed].Data);
        Assert.True(events[failed].IsLocal);
        Assert.Empty(clientMesh.GetVerifiedJoinRequiredProcessingList(host.RakNetGuid));
    }

    [Fact]
    public void Simultaneous_verified_joins_end_in_one_mesh()
    {
        // Samples/FCMVerifiedJoinSimultaneous: three clients connect to one system, which starts a verified join for
        // each at once; the clients connect to what they are told and the host accepts every capable client.
        var (host, hostMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        var clients = Enumerable.Range(0, 3).Select(_ => AddSystem(autoParticipate: false, connectOnNew: false)).ToList();
        foreach (var (client, _) in clients)
            _mesh.Connect(client, host);
        _mesh.Settle();
        foreach (var (client, _) in clients)
            hostMesh.StartVerifiedJoin(client.RakNetGuid);

        var handled = _systems.ToDictionary(s => s.Peer, _ => 0);
        _mesh.RunUntil(() =>
        {
            foreach (var (peer, mesh) in _systems)
            {
                var seen = _mesh.Seen(peer);
                for (; handled[peer] < seen.Count; handled[peer]++)
                {
                    var e = seen[handled[peer]];
                    if (e.Id == (byte)MessageId.Fcm2VerifiedJoinStart)
                    {
                        foreach (var system in mesh.GetVerifiedJoinRequiredProcessingList(e.RakNetGuid))
                            peer.Connect(system.Address);
                    }
                    else if (e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable)
                    {
                        mesh.RespondOnVerifiedJoinCapable(e.Data, e.Address, e.RakNetGuid, accept: true);
                    }
                }
            }
            return _systems.TrueForAll(s => s.Mesh.ParticipantCount == 3);
        }, "every system has the other three as participants");
        _mesh.Settle();

        var order = hostMesh.GetHostOrder();
        Assert.Equal(4, order.Count);
        Assert.All(_systems, s => Assert.Equal(order, s.Mesh.GetHostOrder()));
        Assert.All(_systems, s => Assert.Equal(3, s.Peer.ConnectionCount));
    }

    [Fact]
    public void Truncated_mesh_messages_are_consumed_and_change_nothing()
    {
        var (older, olderMesh) = AddSystem();
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (newer, _) = AddSystem();
        _mesh.Connect(newer, older);
        _mesh.Settle();
        var count = olderMesh.TotalConnectionCount;
        var order = olderMesh.GetHostOrder();

        byte[][] truncated =
        [
            [(byte)MessageId.Fcm2RequestFcmguid],
            // The bit saying a GUID follows, then a connection count cut short.
            [(byte)MessageId.Fcm2RequestFcmguid, 0x80, 0xFF, 0xFF],
            // No GUID, then an elapsed runtime cut short.
            [(byte)MessageId.Fcm2RequestFcmguid, 0x00, 0xFF, 0xFF, 0xFF],
            [(byte)MessageId.Fcm2RespondConnectionCount, 0xFF, 0xFF, 0xFF],
            [(byte)MessageId.Fcm2InformFcmguid, 0, 0, 0, 0, 0, 0, 0, 1, 0xFF, 0xFF],
            [(byte)MessageId.Fcm2UpdateMinTotalConnectionCount, 0xFF, 0xFF, 0xFF],
        ];
        foreach (var message in truncated)
            newer.Send(message, older.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal(count, olderMesh.TotalConnectionCount);
        Assert.Equal(order, olderMesh.GetHostOrder());
        Assert.Equal([newer.RakNetGuid], olderMesh.GetParticipantList());
        Assert.Equal(older.RakNetGuid, olderMesh.ConnectedHost);
        Assert.DoesNotContain(_mesh.Seen(older), e => Internal.Contains(e.Id));
    }

    [Fact]
    public void A_truncated_remote_connection_list_connects_only_to_complete_entries()
    {
        var (peer, _) = AddSystem();
        var (sender, _) = AddSystem();
        var (target, _) = AddSystem();
        _mesh.Connect(sender, peer);
        _mesh.Settle();

        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.RemoteNewIncomingConnection);
        writer.WriteUInt32(1);
        writer.WriteAddress(target.Sockets[0].LocalAddress);
        var cutMidAddress = writer.Written[..8].ToArray();
        sender.Send(new byte[] { (byte)MessageId.RemoteNewIncomingConnection, 0, 0 }, peer.RakNetGuid, Reliability.ReliableOrdered);
        sender.Send(cutMidAddress, peer.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal([sender.RakNetGuid], peer.Connections.Select(c => c.RakNetGuid));

        // A count far past the one entry that follows: the entry is connected to and the list ends with the message.
        writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.RemoteNewIncomingConnection);
        writer.WriteUInt32(uint.MaxValue);
        writer.WriteAddress(target.Sockets[0].LocalAddress);
        writer.WriteRakNetGuid(target.RakNetGuid);
        sender.Send(writer.Written.ToArray(), peer.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal(new[] { sender.RakNetGuid, target.RakNetGuid }.Order(), peer.Connections.Select(c => c.RakNetGuid).Order());
    }

    [Fact]
    public void A_truncated_verified_join_start_is_consumed_and_starts_no_join()
    {
        var (host, _, member, _) = AddMesh();
        var (client, clientMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(client, host);
        _mesh.Settle();

        var listCountPastEntries = StartBytes(host, (member, [1, 2]));
        listCountPastEntries[2] = 2;
        // The entry's bit count (after ID, count, GUID and address) says 64 bits; one byte follows.
        var bitCountPastEnd = StartBytes(host, (member, [0xAA]));
        BinaryPrimitives.WriteUInt32BigEndian(bitCountPastEnd.AsSpan(1 + 2 + 8 + 7), 64);
        byte[][] truncated =
        [
            [(byte)MessageId.Fcm2VerifiedJoinStart],
            [(byte)MessageId.Fcm2VerifiedJoinStart, 0],
            StartBytes(host, (member, [1, 2]))[..10],
            listCountPastEntries,
            bitCountPastEnd,
        ];
        foreach (var message in truncated)
            host.Send(message, client.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.DoesNotContain(_mesh.Seen(client), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinStart);
        Assert.Empty(clientMesh.GetVerifiedJoinRequiredProcessingList(host.RakNetGuid));
        Assert.DoesNotContain(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable);
        Assert.Equal([host.RakNetGuid], client.Connections.Select(c => c.RakNetGuid));
    }

    [Fact]
    public void A_truncated_update_to_a_verified_join_leaves_it_as_it_was()
    {
        var (host, _, member, _) = AddMesh();
        var (client, clientMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(client, host);
        host.Send(StartBytes(host, (member, [])), client.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();
        var required = Assert.Single(clientMesh.GetVerifiedJoinRequiredProcessingList(host.RakNetGuid));

        // An update listing two systems, only the first of which follows: applied, it would add that system and mark the
        // member unnecessary.
        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2VerifiedJoinStart);
        writer.WriteUInt16(2);
        writer.WriteRakNetGuid(new RakNetGuid(0x77));
        writer.WriteAddress(member.Sockets[0].LocalAddress);
        writer.WriteUInt32(0);
        host.Send(writer.Written.ToArray(), client.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal([required], clientMesh.GetVerifiedJoinRequiredProcessingList(host.RakNetGuid));
        Assert.Single(_mesh.Seen(client), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinStart);
        Assert.DoesNotContain(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable);
    }

    [Fact]
    public void A_truncated_verified_join_capable_is_consumed_without_an_answer()
    {
        var (host, hostMesh, member, _) = AddMesh();
        var (client, _) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(client, host);
        _mesh.Settle();

        byte[][] truncated =
        [
            [(byte)MessageId.Fcm2VerifiedJoinCapable],
            [(byte)MessageId.Fcm2VerifiedJoinCapable, 0],
            // One member listed, cut in its GUID.
            [(byte)MessageId.Fcm2VerifiedJoinCapable, 0, 1, .. BitConverter.GetBytes(member.RakNetGuid.Value)[..3]],
        ];
        foreach (var message in truncated)
            client.Send(message, host.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.DoesNotContain(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable);
        byte[] answers = [(byte)MessageId.Fcm2VerifiedJoinStart, (byte)MessageId.Fcm2VerifiedJoinFailed, (byte)MessageId.Fcm2VerifiedJoinAccepted, (byte)MessageId.Fcm2VerifiedJoinRejected];
        Assert.DoesNotContain(_mesh.Seen(client), e => answers.Contains(e.Id));
        Assert.Equal([member.RakNetGuid], hostMesh.GetParticipantList());
    }

    [Fact]
    public void A_truncated_verified_join_accepted_reaches_the_application_and_changes_nothing()
    {
        var (host, hostMesh, member, _) = AddMesh();
        var (client, clientMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(client, host);
        hostMesh.StartVerifiedJoin(client.RakNetGuid);
        _mesh.Settle();
        client.Connect(member.Sockets[0].LocalAddress);
        _mesh.Settle();
        var capable = Assert.Single(_mesh.Seen(host), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinCapable);

        // This system accepted, the member listed as a system to disconnect from, then the reached list cut short.
        var cut = AcceptedBytes(client, [member], [], [])[..^2];
        host.Send(cut, client.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Single(_mesh.Seen(client), e => e.Id == (byte)MessageId.Fcm2VerifiedJoinAccepted);
        Assert.Equal(2, client.ConnectionCount);
        Assert.Empty(clientMesh.GetParticipantList());

        // The join is still in progress: a real acceptance completes it.
        hostMesh.RespondOnVerifiedJoinCapable(capable.Data, capable.Address, capable.RakNetGuid, accept: true);
        _mesh.Settle();

        Assert.Equal(new[] { host.RakNetGuid, member.RakNetGuid }.Order(), clientMesh.GetParticipantList().Order());
    }

    private (RakPeer Peer, Plugins.FullyConnectedMesh2 Mesh) AddSystem(bool withGraph = false, bool autoParticipate = true, bool connectOnNew = true)
    {
        var mesh = new Plugins.FullyConnectedMesh2 { AutoParticipateConnections = autoParticipate, ConnectOnNewRemoteConnection = connectOnNew };
        // The samples attach FullyConnectedMesh2 before ConnectionGraph2.
        IRakPlugin[] plugins = withGraph ? [mesh, new Plugins.ConnectionGraph2()] : [mesh];
        var peer = _mesh.Add(4, plugins);
        _systems.Add((peer, mesh));
        return (peer, mesh);
    }

    // A host and one member that are participants of each other, the member having joined later.
    private (RakPeer Host, Plugins.FullyConnectedMesh2 HostMesh, RakPeer Member, Plugins.FullyConnectedMesh2 MemberMesh) AddMesh()
    {
        var (host, hostMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        var (member, memberMesh) = AddSystem(autoParticipate: false, connectOnNew: false);
        _mesh.Connect(member, host);
        hostMesh.AddParticipant(member.RakNetGuid);
        memberMesh.AddParticipant(host.RakNetGuid);
        _mesh.Settle();
        Assert.Equal(host.RakNetGuid, memberMesh.ConnectedHost);
        return (host, hostMesh, member, memberMesh);
    }

    private static SystemAddress AddressAt(RakPeer peer, RakPeer remote) => peer.GetAddressFromGuid(remote.RakNetGuid);

    // FullyConnectedMesh2.cpp:784-800: ID, uint16 count, then GUID, address, uint32 bit count, the bits, padded.
    private static byte[] StartBytes(RakPeer host, params (RakPeer Member, byte[] UserData)[] members)
    {
        var buffer = new byte[256];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2VerifiedJoinStart);
        writer.WriteUInt16((ushort)members.Length);
        foreach (var (member, userData) in members)
        {
            writer.WriteRakNetGuid(member.RakNetGuid);
            writer.WriteAddress(AddressAt(host, member));
            writer.WriteUInt32((uint)userData.Length * 8);
            writer.WriteBytes(userData);
        }
        return writer.Written.ToArray();
    }

    // FullyConnectedMesh2.cpp:858-872.
    private static byte[] AcceptedBytes(RakPeer client, RakPeer[] notParticipating, RakPeer[] reached, byte[] additional)
    {
        var buffer = new byte[256];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2VerifiedJoinAccepted);
        writer.WriteRakNetGuid(client.RakNetGuid);
        writer.WriteUInt16((ushort)notParticipating.Length);
        foreach (var peer in notParticipating)
            writer.WriteRakNetGuid(peer.RakNetGuid);
        writer.WriteUInt16((ushort)reached.Length);
        foreach (var peer in reached)
            writer.WriteRakNetGuid(peer.RakNetGuid);
        writer.WriteBytes(additional);
        return writer.Written.ToArray();
    }
}
