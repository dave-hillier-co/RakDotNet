using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Drives tools/interop-peer (or anything else speaking docs/interop-protocol.md) as a child process.
/// Commands are JSON lines on stdin; replies are matched by <c>seq</c>; packet events go to <see cref="Packets"/>.
/// </summary>
public sealed partial class NativePeerDriver : IPeerDriver
{
    public const string BinaryEnvironmentVariable = "RAKDOTNET_INTEROP_PEER";

    private const int MaxStandardErrorLines = 1000;

    private readonly Process _process;
    private readonly ConcurrentDictionary<long, PendingCommand> _pending = new();
    private readonly Channel<ReceivedPacket> _packets = Channel.CreateUnbounded<ReceivedPacket>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly ConcurrentQueue<string> _standardError = new();
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<JsonObject> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutLoop;
    private readonly Task _stderrLoop;
    private long _nextSeq;
    private volatile bool _exited;
    private int _disposed;

    private NativePeerDriver(Process process)
    {
        _process = process;
        _stderrLoop = Task.Run(ReadStandardErrorAsync);
        _stdoutLoop = Task.Run(ReadStandardOutputAsync);
    }

    public string Kind => "native";

    public ChannelReader<ReceivedPacket> Packets => _packets.Reader;

    public string RakNetVersion { get; private set; } = "";

    public int ProtocolVersion { get; private set; }

    /// <summary>Upper bound on how long any one command waits for its reply.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool HasExited => _exited;

    /// <summary>Everything the peer wrote to stderr so far (the most recent lines when it is long).</summary>
    public string StandardError => string.Join(Environment.NewLine, _standardError);

    /// <summary>Log events and stdout lines that were not valid protocol messages.</summary>
    public IReadOnlyCollection<string> Diagnostics => _diagnostics.ToArray();

    /// <summary>Commands sent and still waiting for a reply.</summary>
    internal int PendingCommandCount => _pending.Count;

