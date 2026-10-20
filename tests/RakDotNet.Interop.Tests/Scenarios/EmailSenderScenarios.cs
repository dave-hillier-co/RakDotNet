using System.Text;
using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// EmailSender on the client peer, as Samples/SendEmail uses it, against the server peer's TCPInterface playing an SMTP
/// server that checks every byte. The boundary of a multipart message is random in both implementations, so it is read
/// from the message and checked for its form.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class EmailSenderScenarios
{
    // Samples/SendEmail/main.cpp:24, 68.
    private const string Quote =
        "Man is distinguished, not only by his reason, but by this singular passion from other animals, which is a lust of the mind, " +
        "that by a perseverance of delight in the continued and indefatigable generation of knowledge, exceeds the short vehemence of any carnal pleasure.";

    private const string SampleBody =
        "Test attachment body :).\n.\n..\n.\n(Should be .,.,..,.)\r\n.\r\n.\r\n..\r\n.\r\n(Should be .,.,..,.)12345\r\n.\r\n";

    // EmailSender.cpp:178-248: the sample body dot-stuffed, with the CRLF appended.
    private const string SampleBodySent =
        "Test attachment body :).\n..\r\n...\r\n..\r\n(Should be .,.,..,.)\r\n..\r\n..\r\n...\r\n..\r\n(Should be .,.,..,.)12345\r\n..\r\n\r\n";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task The_sample_email_with_its_attachment_is_sent_byte_for_byte(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "SMTP server", Token);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Token);
        await serverPeer.ListenAsync(cancellationToken: Token);

        var send = ((IServicesDriver)clientDriver).SendEmailAsync(new EmailRequest(
            "127.0.0.1", serverPeer.Port, "sender@example.com", "rakkar@rakkar.org", "sender@example.com", "rakkar@rakkar.org",
            "Test subject.", SampleBody, [new EmailAttachmentSpec("quote.txt", Encoding.ASCII.GetBytes(Quote))], "unused"), Token);

        var connection = await ServiceScript.AcceptAsync(serverPeer, Token);
        await ServiceScript.SendAsync(serverPeer, connection, "220 smtp.example.com ESMTP\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, connection, "EHLO\r\n", "250 smtp.example.com\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, connection, "MAIL From: <sender@example.com>\r\n", "250 OK\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, connection, "RCPT TO: <rakkar@rakkar.org>\r\n", "250 OK\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, connection, "DATA\r\n", "354 Go ahead\r\n", Token);
        var data = await ServiceScript.ReceiveUntilAsync(serverPeer, connection, "--\r\n\r\n.\r\n", Token);
        await ServiceScript.SendAsync(serverPeer, connection, "250 Queued\r\n", Token);
        await ServiceScript.QuitOrCloseAsync(serverPeer, connection, Token);

        Assert.Null(await send);
        var start = data.IndexOf("BOUNDARY=\"", StringComparison.Ordinal) + 10;
        var boundary = data[start..data.IndexOf('"', start)];
        Assert.Equal(60, boundary.Length);
        Assert.All(boundary, c => Assert.Contains(c, Plugins.Base64Encoder.Map));
        // EmailSender.cpp:146-293.
        Assert.Equal(
            "Subject: Test subject.\r\nFrom: sender@example.com\r\nTo: rakkar@rakkar.org\r\nMIME-version: 1.0\r\n" +
            $"Content-type: multipart/mixed; BOUNDARY=\"{boundary}\"\r\n\r\n" +
            $"This is a multi-part message in MIME format.\r\n\r\n--{boundary}\r\n" +
            "Content-Type: text/plain; charset=\"US-ASCII\"\r\n\r\n" + SampleBodySent +
            $"\r\n--{boundary}\r\n" +
            "Content-Type: APPLICATION/Octet-Stream; SizeOnDisk=269; name=\"quote.txt\"\r\nContent-Transfer-Encoding: BASE64\r\n" +
            "Content-Description: quote.txt\r\n\r\n" +
            Plugins.Base64Encoder.Encode(Encoding.ASCII.GetBytes(Quote)) +
            $"\r\n--{boundary}--\r\n\r\n.\r\n",
            data);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_plain_email_without_names_and_a_rejected_one(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "SMTP server", Token);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        var services = (IServicesDriver)clientDriver;

        // EmailSender.cpp:137-160: null addresses are <>, and null headers are left out.
        var plain = services.SendEmailAsync(new EmailRequest("127.0.0.1", serverPeer.Port, null, null, null, null, null, "line\n.\nend"), Token);
        var first = await ServiceScript.AcceptAsync(serverPeer, Token);
        // A reply holding no known code is skipped (EmailSender.cpp:310-363).
        await ServiceScript.SendAsync(serverPeer, first, "220 ready\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, first, "EHLO\r\n", "100 thinking\r\n", Token);
        await Task.Delay(300, Token);
        await ServiceScript.SendAsync(serverPeer, first, "250 hello\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, first, "MAIL From: <>\r\n", "250 OK\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, first, "RCPT TO: <>\r\n", "250 OK\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, first, "DATA\r\n", "354 Go ahead\r\n", Token);
        await ServiceScript.ExpectAsync(serverPeer, first,
            "MIME-version: 1.0\r\nContent-Type: text/plain; charset=\"US-ASCII\"\r\n\r\nline\n..\r\nend\r\n\r\n.\r\n", Token);
        await ServiceScript.SendAsync(serverPeer, first, "250 Queued\r\n", Token);
        await ServiceScript.QuitOrCloseAsync(serverPeer, first, Token);
        Assert.Null(await plain);

        var rejected = services.SendEmailAsync(new EmailRequest("127.0.0.1", serverPeer.Port, "a@b", "nobody@b", null, null, "S", "x"), Token);
        var second = await ServiceScript.AcceptAsync(serverPeer, Token);
        await ServiceScript.SendAsync(serverPeer, second, "220 ready\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, second, "EHLO\r\n", "250 hello\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, second, "MAIL From: <a@b>\r\n", "250 OK\r\n", Token);
        await ServiceScript.ExchangeAsync(serverPeer, second, "RCPT TO: <nobody@b>\r\n", "550 No such user\r\n", Token);
        Assert.Equal("Failed on error code 550", await rejected);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_server_that_closes_during_the_conversation_loses_the_connection(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "SMTP server", Token);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Token);
        await serverPeer.ListenAsync(cancellationToken: Token);

        var send = ((IServicesDriver)clientDriver).SendEmailAsync(new EmailRequest("127.0.0.1", serverPeer.Port, "a@b", "c@d", null, null, null, "x"), Token);
        var connection = await ServiceScript.AcceptAsync(serverPeer, Token);
        await ServiceScript.SendAsync(serverPeer, connection, "220 ready\r\n", Token);
        await ServiceScript.ExpectAsync(serverPeer, connection, "EHLO\r\n", Token);
        await serverPeer.Tcp.TcpCloseAsync(connection, Token);

        Assert.Equal("Connection to server lost.", await send);
    }
}
