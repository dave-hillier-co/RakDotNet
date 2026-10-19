// A client that connects, then sends a timestamped message.
using RakDotNet.Samples.Client;

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await GameClient.RunAsync("localhost", 19132, Console.Out, cts.Token);
