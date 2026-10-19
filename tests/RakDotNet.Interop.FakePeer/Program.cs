// A stand-in for tools/interop-peer that speaks the JSON Lines protocol in docs/interop-protocol.md
// without RakNet, so NativePeerDriver can be tested on machines without the native binary.
// It does no networking; query commands (info, stats, time) reply with fixed values.
// Extra commands exist only to exercise the driver:
//   history          reply carries "commands": every command received so far
//   delayed  ms      reply after ms milliseconds, without blocking later commands
//   emit     line    writes the given object as a raw line on stdout (events, malformed lines)
//   crash    code    writes to stderr and exits with code, without replying
// Command line: --fail-on-start writes to stderr and exits 3 before the ready event.

using System.Text;
using System.Text.Json.Nodes;

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var writeLock = new Lock();

void WriteLine(JsonNode node)
{
    lock (writeLock)
    {
        stdout.WriteLine(node.ToJsonString());
    }
}

if (args.Contains("--fail-on-start"))
{
    stderr.WriteLine("fake peer refused to start");
    return 3;
}

const string guid = "18446744073709551000";
var history = new JsonArray();
var offlinePingResponse = "";
uint nextReceipt = 1;
var pendingDelays = new List<Task>();

WriteLine(new JsonObject { ["event"] = "ready", ["raknetVersion"] = "4.081", ["protocolVersion"] = 6 });
stderr.WriteLine("fake peer ready");

var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
while (await stdin.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    var command = JsonNode.Parse(line)!.AsObject();
    history.Add(command.DeepClone());
    var seq = command["seq"]!.GetValue<long>();
    var name = command["cmd"]?.GetValue<string>() ?? "";

    JsonObject Ok(params (string Key, JsonNode? Value)[] fields)
    {
        var reply = new JsonObject { ["seq"] = seq, ["ok"] = true };
        foreach (var (key, value) in fields)
        {
            reply[key] = value;
        }
        return reply;
    }

    switch (name)
    {
        case "startup":
        {
            var port = command["port"]?.GetValue<int>() ?? 0;
            WriteLine(Ok(("port", port == 0 ? 40000 : port), ("guid", guid)));
            break;
        }
        case "connect":
            WriteLine(Ok(("result", "CONNECTION_ATTEMPT_STARTED")));
            WriteLine(new JsonObject
            {
                ["event"] = "packet",
                ["messageId"] = 16,
                ["messageName"] = "ID_CONNECTION_REQUEST_ACCEPTED",
                ["guid"] = "42",
                ["address"] = $"{command["host"]}:{command["port"]}",
                ["data"] = "10",
                ["timeMs"] = 1000,
            });
            break;
        case "send":
            WriteLine(Ok(("receipt", nextReceipt++)));
            break;
        case "setOfflinePingResponse":
            offlinePingResponse = command["data"]?.GetValue<string>() ?? "";
            WriteLine(Ok());
            break;
        case "ping":
            WriteLine(Ok(("sent", true)));
            WriteLine(new JsonObject
            {
                ["event"] = "packet",
                ["messageId"] = 28,
                ["messageName"] = "ID_UNCONNECTED_PONG",
                ["guid"] = guid,
                ["address"] = $"{command["host"]}:{command["port"]}",
                ["data"] = "1c" + offlinePingResponse,
                ["timeMs"] = 2000,
            });
            break;
        case "sendList":
            WriteLine(Ok(("receipt", nextReceipt++)));
            break;
        case "close" or "cancelConnect" or "pingConnected" or "setTimeout" or "setMaxIncoming" or "setOccasionalPing"
            or "setSplitMessageProgressInterval" or "setOutgoingBandwidthLimit" or "ban":
            WriteLine(Ok());
            break;
        case "advertise":
            WriteLine(Ok(("sent", true)));
            break;
        case "time":
            WriteLine(Ok(("time", guid)));
            break;
        case "info":
            WriteLine(Ok(
                ("guid", guid),
                ("boundAddress", "127.0.0.1:40000"),
                ("connections", new JsonArray(new JsonObject
                {
                    ["guid"] = "42",
                    ["address"] = "127.0.0.1:1234",
                    ["mtu"] = 1492,
                    ["averagePing"] = 3,
                    ["lastPing"] = 2,
                    ["lowestPing"] = 1,
                    ["externalAddress"] = "127.0.0.1:40000",
                }))));
            break;
        case "stats":
        {
            string[] metrics =
            [
                "USER_MESSAGE_BYTES_PUSHED", "USER_MESSAGE_BYTES_SENT", "USER_MESSAGE_BYTES_RESENT",
                "USER_MESSAGE_BYTES_RECEIVED_PROCESSED", "USER_MESSAGE_BYTES_RECEIVED_IGNORED", "ACTUAL_BYTES_SENT", "ACTUAL_BYTES_RECEIVED",
            ];
            var runningTotal = new JsonObject();
            var valueOverLastSecond = new JsonObject();
            for (var i = 0; i < metrics.Length; i++)
            {
                runningTotal[metrics[i]] = 7 + i;
                valueOverLastSecond[metrics[i]] = 8 + i;
            }
            WriteLine(Ok(("stats", new JsonObject
            {
                ["messagesInResendBuffer"] = 2,
                ["bytesInResendBuffer"] = 300,
                ["packetlossLastSecond"] = 0.25,
                ["packetlossTotal"] = 0.5,
                ["isLimitedByCongestionControl"] = true,
                ["bpsLimitByCongestionControl"] = 1000,
                ["isLimitedByOutgoingBandwidthLimit"] = true,
                ["bpsLimitByOutgoingBandwidthLimit"] = 100000,
                ["runningTotal"] = runningTotal,
                ["valueOverLastSecond"] = valueOverLastSecond,
            })));
            break;
        }
        case "connectionState":
            WriteLine(Ok(("state", "IS_CONNECTED")));
            break;
        case "history":
            WriteLine(Ok(("commands", history.DeepClone())));
            break;
        case "delayed":
        {
            var ms = command["ms"]?.GetValue<int>() ?? 0;
            var reply = Ok(("delayedMs", ms));
            pendingDelays.Add(Task.Run(async () =>
            {
                await Task.Delay(ms);
                WriteLine(reply);
            }));
            break;
        }
        case "emit":
            lock (writeLock)
            {
                stdout.WriteLine(command["line"]!.GetValue<string>());
            }
            WriteLine(Ok());
            break;
        case "crash":
        {
            var code = command["code"]?.GetValue<int>() ?? 1;
            stderr.WriteLine($"fake peer crashing with code {code}");
            return code;
        }
        case "shutdown":
            WriteLine(Ok());
            await Task.WhenAll(pendingDelays);
            return 0;
        default:
            WriteLine(new JsonObject { ["seq"] = seq, ["ok"] = false, ["error"] = $"unknown command '{name}'" });
            break;
    }
}

await Task.WhenAll(pendingDelays);
return 0;
