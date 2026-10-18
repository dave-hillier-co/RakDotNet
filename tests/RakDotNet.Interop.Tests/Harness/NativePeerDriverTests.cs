using System.Net;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Runs NativePeerDriver against RakDotNet.Interop.FakePeer, which speaks the same JSON Lines protocol
/// without RakNet, so the protocol handling is tested on machines without the native binary.
/// </summary>
public sealed class NativePeerDriverTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string FakePeerPath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RakDotNet.Interop.FakePeer.exe" : "RakDotNet.Interop.FakePeer");

    private static Task<NativePeerDriver> LaunchFakeAsync(params string[] arguments) =>
        NativePeerDriver.LaunchAsync(FakePeerPath, arguments, TestContext.Current.CancellationToken);

    private static async Task<JsonArray> HistoryAsync(NativePeerDriver driver)
    {
        var reply = await driver.SendCommandAsync("history", new JsonObject(), TestContext.Current.CancellationToken);
        return reply["commands"]!.AsArray();
    }

    [Fact]
    public async Task Launch_waits_for_the_ready_event_and_reports_versions()
    {
        await using var driver = await LaunchFakeAsync();

        Assert.Equal("native", driver.Kind);
        Assert.Equal("4.081", driver.RakNetVersion);
        Assert.Equal(6, driver.ProtocolVersion);
    }

    [Fact]
    public async Task Startup_sends_the_options_and_returns_the_bound_address_and_a_guid_beyond_double_precision()
    {
        await using var driver = await LaunchFakeAsync();

        var started = await driver.StartupAsync(new StartupOptions(Port: 0, MaxConnections: 4, MaxIncoming: 2), TestContext.Current.CancellationToken);

        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 40000), started.BoundAddress);
        Assert.Equal(18446744073709551000UL, started.PeerGuid);
        var command = (await HistoryAsync(driver))[0]!;
        Assert.Equal("startup", (string?)command["cmd"]);
        Assert.Equal(0, (int?)command["port"]);
        Assert.Equal("127.0.0.1", (string?)command["host"]);
        Assert.Equal(4, (int?)command["maxConnections"]);
        Assert.Equal(2, (int?)command["maxIncoming"]);
        Assert.Equal(1, (long?)command["seq"]);
    }

    [Fact]
    public async Task Send_uses_hex_data_decimal_guids_and_the_cpp_enum_names()
    {
        await using var driver = await LaunchFakeAsync();

        var receipt = await driver.SendAsync(
            new SendOptions(18446744073709551000UL, new byte[] { 0x86, 0x01, 0xff }, Reliability.ReliableOrderedWithAckReceipt, Priority.Immediate, Channel: 3),
            TestContext.Current.CancellationToken);

        Assert.Equal(1u, receipt);
        var command = (await HistoryAsync(driver))[0]!;
        Assert.Equal("send", (string?)command["cmd"]);
        Assert.Equal("18446744073709551000", (string?)command["target"]);
        Assert.Equal("8601ff", (string?)command["data"]);
        Assert.Equal("RELIABLE_ORDERED_WITH_ACK_RECEIPT", (string?)command["reliability"]);
        Assert.Equal("IMMEDIATE_PRIORITY", (string?)command["priority"]);
        Assert.Equal(3, (int?)command["channel"]);
        Assert.False((bool?)command["broadcast"]);
    }

    [Theory]
    [InlineData(Reliability.Unreliable, "UNRELIABLE")]
    [InlineData(Reliability.UnreliableSequenced, "UNRELIABLE_SEQUENCED")]
    [InlineData(Reliability.Reliable, "RELIABLE")]
    [InlineData(Reliability.ReliableOrdered, "RELIABLE_ORDERED")]
    [InlineData(Reliability.ReliableSequenced, "RELIABLE_SEQUENCED")]
    [InlineData(Reliability.UnreliableWithAckReceipt, "UNRELIABLE_WITH_ACK_RECEIPT")]
    [InlineData(Reliability.ReliableWithAckReceipt, "RELIABLE_WITH_ACK_RECEIPT")]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, "RELIABLE_ORDERED_WITH_ACK_RECEIPT")]
    public void Reliability_maps_to_its_cpp_name(Reliability reliability, string expected)
    {
        Assert.Equal(expected, NativePeerDriver.ToProtocolName(reliability));
    }

    [Theory]
    [InlineData(Priority.Immediate, "IMMEDIATE_PRIORITY")]
    [InlineData(Priority.High, "HIGH_PRIORITY")]
    [InlineData(Priority.Medium, "MEDIUM_PRIORITY")]
    [InlineData(Priority.Low, "LOW_PRIORITY")]
    public void Priority_maps_to_its_cpp_name(Priority priority, string expected)
    {
        Assert.Equal(expected, NativePeerDriver.ToProtocolName(priority));
    }

    [Fact]
    public async Task Connect_returns_the_attempt_result_and_surfaces_the_accepted_packet()
    {
        await using var driver = await LaunchFakeAsync();

        var result = await driver.ConnectAsync(new ConnectOptions(new IPEndPoint(IPAddress.Loopback, 1234)), TestContext.Current.CancellationToken);

        Assert.Equal("CONNECTION_ATTEMPT_STARTED", result);
        var packet = await driver.Packets.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(16, packet.MessageId);
        Assert.Equal("ID_CONNECTION_REQUEST_ACCEPTED", packet.MessageName);
        Assert.Equal(42UL, packet.PeerGuid);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 1234), packet.Address);
        Assert.Equal(new byte[] { 0x10 }, packet.Data.ToArray());
        Assert.Equal(1000, packet.TimeMs);
        Assert.Null(packet.Timestamp);
        var command = (await HistoryAsync(driver))[0]!;
        Assert.Equal("127.0.0.1", (string?)command["host"]);
        Assert.Equal(1234, (int?)command["port"]);
        Assert.Equal("", (string?)command["password"]);
        Assert.Equal(12, (int?)command["attempts"]);
        Assert.Equal(500, (int?)command["attemptIntervalMs"]);
        Assert.Equal(0, (int?)command["timeoutMs"]);
    }

    [Fact]
    public async Task Ping_reports_the_pong_with_the_offline_ping_response()
    {
        await using var driver = await LaunchFakeAsync();

        await driver.SetOfflinePingResponseAsync("hello"u8.ToArray(), TestContext.Current.CancellationToken);
        var sent = await driver.PingAsync(new IPEndPoint(IPAddress.Loopback, 19132), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(sent);
        var packet = await driver.Packets.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ID_UNCONNECTED_PONG", packet.MessageName);
        Assert.Equal("\u001chello"u8.ToArray(), packet.Data.ToArray());
    }

    [Fact]
    public async Task Packet_events_with_a_timestamp_carry_it_as_an_unsigned_64_bit_value()
    {
        await using var driver = await LaunchFakeAsync();
        var line = """{"event":"packet","messageId":134,"messageName":"ID_USER_PACKET_ENUM","guid":"7","address":"127.0.0.1:5","data":"1b00000000000000ff86","timeMs":3,"timestamp":"18446744073709551615"}""";

        await driver.SendCommandAsync("emit", new JsonObject { ["line"] = line }, TestContext.Current.CancellationToken);

        var packet = await driver.Packets.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(134, packet.MessageId);
        Assert.Equal(ulong.MaxValue, packet.Timestamp);
    }

    [Fact]
    public async Task Replies_are_correlated_by_seq_when_they_arrive_out_of_order()
    {
        await using var driver = await LaunchFakeAsync();
        var token = TestContext.Current.CancellationToken;

        var slow = driver.SendCommandAsync("delayed", new JsonObject { ["ms"] = 300 }, token);
        var fast = driver.SendCommandAsync("delayed", new JsonObject { ["ms"] = 0 }, token);

        var first = await Task.WhenAny(slow, fast);
        Assert.Same(fast, first);
        Assert.Equal(0, (int?)(await fast)["delayedMs"]);
        Assert.Equal(300, (int?)(await slow)["delayedMs"]);
    }

    [Fact]
    public async Task A_failed_reply_throws_with_the_peer_error()
    {
        await using var driver = await LaunchFakeAsync();

        var error = await Assert.ThrowsAsync<PeerCommandException>(
            () => driver.SendCommandAsync("nonsense", new JsonObject(), TestContext.Current.CancellationToken));

        Assert.Equal("nonsense", error.Command);
        Assert.Contains("unknown command 'nonsense'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_commands_fail_with_the_exit_code_and_stderr_when_the_process_exits()
    {
        await using var driver = await LaunchFakeAsync();
        var token = TestContext.Current.CancellationToken;

        var pending = driver.SendCommandAsync("delayed", new JsonObject { ["ms"] = 5000 }, token);
        var crash = driver.SendCommandAsync("crash", new JsonObject { ["code"] = 7 }, token);

        var error = await Assert.ThrowsAsync<PeerProcessExitedException>(() => pending);
        await Assert.ThrowsAsync<PeerProcessExitedException>(() => crash);
        Assert.Equal(7, error.ExitCode);
        Assert.Contains("fake peer crashing with code 7", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_after_the_process_exits_fail_immediately_and_the_packet_channel_completes()
    {
        await using var driver = await LaunchFakeAsync();
        var token = TestContext.Current.CancellationToken;

        await driver.ShutdownAsync(cancellationToken: token);
        await driver.Packets.Completion.WaitAsync(Timeout, token);

        await Assert.ThrowsAsync<PeerProcessExitedException>(() => driver.GetConnectionStateAsync(1, token));
    }

    [Fact]
    public async Task Stderr_is_captured_for_diagnostics()
    {
        await using var driver = await LaunchFakeAsync();

        await driver.SendCommandAsync("delayed", new JsonObject { ["ms"] = 0 }, TestContext.Current.CancellationToken);

        Assert.Contains("fake peer ready", driver.StandardError);
    }

    [Fact]
    public async Task Launch_fails_with_stderr_when_the_process_exits_before_ready()
    {
        var error = await Assert.ThrowsAsync<PeerProcessExitedException>(() => LaunchFakeAsync("--fail-on-start"));

        Assert.Equal(3, error.ExitCode);
        Assert.Contains("fake peer refused to start", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_lines_are_kept_as_diagnostics_without_breaking_the_session()
    {
        await using var driver = await LaunchFakeAsync();
        var token = TestContext.Current.CancellationToken;

        await driver.SendCommandAsync("emit", new JsonObject { ["line"] = "this is not json" }, token);
        var state = await driver.GetConnectionStateAsync(5, token);

        Assert.Equal("IS_CONNECTED", state);
        Assert.Contains(driver.Diagnostics, d => d.Contains("this is not json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"event":"packet","guid":"1","data":"10"}""")]
    [InlineData("""{"event":"packet","messageId":16,"guid":"1","data":"zz"}""")]
    [InlineData("""{"event":"packet","messageId":16,"guid":"1","data":"10","timestamp":"not a number"}""")]
    [InlineData("""{"seq":"abc","ok":true}""")]
    public async Task Malformed_protocol_messages_are_kept_as_diagnostics_without_breaking_the_session(string line)
    {
        var driver = await LaunchFakeAsync();
        var token = TestContext.Current.CancellationToken;

        await driver.SendCommandAsync("emit", new JsonObject { ["line"] = line }, token);
        var state = await driver.GetConnectionStateAsync(5, token);

        Assert.Equal("IS_CONNECTED", state);
        Assert.Contains(driver.Diagnostics, d => d.Contains(line, StringComparison.Ordinal));
        await driver.DisposeAsync().AsTask().WaitAsync(Timeout, token);
    }

    [Fact]
    public async Task A_command_cancelled_before_it_is_written_is_not_left_pending()
    {
        await using var driver = await LaunchFakeAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => driver.SendCommandAsync("delayed", new JsonObject { ["ms"] = 0 }, cancelled.Token));

        Assert.Equal(0, driver.PendingCommandCount);
    }

    [Fact]
    public async Task Log_events_are_kept_as_diagnostics()
    {
        await using var driver = await LaunchFakeAsync();

        await driver.SendCommandAsync("emit", new JsonObject { ["line"] = """{"event":"log","message":"hi there"}""" }, TestContext.Current.CancellationToken);

        Assert.Contains(driver.Diagnostics, d => d.Contains("hi there", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_kills_a_peer_that_does_not_exit_on_its_own()
    {
        var driver = await LaunchFakeAsync();
        var token = TestContext.Current.CancellationToken;
        // A reply delayed this long keeps the fake alive after stdin closes.
        _ = driver.SendCommandAsync("delayed", new JsonObject { ["ms"] = 60_000 }, token);

        var disposing = driver.DisposeAsync().AsTask();

        await disposing.WaitAsync(Timeout, token);
        Assert.True(driver.HasExited);
    }

    [Fact]
    public void The_binary_location_prefers_the_environment_variable()
    {
        var path = NativePeerDriver.ResolveBinaryPath(name => name == NativePeerDriver.BinaryEnvironmentVariable ? "/opt/peer" : null);

        Assert.Equal("/opt/peer", path);
    }

    [Fact]
    public void The_binary_location_defaults_to_the_cmake_build_folder_under_the_repo_root()
    {
        var path = NativePeerDriver.ResolveBinaryPath(_ => null);

        var root = RepoPaths.FindRepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "RakDotNet.slnx")));
        Assert.Equal(Path.Combine(root, "tools", "interop-peer", "build", OperatingSystem.IsWindows() ? "interop-peer.exe" : "interop-peer"), path);
    }
}