    /// <summary>
    /// The native peer binary: <see cref="BinaryEnvironmentVariable"/> when set, otherwise
    /// tools/interop-peer/build/interop-peer under the repo root.
    /// </summary>
    public static string ResolveBinaryPath(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var fromEnvironment = getEnvironmentVariable(BinaryEnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        var name = OperatingSystem.IsWindows() ? "interop-peer.exe" : "interop-peer";
        return Path.Combine(RepoPaths.FindRepoRoot(), "tools", "interop-peer", "build", name);
    }

    /// <summary>The native peer binary, or null when it has not been built.</summary>
    public static string? FindBinary()
    {
        var path = ResolveBinaryPath();
        return File.Exists(path) ? path : null;
    }

    public static Task<NativePeerDriver> LaunchAsync(CancellationToken cancellationToken = default) =>
        LaunchAsync(ResolveBinaryPath(), [], cancellationToken);

    public static async Task<NativePeerDriver> LaunchAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.StandardInput.NewLine = "\n";
        process.StandardInput.AutoFlush = true;
        var driver = new NativePeerDriver(process);
        try
        {
            var ready = await driver._ready.Task.WaitAsync(driver.CommandTimeout, cancellationToken).ConfigureAwait(false);
            driver.RakNetVersion = (string?)ready["raknetVersion"] ?? "";
            driver.ProtocolVersion = (int?)ready["protocolVersion"] ?? 0;
            return driver;
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends any command, including ones without a typed wrapper (e.g. <c>info</c>, <c>stats</c>), and returns the reply.
    /// </summary>
    /// <exception cref="PeerCommandException">The peer replied <c>ok:false</c>.</exception>
    /// <exception cref="PeerProcessExitedException">The peer exited before replying.</exception>
    public async Task<JsonObject> SendCommandAsync(string command, JsonObject fields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var seq = Interlocked.Increment(ref _nextSeq);
        var message = new JsonObject { ["seq"] = seq, ["cmd"] = command };
        foreach (var (key, value) in fields)
        {
            message[key] = value?.DeepClone();
        }

        var pending = new PendingCommand(command);
        _pending[seq] = pending;
        try
        {
            if (_exited)
            {
                FailPending();
            }
            else
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _process.StandardInput.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The process is gone; the stdout loop fails pending commands with the exit code.
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            var reply = await pending.Reply.Task.WaitAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
            if ((bool?)reply["ok"] != true)
            {
                throw new PeerCommandException(command, (string?)reply["error"] ?? "no error message");
            }
            return reply;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Peer did not reply to '{command}' (seq {seq}) within {CommandTimeout}.{Environment.NewLine}stderr:{Environment.NewLine}{StandardError}");
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    public async Task<PeerStarted> StartupAsync(StartupOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var reply = await SendCommandAsync("startup", new JsonObject
        {
            ["port"] = options.Port,
            ["host"] = options.Host,
            ["maxConnections"] = options.MaxConnections,
            ["maxIncoming"] = options.MaxIncoming,
        }, cancellationToken).ConfigureAwait(false);
        var port = (int)reply["port"]!;
        return new PeerStarted(new IPEndPoint(IPAddress.Parse(options.Host), port), ParseGuid(reply["guid"]));
    }

    public async Task<string> ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var reply = await SendCommandAsync("connect", new JsonObject
        {
            ["host"] = options.Remote.Address.ToString(),
            ["port"] = options.Remote.Port,
            ["password"] = Convert.ToHexStringLower(options.Password.Span),
            ["attempts"] = options.Attempts,
            ["attemptIntervalMs"] = options.AttemptIntervalMs,
            ["timeoutMs"] = options.TimeoutMs,
        }, cancellationToken).ConfigureAwait(false);
        return (string?)reply["result"] ?? "";
    }

    public async Task<uint> SendAsync(SendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var reply = await SendCommandAsync("send", new JsonObject
        {
            ["target"] = FormatGuid(options.Target),
            ["data"] = Convert.ToHexStringLower(options.Data.Span),
            ["reliability"] = ToProtocolName(options.Reliability),
            ["priority"] = ToProtocolName(options.Priority),
            ["channel"] = options.Channel,
            ["broadcast"] = options.Broadcast,
        }, cancellationToken).ConfigureAwait(false);
        return (uint)reply["receipt"]!;
    }

    public Task CloseAsync(ulong target, bool notify = true, CancellationToken cancellationToken = default) =>
        SendCommandAsync("close", new JsonObject { ["target"] = FormatGuid(target), ["notify"] = notify, ["channel"] = 0 }, cancellationToken);

    public async Task<bool> PingAsync(IPEndPoint remote, bool onlyIfAccepting = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var reply = await SendCommandAsync("ping", new JsonObject
        {
            ["host"] = remote.Address.ToString(),
            ["port"] = remote.Port,
            ["onlyIfAccepting"] = onlyIfAccepting,
        }, cancellationToken).ConfigureAwait(false);
        return (bool?)reply["sent"] ?? false;
    }

    public Task SetOfflinePingResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        SendCommandAsync("setOfflinePingResponse", new JsonObject { ["data"] = Convert.ToHexStringLower(data.Span) }, cancellationToken);

    public async Task<string> GetConnectionStateAsync(ulong target, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("connectionState", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken).ConfigureAwait(false);
        return (string?)reply["state"] ?? "";
    }

    public async Task<uint> SendListAsync(SendListOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var reply = await SendCommandAsync("sendList", new JsonObject
        {
            ["target"] = FormatGuid(options.Target),
            ["parts"] = new JsonArray([.. options.Parts.Select(p => (JsonNode)Convert.ToHexStringLower(p.Span))]),
            ["reliability"] = ToProtocolName(options.Reliability),
            ["priority"] = ToProtocolName(options.Priority),
            ["channel"] = options.Channel,
            ["broadcast"] = options.Broadcast,
        }, cancellationToken).ConfigureAwait(false);
        return (uint)reply["receipt"]!;
    }

    public Task CancelConnectionAttemptAsync(IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return SendCommandAsync("cancelConnect", new JsonObject { ["host"] = remote.Address.ToString(), ["port"] = remote.Port }, cancellationToken);
    }

    public Task PingConnectedAsync(ulong target, CancellationToken cancellationToken = default) =>
        SendCommandAsync("pingConnected", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken);

    public async Task<bool> AdvertiseSystemAsync(IPEndPoint remote, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var reply = await SendCommandAsync("advertise", new JsonObject
        {
            ["host"] = remote.Address.ToString(),
            ["port"] = remote.Port,
            ["data"] = Convert.ToHexStringLower(data.Span),
        }, cancellationToken).ConfigureAwait(false);
        return (bool?)reply["sent"] ?? false;
    }

    public Task SetTimeoutAsync(TimeSpan timeout, ulong? target = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["ms"] = (long)timeout.TotalMilliseconds };
        if (target is { } guid)
        {
            fields["target"] = FormatGuid(guid);
        }
        return SendCommandAsync("setTimeout", fields, cancellationToken);
    }

    public Task SetMaximumIncomingConnectionsAsync(int count, CancellationToken cancellationToken = default) =>
        SendCommandAsync("setMaxIncoming", new JsonObject { ["count"] = count }, cancellationToken);

    public Task SetOccasionalPingAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync("setOccasionalPing", new JsonObject { ["enabled"] = enabled }, cancellationToken);

    public Task SetSplitMessageProgressIntervalAsync(int interval, CancellationToken cancellationToken = default) =>
        SendCommandAsync("setSplitMessageProgressInterval", new JsonObject { ["interval"] = interval }, cancellationToken);

    public Task SetOutgoingBandwidthLimitAsync(uint bitsPerSecond, CancellationToken cancellationToken = default) =>
        SendCommandAsync("setOutgoingBandwidthLimit", new JsonObject { ["bitsPerSecond"] = bitsPerSecond }, cancellationToken);

    public Task BanAsync(string ip, TimeSpan duration, CancellationToken cancellationToken = default) =>
        SendCommandAsync("ban", new JsonObject { ["ip"] = ip, ["ms"] = (long)duration.TotalMilliseconds }, cancellationToken);

    public async Task<PeerStatistics> GetStatisticsAsync(ulong target, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("stats", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken).ConfigureAwait(false);
        var stats = reply["stats"]!.AsObject();
        return new PeerStatistics(
            (int)stats["messagesInResendBuffer"]!,
            (ulong)stats["bytesInResendBuffer"]!,
            (double)stats["packetlossLastSecond"]!,
            (double)stats["packetlossTotal"]!,
            (bool)stats["isLimitedByCongestionControl"]!,
            (ulong)stats["bpsLimitByCongestionControl"]!,
            (bool)stats["isLimitedByOutgoingBandwidthLimit"]!,
            (ulong)stats["bpsLimitByOutgoingBandwidthLimit"]!,
            ParseMetrics(stats["runningTotal"]!.AsObject()),
            ParseMetrics(stats["valueOverLastSecond"]!.AsObject()));
    }

    public async Task<PeerInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("info", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var connections = reply["connections"]!.AsArray()
            .Select(c => new ConnectionInfo(
                ParseGuid(c!["guid"]),
                IPEndPoint.Parse((string)c["address"]!),
                (int)c["mtu"]!,
                (int)c["averagePing"]!,
                (int)c["lastPing"]!,
                (int)c["lowestPing"]!,
                IPEndPoint.Parse((string)c["externalAddress"]!)))
            .ToList();
        return new PeerInfo(ParseGuid(reply["guid"]), IPEndPoint.Parse((string)reply["boundAddress"]!), connections);
    }

    public async Task<ulong> GetTimeAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("time", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return ulong.Parse((string)reply["time"]!, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>The C++ name of each RNSPerSecondMetrics value (RakNetStatistics.h), as interop-peer reports it.</summary>
    public static string ToProtocolName(PerSecondMetric metric) => metric switch
    {
        PerSecondMetric.UserMessageBytesPushed => "USER_MESSAGE_BYTES_PUSHED",
        PerSecondMetric.UserMessageBytesSent => "USER_MESSAGE_BYTES_SENT",
        PerSecondMetric.UserMessageBytesResent => "USER_MESSAGE_BYTES_RESENT",
        PerSecondMetric.UserMessageBytesReceivedProcessed => "USER_MESSAGE_BYTES_RECEIVED_PROCESSED",
        PerSecondMetric.UserMessageBytesReceivedIgnored => "USER_MESSAGE_BYTES_RECEIVED_IGNORED",
        PerSecondMetric.ActualBytesSent => "ACTUAL_BYTES_SENT",
        PerSecondMetric.ActualBytesReceived => "ACTUAL_BYTES_RECEIVED",
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    private static Dictionary<PerSecondMetric, ulong> ParseMetrics(JsonObject metrics) =>
        Enum.GetValues<PerSecondMetric>().ToDictionary(m => m, m => (ulong)metrics[ToProtocolName(m)]!);

    public Task ShutdownAsync(int blockMs = 100, CancellationToken cancellationToken = default) =>
        SendCommandAsync("shutdown", new JsonObject { ["blockMs"] = blockMs }, cancellationToken);

    public static string ToProtocolName(Reliability reliability) => reliability switch
    {
        Reliability.Unreliable => "UNRELIABLE",
        Reliability.UnreliableSequenced => "UNRELIABLE_SEQUENCED",
        Reliability.Reliable => "RELIABLE",
        Reliability.ReliableOrdered => "RELIABLE_ORDERED",
        Reliability.ReliableSequenced => "RELIABLE_SEQUENCED",
        Reliability.UnreliableWithAckReceipt => "UNRELIABLE_WITH_ACK_RECEIPT",
        Reliability.ReliableWithAckReceipt => "RELIABLE_WITH_ACK_RECEIPT",
        Reliability.ReliableOrderedWithAckReceipt => "RELIABLE_ORDERED_WITH_ACK_RECEIPT",
        _ => throw new ArgumentOutOfRangeException(nameof(reliability), reliability, null),
    };

    public static string ToProtocolName(Priority priority) => priority switch
    {
        Priority.Immediate => "IMMEDIATE_PRIORITY",
        Priority.High => "HIGH_PRIORITY",
        Priority.Medium => "MEDIUM_PRIORITY",
        Priority.Low => "LOW_PRIORITY",
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null),
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (!_exited)
        {
            try
            {
                // stdin EOF asks the peer to shut down; kill it if it does not.
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        try
        {
            // The loops catch their own failures; this only waits for them to drain.
            await Task.WhenAll(_stdoutLoop, _stderrLoop).ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
            _writeLock.Dispose();
        }
    }

    private async Task ReadStandardOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                HandleLine(line);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            // stderr may still be draining; give it a moment so failures carry the peer's last words.
            await _stderrLoop.WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            _exited = true;
            FailPending();
            _packets.Writer.TryComplete();
        }
    }

    private void FailPending()
    {
        int? exitCode = _process.HasExited ? _process.ExitCode : null;
        _ready.TrySetException(new PeerProcessExitedException(exitCode, "before the ready event", StandardError));
        foreach (var (seq, pending) in _pending)
        {
            pending.Reply.TrySetException(new PeerProcessExitedException(exitCode, $"before replying to '{pending.Command}' (seq {seq})", StandardError));
        }
    }

    private void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            HandleMessage(line);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            // A line that does not follow docs/interop-protocol.md must not stop the reader: later replies still arrive.
            _diagnostics.Enqueue($"unparsed stdout ({exception.GetType().Name}: {exception.Message}): {line}");
        }
    }

    private void HandleMessage(string line)
    {
        var message = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");

        if (message["seq"] is { } seqNode)
        {
            if (seqNode is JsonValue seqValue && seqValue.TryGetValue<long>(out var seq) && _pending.TryGetValue(seq, out var pending))
            {
                pending.Reply.TrySetResult(message);
            }
            else
            {
                _diagnostics.Enqueue($"reply with no pending command: {line}");
            }
            return;
        }

        switch ((string?)message["event"])
        {
            case "ready":
                _ready.TrySetResult(message);
                break;
            case "packet":
                _packets.Writer.TryWrite(ParsePacket(message));
                break;
            case "log":
                _diagnostics.Enqueue($"log: {(string?)message["message"]}");
                break;
            default:
                _diagnostics.Enqueue($"unknown stdout message: {line}");
                break;
        }
    }

    private async Task ReadStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _standardError.Enqueue(line);
                while (_standardError.Count > MaxStandardErrorLines)
                {
                    _standardError.TryDequeue(out _);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static ReceivedPacket ParsePacket(JsonObject message)
    {
        var address = IPEndPoint.TryParse((string?)message["address"] ?? "", out var parsed) ? parsed : new IPEndPoint(IPAddress.None, 0);
        var timestamp = message["timestamp"] is { } timestampNode
            ? ulong.Parse((string)timestampNode!, NumberStyles.None, CultureInfo.InvariantCulture)
            : (ulong?)null;
        return new ReceivedPacket(
            (byte)((int?)message["messageId"] ?? throw new FormatException("packet event has no messageId")),
            (string?)message["messageName"] ?? "",
            ParseGuid(message["guid"]),
            address,
            Convert.FromHexString((string?)message["data"] ?? ""),
            (long?)message["timeMs"] ?? 0,
            timestamp);
    }

    // GUIDs are decimal strings because JSON numbers lose precision above 2^53 (docs/interop-protocol.md).
    // Anything unparsable maps to UNASSIGNED_RAKNET_GUID, which is (uint64_t)-1 (RakNetTypes.h:364).
    private static ulong ParseGuid(JsonNode? node) =>
        ulong.TryParse((string?)node, NumberStyles.None, CultureInfo.InvariantCulture, out var guid) ? guid : ulong.MaxValue;

    private static string FormatGuid(ulong guid) => guid.ToString(CultureInfo.InvariantCulture);

    private sealed class PendingCommand(string command)
    {
        public string Command { get; } = command;

        public TaskCompletionSource<JsonObject> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
