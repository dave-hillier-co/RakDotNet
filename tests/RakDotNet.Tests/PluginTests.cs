using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

public class PluginTests
{
    [Fact]
    public void Plugins_given_at_start_are_attached_then_started_and_shut_down_once()
    {
        var log = new List<string>();
        var plugin = new RecordingPlugin("a", log);
        var peers = new ManualPeers(client: o => o with { Plugins = [plugin] });

        Assert.Equal(["a.OnAttached", "a.OnStartup"], log);
        Assert.Single(peers.Client.Plugins);
        Assert.Same(peers.Client, plugin.Host!.Peer);
        peers.Dispose();
        peers.Client.Dispose();

        Assert.Equal(["a.OnAttached", "a.OnStartup", "a.OnShutdown"], log);
    }

    [Fact]
    public void Hooks_run_on_the_ordinary_list_before_the_reliability_layer_list_each_in_attach_order()
    {
        var log = new List<string>();
        using var peers = new ManualPeers();
        var observerPlugin = new ObservingPlugin("r", log);
        var first = new RecordingPlugin("a", log);
        var second = new RecordingPlugin("b", log);
        // RakPeer.cpp:2760-2781: a plugin that uses the reliability layer goes in the second list, whenever attached.
        peers.Client.Plugins.Attach(observerPlugin);
        peers.Client.Plugins.Attach(first);
        peers.Client.Plugins.Attach(second);
        Assert.Empty(log);

        peers.Client.SendLoopback([0x86]);
        peers.Client.Update();
        Assert.True(peers.Client.Events.TryRead(out _));

        Assert.Equal(
            [
                "r.OnAttached", "a.OnAttached", "b.OnAttached",
                "a.OnInjected", "b.OnInjected", "r.OnInjected",
                "a.OnUpdate", "b.OnUpdate", "r.OnUpdate",
                "a.OnEvent", "b.OnEvent", "r.OnEvent",
            ],
            log);
    }

    [Fact]
    public void A_consumed_event_stops_the_chain_and_is_not_returned()
    {
        var log = new List<string>();
        using var peers = new ManualPeers();
        var consumer = new RecordingPlugin("a", log) { Consume = e => e.Data[^1] == 1 };
        peers.Client.Plugins.Attach(consumer);
        peers.Client.Plugins.Attach(new RecordingPlugin("b", log));

        peers.Client.SendLoopback([0x86, 1]);
        peers.Client.SendLoopback([0x86, 2]);
        peers.Client.Update();
        Assert.True(peers.Client.Events.TryRead(out var e));

        Assert.Equal([0x86, 2], e.ToArray());
        Assert.Equal(["a.OnEvent", "a.OnEvent", "b.OnEvent"], log.Where(l => l.EndsWith(".OnEvent", StringComparison.Ordinal)));
    }

    [Fact]
    public void Connection_callbacks_follow_the_packets_RakNet_dispatches()
    {
        var log = new List<string>();
        var plugin = new RecordingPlugin("a", log);
        using var peers = new ManualPeers(client: o => o with { Plugins = [plugin] });

        var server = peers.Connect();
        server.Close(notify: false);
        peers.Step();

        Assert.Contains("a.OnConnected(False)", log);
        Assert.Contains("a.OnDisconnected(ConnectionLost)", log);
        Assert.True(log.IndexOf("a.OnConnected(False)") < log.IndexOf("a.OnDisconnected(ConnectionLost)"));
    }

    [Fact]
    public void Injected_connection_packets_with_no_connection_still_call_the_connection_callbacks()
    {
        var log = new List<string>();
        var connections = new List<RakConnection>();
        var plugin = new RecordingPlugin("a", log) { Connections = connections };
        using var peers = new ManualPeers(client: o => o with { Plugins = [plugin] });
        var source = new SystemAddress(System.Net.IPAddress.Parse("192.0.2.7"), 4321);
        var guid = new RakNetGuid(77);

        peers.Client.Inject([(byte)MessageId.NewIncomingConnection], source, guid);
        peers.Client.Inject([(byte)MessageId.ConnectionLost], source, guid);
        ManualPeers.Drain(peers.Client, peers.ClientEvents);

        // CallPluginCallbacks keys on the first byte alone, so a pushed-back packet calls OnNewConnection and
        // OnClosedConnection with its own address and GUID (RakPeer.cpp:6411-6428).
        Assert.Equal(["a.OnConnected(True)", "a.OnDisconnected(ConnectionLost)"], log.Where(l => l.Contains(".OnConnected", StringComparison.Ordinal) || l.Contains(".OnDisconnected", StringComparison.Ordinal)));
        Assert.All(connections, c => Assert.Equal(source, c.Address));
        Assert.All(connections, c => Assert.Equal(guid, c.RakNetGuid));
        Assert.All(connections, c => Assert.False(peers.Client.TryGetConnection(c.RakNetGuid, out _)));
        Assert.Equal(2, connections.Count);
    }

