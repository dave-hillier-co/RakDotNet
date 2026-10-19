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

        public void OnConnected(RakConnection connection, bool isIncoming) => log.Add($"{name}.OnConnected({isIncoming})");

        public void OnDisconnected(RakConnection connection, DisconnectReason reason) => log.Add($"{name}.OnDisconnected({reason})");

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
