using System.Net;
using System.Text;
using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// A TCPInterface peer playing a server for the external services: it reads what the client sends on a connection
/// and answers with scripted text. Text is compared as Latin-1, byte for byte.
/// </summary>
internal static class ServiceScript
{
    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    public static async Task<IPEndPoint> AcceptAsync(TcpPeer server, CancellationToken cancellationToken) =>
        (await server.WaitForKindAsync("newIncomingConnection", Timeout, cancellationToken).ConfigureAwait(false)).Address!;

    /// <summary>Receives exactly as many bytes as <paramref name="expected"/> holds and asserts they are it.</summary>
    public static async Task ExpectAsync(TcpPeer server, IPEndPoint from, string expected, CancellationToken cancellationToken)
    {
        var received = await server.ReceiveBytesAsync(from, r => r.Length >= expected.Length, $"'{Escape(expected)}'", Timeout, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(Escape(expected), Escape(Encoding.Latin1.GetString(received)));
    }

    /// <summary>Receives until what arrived ends with <paramref name="end"/>.</summary>
    public static async Task<string> ReceiveUntilAsync(TcpPeer server, IPEndPoint from, string end, CancellationToken cancellationToken)
    {
        var received = await server.ReceiveBytesAsync(from, r => Encoding.Latin1.GetString(r).EndsWith(end, StringComparison.Ordinal),
            $"text ending '{Escape(end)}'", Timeout, cancellationToken).ConfigureAwait(false);
        return Encoding.Latin1.GetString(received);
    }

    public static Task SendAsync(TcpPeer server, IPEndPoint to, string text, CancellationToken cancellationToken) =>
        server.Tcp.TcpSendAsync(to, Encoding.Latin1.GetBytes(text), cancellationToken: cancellationToken);

    public static async Task ExchangeAsync(TcpPeer server, IPEndPoint with, string expected, string reply, CancellationToken cancellationToken)
    {
        await ExpectAsync(server, with, expected, cancellationToken).ConfigureAwait(false);
        await SendAsync(server, with, reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The end of an email: EmailSender queues QUIT, waits 30 ms and stops its interface (EmailSender.cpp:295-306), which
    /// drops whatever is still unsent (TCPInterface.cpp:248-253). Native RakNet's send thread often has not sent QUIT by
    /// then, so the server sees QUIT then the close, or only the close.
    /// </summary>
    public static async Task QuitOrCloseAsync(TcpPeer server, IPEndPoint with, CancellationToken cancellationToken)
    {
        var received = new List<byte>();
        var end = await server.WaitForAsync(e => Equals(e.Address, with) && e.Kind is ("packet" or "lostConnection") && Collect(e), "QUIT or the close",
            Timeout, cancellationToken).ConfigureAwait(false);
        while (end.Kind == "packet")
            end = await server.WaitForAsync(e => Equals(e.Address, with) && e.Kind is ("packet" or "lostConnection") && Collect(e), "the close",
                Timeout, cancellationToken).ConfigureAwait(false);
        var text = Encoding.Latin1.GetString([.. received]);
        Assert.True(text is "" or "QUIT\r\n", $"Expected QUIT or nothing before the close, got '{Escape(text)}'.");

        bool Collect(TcpEvent e)
        {
            if (e.Kind == "packet")
                received.AddRange(e.Data);
            return true;
        }
    }

    // Line ends made visible, so a failed comparison shows them.
    private static string Escape(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\0", "\\0", StringComparison.Ordinal);
}