    [Fact]
    public void Abandoned_receipts_never_reach_plugins()
    {
        var log = new List<string>();
        var plugin = new RecordingPlugin("a", log);
        using var peers = new ManualPeers(client: o => o with { Plugins = [plugin] });
        var server = peers.Connect();
        peers.Settle();

        server.Send(new byte[] { 0x86 }, Reliability.ReliableWithAckReceipt);
        server.Close(notify: false);
        peers.Step();

        Assert.Contains(peers.ClientEvents, e => e.Kind == PeerEventKind.ReceiptAbandoned);
        Assert.DoesNotContain("a.OnEvent(ReceiptAbandoned)", log);
    }

    [Fact]
    public void A_plugin_detached_later_gets_OnDetached_at_the_next_dispatch_step()
    {
        var log = new List<string>();
        using var peers = new ManualPeers();
        var plugin = new RecordingPlugin("a", log);
        peers.Client.Plugins.Attach(plugin);
        peers.Client.Events.TryRead(out _);

        Assert.True(peers.Client.Plugins.Detach(plugin));
        peers.Client.Events.TryRead(out _);

        Assert.Equal(["a.OnAttached", "a.OnUpdate", "a.OnDetached"], log);
        Assert.Empty(peers.Client.Plugins);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var other = RakPeer.Start(peers.Options(0));
            peers.Client.Plugins.Attach(plugin);
            other.Plugins.Attach(plugin);
        });
    }

    [Fact]
    public void A_plugin_injecting_from_a_hook_acts_inside_the_step_and_sees_OnInjected()
    {
        var log = new List<string>();
        using var peers = new ManualPeers();
        var injector = new RecordingPlugin("a", log) { OnUpdateAction = host => host.Inject([0x90], SystemAddress.Unassigned, RakNetGuid.Unassigned) };
        peers.Client.Plugins.Attach(injector);

        Assert.True(peers.Client.Events.TryRead(out var e));

        Assert.Equal([0x90], e.ToArray());
        Assert.Contains("a.OnInjected", log);
    }

    [Fact]
    public void A_plugin_can_inject_a_packet_generated_locally()
    {
        // TwoWayAuthentication.cpp:265-273 and FullyConnectedMesh2.cpp:447 set wasGeneratedLocally before PushBackPacket.
        var log = new List<string>();
        using var peers = new ManualPeers();
        var seenLocal = new List<bool>();
        var injector = new RecordingPlugin("a", log)
        {
            OnUpdateAction = host => host.Inject([0x90], SystemAddress.Unassigned, new RakNetGuid(5), atHead: true, generatedLocally: true),
            Consume = e =>
            {
                seenLocal.Add(e.IsLocal);
                return false;
            },
        };
        peers.Client.Plugins.Attach(injector);

        Assert.True(peers.Client.Events.TryRead(out var e));

        Assert.Equal([0x90], e.ToArray());
        Assert.True(e.IsLocal);
        Assert.Equal([true], seenLocal);
    }

    [Fact]
    public void A_plugin_that_rewrites_the_id_passes_the_rewritten_packet_on()
    {
        // TwoWayAuthentication.cpp:375 rewrites packet->data[0] and returns RR_CONTINUE_PROCESSING.
        var log = new List<string>();
        using var peers = new ManualPeers();
        var seenByLater = new List<byte>();
        peers.Client.Plugins.Attach(new RecordingPlugin("a", log)
        {
            Consume = e =>
            {
                e.RewriteId((MessageId)0x87);
                return false;
            },
        });
        peers.Client.Plugins.Attach(new RecordingPlugin("b", log)
        {
            Consume = e =>
            {
                seenByLater.Add(e.Data[0]);
                return false;
            },
        });

        peers.Client.SendLoopback([0x86, 1]);
        peers.Client.Update();
        Assert.True(peers.Client.Events.TryRead(out var e));

        Assert.Equal([0x87, 1], e.ToArray());
        Assert.Equal(MessageId.UserPacketEnum + 1, e.Id);
        Assert.Equal(PeerEventKind.Message, e.Kind);
        Assert.Equal([0x87], seenByLater);
    }

    [Fact]
    public void A_plugin_that_rewrites_the_guid_passes_the_packet_on_with_it()
    {
        // Router2.cpp:493 sets packet->guid in OnFailedConnectionAttempt; later plugins and the reader see it.
        using var peers = new ManualPeers();
        var seenByLater = new List<RakNetGuid>();
        peers.Client.Plugins.Attach(new RecordingPlugin("a", [])
        {
            Consume = e =>
            {
                e.RewriteGuid(new RakNetGuid(0x1234));
                return false;
            },
        });
        peers.Client.Plugins.Attach(new RecordingPlugin("b", [])
        {
            Consume = e =>
            {
                seenByLater.Add(e.RakNetGuid);
                return false;
            },
        });

        peers.Client.SendLoopback([0x86, 1]);
        peers.Client.Update();
        Assert.True(peers.Client.Events.TryRead(out var e));

        Assert.Equal(new RakNetGuid(0x1234), e.RakNetGuid);
        Assert.Equal([new RakNetGuid(0x1234)], seenByLater);
        Assert.Equal([0x86, 1], e.ToArray());
    }

    [Fact]
    public void Rewriting_the_id_into_another_kind_of_event_is_refused()
    {
        using var peers = new ManualPeers();
        Exception? thrown = null;
        peers.Client.Plugins.Attach(new RecordingPlugin("a", [])
        {
            Consume = e =>
            {
                try
                {
                    e.RewriteId(MessageId.ConnectionLost);
                }
                catch (InvalidOperationException ex)
                {
                    thrown = ex;
                }
                return false;
            },
        });

        peers.Client.SendLoopback([0x86, 1]);
        peers.Client.Update();
        Assert.True(peers.Client.Events.TryRead(out var e));

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal([0x86, 1], e.ToArray());
        Assert.Equal(PeerEventKind.Message, e.Kind);
    }

    [Fact]
    public void Reading_from_inside_a_hook_is_refused()
    {
        using var peers = new ManualPeers();
        Exception? thrown = null;
        var plugin = new RecordingPlugin("a", []) { OnUpdateAction = host => thrown = Record.Exception(() => host.Peer!.Events.TryRead(out _)) };
        peers.Client.Plugins.Attach(plugin);

        peers.Client.Events.TryRead(out _);

        Assert.IsType<InvalidOperationException>(thrown);
    }

    private class RecordingPlugin(string name, List<string> log) : IRakPlugin
    {
        public IPluginHost? Host { get; private set; }

        public Func<PeerEvent, bool>? Consume { get; init; }

        public Action<IPluginHost>? OnUpdateAction { get; init; }

        public void OnAttached(IPluginHost host)
        {
            Host = host;
            log.Add($"{name}.OnAttached");
        }

        public void OnDetached() => log.Add($"{name}.OnDetached");

        public void OnStartup() => log.Add($"{name}.OnStartup");

        public void OnShutdown() => log.Add($"{name}.OnShutdown");

        public void OnUpdate()
        {
            log.Add($"{name}.OnUpdate");
            OnUpdateAction?.Invoke(Host!);
        }

        public List<RakConnection>? Connections { get; init; }

        public void OnConnected(RakConnection connection, bool isIncoming)
        {
            Connections?.Add(connection);
            log.Add($"{name}.OnConnected({isIncoming})");
        }

        public void OnDisconnected(RakConnection connection, DisconnectReason reason)
        {
            Connections?.Add(connection);
            log.Add($"{name}.OnDisconnected({reason})");
        }

        public PluginResult OnEvent(PeerEvent e)
        {
            log.Add($"{name}.OnEvent");
            if (e.Kind == PeerEventKind.ReceiptAbandoned)
                log.Add($"{name}.OnEvent(ReceiptAbandoned)");
            return Consume?.Invoke(e) == true ? PluginResult.Consume : PluginResult.Continue;
        }

        public void OnInjected(ReadOnlySpan<byte> data, int bitLength, SystemAddress source) => log.Add($"{name}.OnInjected");
    }

    private sealed class ObservingPlugin(string name, List<string> log) : RecordingPlugin(name, log), INetworkObserver;
}
