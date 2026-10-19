// A client that connects, then sends a timestamped message.
using RakDotNet;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 1 });

var result = await client.ConnectAsync("localhost", 19132, cancellationToken: cts.Token);
var server = result.EnsureConnected(); // throws RakConnectException carrying the result

Span<byte> buffer = stackalloc byte[32];
var writer = new BitStreamWriter(buffer);
new TimestampHeader(client.GetTime()).Write(ref writer);
writer.WriteByte((byte)MessageId.UserPacketEnum);
writer.WriteSingle(1.5f);
writer.WriteSingle(-2.0f);
var sent = server.Send(writer, Reliability.UnreliableSequenced, Priority.High, orderingChannel: 1);
Console.WriteLine($"Send: {sent.Status}");

await server.CloseAsync(cancellationToken: cts.Token);
