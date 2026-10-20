// A client and a server in one process: connect, send, echo and receive over loopback UDP. Exits 0 on success.
using RakDotNet.Samples.Loopback;

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
return await LoopbackEcho.RunAsync("hello", Console.Out, cts.Token) ? 0 : 1;
