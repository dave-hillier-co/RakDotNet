// A server on the background loop, reading borrowed events without allocating. Stop it with Ctrl+C.
using RakDotNet.Samples.Server;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

await using var server = GameServer.Start(port: 19132);
try
{
    await GameServer.RunAsync(server, Console.Out, cts.Token);
}
catch (OperationCanceledException)
{
}
