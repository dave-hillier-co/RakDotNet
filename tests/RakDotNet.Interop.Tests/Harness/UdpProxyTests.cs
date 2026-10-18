using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;

namespace RakDotNet.Interop.Tests.Harness;

public sealed class UdpProxyTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly UdpClient _server = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _client = new(new IPEndPoint(IPAddress.Loopback, 0));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IPEndPoint ServerEndPoint => (IPEndPoint)_server.Client.LocalEndPoint!;

    public void Dispose()
    {
        _server.Dispose();
        _client.Dispose();
    }

    private static async Task<UdpReceiveResult> ReceiveAsync(UdpClient socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Timeout);
        return await socket.ReceiveAsync(timeout.Token);
    }

    private static async Task<List<byte[]>> ReceiveForAsync(UdpClient socket, TimeSpan quietPeriod)
    {
        var received = new List<byte[]>();
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(quietPeriod);
            try
            {
                received.Add((await socket.ReceiveAsync(timeout.Token)).Buffer);
            }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested)
            {
                return received;
            }
        }
    }

    private static async Task WaitForRecordedAsync(UdpProxy proxy, int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Timeout);
        while (proxy.Datagrams.Count < count)
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    [Fact]
    public async Task Forwards_client_datagrams_to_the_server_and_replies_back_to_the_client()
    {
        await using var proxy = UdpProxy.Start(ServerEndPoint);

        await _client.SendAsync(new byte[] { 1, 2, 3 }, proxy.ListenEndPoint, Token);
        var atServer = await ReceiveAsync(_server);
        await _server.SendAsync(new byte[] { 4, 5 }, atServer.RemoteEndPoint, Token);
        var atClient = await ReceiveAsync(_client);

        Assert.Equal(new byte[] { 1, 2, 3 }, atServer.Buffer);
        Assert.Equal(new byte[] { 4, 5 }, atClient.Buffer);
        Assert.Equal(proxy.ListenEndPoint, atClient.RemoteEndPoint);
        Assert.NotEqual(_client.Client.LocalEndPoint, atServer.RemoteEndPoint);
    }

    [Fact]
    public async Task Each_client_endpoint_appears_at_the_server_as_a_distinct_address()
    {
        await using var proxy = UdpProxy.Start(ServerEndPoint);
        using var secondClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await _client.SendAsync(new byte[] { 1 }, proxy.ListenEndPoint, Token);
        var first = await ReceiveAsync(_server);
        await secondClient.SendAsync(new byte[] { 2 }, proxy.ListenEndPoint, Token);
        var second = await ReceiveAsync(_server);
        await _client.SendAsync(new byte[] { 3 }, proxy.ListenEndPoint, Token);
        var third = await ReceiveAsync(_server);

        Assert.NotEqual(first.RemoteEndPoint, second.RemoteEndPoint);
        Assert.Equal(first.RemoteEndPoint, third.RemoteEndPoint);
        await _server.SendAsync(new byte[] { 9 }, second.RemoteEndPoint, Token);
        Assert.Equal(new byte[] { 9 }, (await ReceiveAsync(secondClient)).Buffer);
    }

    [Fact]
    public async Task Records_every_datagram_with_direction_payload_and_time_since_the_first()
    {
        var time = new FakeTimeProvider();
        await using var proxy = UdpProxy.Start(ServerEndPoint, timeProvider: time);

        time.Advance(TimeSpan.FromSeconds(30));
        await _client.SendAsync(new byte[] { 0xaa }, proxy.ListenEndPoint, Token);
        var atServer = await ReceiveAsync(_server);
        time.Advance(TimeSpan.FromMilliseconds(7));
        await _server.SendAsync(new byte[] { 0xbb, 0xcc }, atServer.RemoteEndPoint, Token);
        await ReceiveAsync(_client);
        await WaitForRecordedAsync(proxy, 2);

        var recorded = proxy.Datagrams;
        Assert.Collection(
            recorded,
            d =>
            {
                Assert.Equal(0, d.Index);
                Assert.Equal(ProxyDirection.AToB, d.Direction);
                Assert.Equal(TimeSpan.Zero, d.Elapsed);
                Assert.Equal(new byte[] { 0xaa }, d.Payload);
                Assert.Equal(_client.Client.LocalEndPoint, d.Client);
            },
            d =>
            {
                Assert.Equal(1, d.Index);
                Assert.Equal(ProxyDirection.BToA, d.Direction);
                Assert.Equal(TimeSpan.FromMilliseconds(7), d.Elapsed);
                Assert.Equal(new byte[] { 0xbb, 0xcc }, d.Payload);
            });
    }

    [Fact]
    public void Fault_decisions_are_the_same_for_the_same_seed_and_differ_for_another_seed()
    {
        var options = new FaultOptions(Seed: 42, LossProbability: 0.3, DuplicationProbability: 0.3, Delay: TimeSpan.FromMilliseconds(5), Jitter: TimeSpan.FromMilliseconds(50));

        var first = Enumerable.Range(0, 200).Select(i => options.Decide(ProxyDirection.AToB, i)).ToList();
        var again = Enumerable.Range(0, 200).Select(i => (options with { }).Decide(ProxyDirection.AToB, i)).ToList();
        var otherSeed = Enumerable.Range(0, 200).Select(i => (options with { Seed = 43 }).Decide(ProxyDirection.AToB, i)).ToList();
        var otherDirection = Enumerable.Range(0, 200).Select(i => options.Decide(ProxyDirection.BToA, i)).ToList();

        Assert.Equal(first, again);
        Assert.NotEqual(first, otherSeed);
        Assert.NotEqual(first, otherDirection);
    }

    [Fact]
    public void Fault_decisions_follow_the_configured_probabilities_and_delay_range()
    {
        var options = new FaultOptions(Seed: 7, LossProbability: 0.2, DuplicationProbability: 0.1, Delay: TimeSpan.FromMilliseconds(10), Jitter: TimeSpan.FromMilliseconds(20));

        var decisions = Enumerable.Range(0, 10_000).Select(i => options.Decide(ProxyDirection.AToB, i)).ToList();

        Assert.InRange(decisions.Count(d => d.Drop), 1800, 2200);
        Assert.InRange(decisions.Count(d => d.Duplicate), 800, 1200);
        Assert.All(decisions, d => Assert.InRange(d.Delay, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30)));
        Assert.True(decisions.Select(d => d.Delay).Distinct().Count() > 100);
    }

    [Fact]
    public void No_faults_never_drops_duplicates_or_delays()
    {
        var decisions = Enumerable.Range(0, 1000).Select(i => FaultOptions.None.Decide(ProxyDirection.BToA, i));

        Assert.All(decisions, d => Assert.Equal(new FaultDecision(false, false, TimeSpan.Zero), d));
    }

    [Fact]
    public async Task Seeded_loss_drops_exactly_the_datagrams_the_decisions_say()
    {
        var faults = new FaultOptions(Seed: 1234, LossProbability: 0.5);
        await using var proxy = UdpProxy.Start(ServerEndPoint, faults);
        var expected = Enumerable.Range(0, 40).Where(i => !faults.Decide(ProxyDirection.AToB, i).Drop).Select(i => (byte)i).ToList();
        Assert.InRange(expected.Count, 5, 35);

        for (var i = 0; i < 40; i++)
        {
            await _client.SendAsync(new[] { (byte)i }, proxy.ListenEndPoint, Token);
            await WaitForRecordedAsync(proxy, i + 1);
        }
        var received = await ReceiveForAsync(_server, TimeSpan.FromMilliseconds(300));

        Assert.Equal(expected, received.Select(d => d[0]));
        Assert.Equal(40, proxy.Datagrams.Count);
        Assert.Equal(40 - expected.Count, proxy.Datagrams.Count(d => d.Fault.Drop));
    }

    [Fact]
    public async Task Duplication_delivers_each_datagram_twice()
    {
        await using var proxy = UdpProxy.Start(ServerEndPoint, new FaultOptions(Seed: 1, DuplicationProbability: 1.0));

        for (var i = 0; i < 5; i++)
        {
            await _client.SendAsync(new[] { (byte)i }, proxy.ListenEndPoint, Token);
            await WaitForRecordedAsync(proxy, i + 1);
        }
        var received = await ReceiveForAsync(_server, TimeSpan.FromMilliseconds(300));

        Assert.Equal(new byte[] { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4 }, received.Select(d => d[0]));
    }

    [Fact]
    public async Task Delay_with_jitter_releases_datagrams_in_order_of_their_seeded_due_time()
    {
        var time = new FakeTimeProvider();
        var faults = new FaultOptions(Seed: 99, Delay: TimeSpan.FromMilliseconds(10), Jitter: TimeSpan.FromMilliseconds(100));
        await using var proxy = UdpProxy.Start(ServerEndPoint, faults, time);
        var expected = Enumerable.Range(0, 10).OrderBy(i => faults.Decide(ProxyDirection.AToB, i).Delay).Select(i => (byte)i).ToList();
        Assert.NotEqual(Enumerable.Range(0, 10).Select(i => (byte)i), expected);

        for (var i = 0; i < 10; i++)
        {
            await _client.SendAsync(new[] { (byte)i }, proxy.ListenEndPoint, Token);
            await WaitForRecordedAsync(proxy, i + 1);
        }
        var beforeDue = await ReceiveForAsync(_server, TimeSpan.FromMilliseconds(100));
        time.Advance(TimeSpan.FromMilliseconds(110));
        var received = await ReceiveForAsync(_server, TimeSpan.FromMilliseconds(300));

        Assert.Empty(beforeDue);
        Assert.Equal(expected, received.Select(d => d[0]));
    }

    [Fact]
    public async Task Faults_can_be_switched_on_after_start()
    {
        await using var proxy = UdpProxy.Start(ServerEndPoint);

        await _client.SendAsync(new byte[] { 1 }, proxy.ListenEndPoint, Token);
        await ReceiveAsync(_server);
        proxy.Faults = new FaultOptions(LossProbability: 1.0);
        await _client.SendAsync(new byte[] { 2 }, proxy.ListenEndPoint, Token);
        await WaitForRecordedAsync(proxy, 2);
        var afterSwitch = await ReceiveForAsync(_server, TimeSpan.FromMilliseconds(200));

        Assert.Empty(afterSwitch);
    }
}
