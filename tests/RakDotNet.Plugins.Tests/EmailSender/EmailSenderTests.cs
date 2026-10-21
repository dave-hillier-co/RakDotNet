using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace RakDotNet.Plugins.Tests.EmailSender;

/// <summary>
/// EmailSender::Send against an in-process SMTP server that answers each command and records every byte, as
/// Samples/SendEmail uses it. Expected bytes are derived from EmailSender.cpp.
/// </summary>
public sealed class EmailSenderTests : IDisposable
{
    // Samples/SendEmail/main.cpp:68.
    private const string SampleBody =
        "Test attachment body :).\n.\n..\n.\n(Should be .,.,..,.)\r\n.\r\n.\r\n..\r\n.\r\n(Should be .,.,..,.)12345\r\n.\r\n";

    // The sample body after the dot-stuffing loop of EmailSender.cpp:178-246, and the CRLF it appends.
    private const string SampleBodySent =
        "Test attachment body :).\n..\r\n...\r\n..\r\n(Should be .,.,..,.)\r\n..\r\n..\r\n...\r\n..\r\n(Should be .,.,..,.)12345\r\n..\r\n\r\n";

    private readonly ScriptedServer _server = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Sends_the_envelope_headers_and_dot_stuffed_body_then_quits()
    {
        _server.Expect(async c =>
        {
            await c.SendAsync("220 fake ESMTP\r\n");
            await Exchange(c, "EHLO\r\n", "250 fake\r\n");
            await Exchange(c, "MAIL From: <sender@example.com>\r\n", "250 OK\r\n");
            await Exchange(c, "RCPT TO: <rcpt@example.com>\r\n", "250 OK\r\n");
            await Exchange(c, "DATA\r\n", "354 Go ahead\r\n");
            Assert.Equal(
                "Subject: Test subject.\r\nFrom: Sender Name\r\nTo: Recipient Name\r\nMIME-version: 1.0\r\n" +
                "Content-Type: text/plain; charset=\"US-ASCII\"\r\n\r\n" + SampleBodySent + "\r\n.\r\n",
                await c.ReadUntilAsync("\r\n.\r\n"));
            await c.SendAsync("250 Queued\r\n");
            Assert.Equal("QUIT\r\n", await c.ReadUntilAsync("\r\n"));
        });

        var result = await new Plugins.EmailSender().SendAsync("127.0.0.1", _server.Port, "sender@example.com", "rcpt@example.com",
            "Sender Name", "Recipient Name", "Test subject.", SampleBody, null, null, null, Token);

        Assert.Null(result);
        await _server.CompletionAsync();
    }

    [Fact]
    public async Task Missing_addresses_and_headers_are_written_as_RakNet_writes_them()
    {
        // EmailSender.cpp:137-160: a null sender or recipient is <>, and a null subject or name writes no header.
        _server.Expect(async c =>
        {
            await c.SendAsync("220 fake\r\n");
            await Exchange(c, "EHLO\r\n", "250 fake\r\n");
            await Exchange(c, "MAIL From: <>\r\n", "250 OK\r\n");
            await Exchange(c, "RCPT TO: <>\r\n", "250 OK\r\n");
            await Exchange(c, "DATA\r\n", "354 Go ahead\r\n");
            Assert.Equal("MIME-version: 1.0\r\nContent-Type: text/plain; charset=\"US-ASCII\"\r\n\r\nhi\r\n\r\n.\r\n", await c.ReadUntilAsync("\r\n.\r\n"));
            await c.SendAsync("250 Queued\r\n");
        });

        Assert.Null(await new Plugins.EmailSender().SendAsync("127.0.0.1", _server.Port, null, null, null, null, null, "hi", null, null, null, Token));
        await _server.CompletionAsync();
    }

    [Fact]
    public async Task An_empty_body_sends_its_terminator()
    {
        // EmailSender.cpp:182-183 copies body[0] whatever the length, so an empty body is a zero byte and the CRLF.
        _server.Expect(async c =>
        {
            await c.SendAsync("220 fake\r\n");
            await Exchange(c, "EHLO\r\n", "250 fake\r\n");
            await Exchange(c, "MAIL From: <a@b>\r\n", "250 OK\r\n");
            await Exchange(c, "RCPT TO: <c@d>\r\n", "250 OK\r\n");
            await Exchange(c, "DATA\r\n", "354 Go ahead\r\n");
            Assert.EndsWith("\r\n\r\n\0\r\n\r\n.\r\n", await c.ReadUntilAsync("\r\n.\r\n"), StringComparison.Ordinal);
            await c.SendAsync("250 Queued\r\n");
        });

        Assert.Null(await new Plugins.EmailSender().SendAsync("127.0.0.1", _server.Port, "a@b", "c@d", null, null, null, "", null, null, null, Token));
        await _server.CompletionAsync();
    }

    [Fact]
    public async Task Attachments_are_sent_as_base64_parts_between_random_boundaries()
    {
        var quote = Encoding.ASCII.GetBytes(new string('q', 100));
        string? data = null;
        _server.Expect(async c =>
        {
            await c.SendAsync("220 fake\r\n");
            await Exchange(c, "EHLO\r\n", "250 fake\r\n");
            await Exchange(c, "MAIL From: <a@b>\r\n", "250 OK\r\n");
            await Exchange(c, "RCPT TO: <c@d>\r\n", "250 OK\r\n");
            await Exchange(c, "DATA\r\n", "354 Go ahead\r\n");
            data = await c.ReadUntilAsync("--\r\n\r\n.\r\n");
            await c.SendAsync("250 Queued\r\n");
        });

        var attachments = new[] { new EmailAttachment("quote.txt", quote), new EmailAttachment("two.bin", new byte[] { 1, 2 }) };
        Assert.Null(await new Plugins.EmailSender().SendAsync("127.0.0.1", _server.Port, "a@b", "c@d", null, null, "S", "body", attachments, null, null, Token));
        await _server.CompletionAsync();

        // EmailSender.cpp:163-176: 60 characters of the base64 alphabet.
        var start = data!.IndexOf("BOUNDARY=\"", StringComparison.Ordinal) + 10;
        var boundary = data[start..data.IndexOf('"', start)];
        Assert.Equal(60, boundary.Length);
        Assert.All(boundary, ch => Assert.Contains(ch, Plugins.Base64Encoder.Map));
        // EmailSender.cpp:163-269.
        Assert.Equal(
            "Subject: S\r\nMIME-version: 1.0\r\n" +
            $"Content-type: multipart/mixed; BOUNDARY=\"{boundary}\"\r\n\r\n" +
            $"This is a multi-part message in MIME format.\r\n\r\n--{boundary}\r\n" +
            "Content-Type: text/plain; charset=\"US-ASCII\"\r\n\r\nbody\r\n" +
            $"\r\n--{boundary}\r\n" +
            "Content-Type: APPLICATION/Octet-Stream; SizeOnDisk=100; name=\"quote.txt\"\r\nContent-Transfer-Encoding: BASE64\r\nContent-Description: quote.txt\r\n\r\n" +
            Plugins.Base64Encoder.Encode(quote) +
            $"\r\n--{boundary}\r\n" +
            "Content-Type: APPLICATION/Octet-Stream; SizeOnDisk=2; name=\"two.bin\"\r\nContent-Transfer-Encoding: BASE64\r\nContent-Description: two.bin\r\n\r\n" +
            "AQI=\r\n" +
            $"\r\n--{boundary}--\r\n" +
            "\r\n.\r\n",
            data);
    }

    [Fact]
    public async Task Each_reply_is_matched_by_the_first_code_it_contains()
    {
        // GetResponse (EmailSender.cpp:310-363) skips replies with no code it knows, and tests 354 before 250, so any
        // reply holding 354 lets DATA go ahead.
        _server.Expect(async c =>
        {
            await c.SendAsync("220 fake\r\n");
            await Exchange(c, "EHLO\r\n", "100 nothing known\r\n");
            await c.SendAsync("250 fake\r\n");
            await Exchange(c, "MAIL From: <a@b>\r\n", "250 OK\r\n");
            await Exchange(c, "RCPT TO: <c@d>\r\n", "250 OK\r\n");
            await Exchange(c, "DATA\r\n", "250 also 354\r\n");
            await c.ReadUntilAsync("\r\n.\r\n");
            await c.SendAsync("250 Queued\r\n");
        });

        Assert.Null(await new Plugins.EmailSender().SendAsync("127.0.0.1", _server.Port, "a@b", "c@d", null, null, null, "x", null, null, null, Token));
        await _server.CompletionAsync();
    }

    [Theory]
    [InlineData("550 No such user\r\n", "Failed on error code 550")]
    [InlineData("553 Not allowed\r\n", "Failed on error code 553")]
    public async Task A_rejected_recipient_ends_the_send_with_its_error(string reply, string expected)
    {
        _server.Expect(async c =>
        {
            await c.SendAsync("220 fake\r\n");
            await Exchange(c, "EHLO\r\n", "250 fake\r\n");
            await Exchange(c, "MAIL From: <a@b>\r\n", "250 OK\r\n");
            await Exchange(c, "RCPT TO: <c@d>\r\n", reply);
        });

        Assert.Equal(expected, await new Plugins.EmailSender().SendAsync("127.0.0.1", _server.Port, "a@b", "c@d", null, null, null, "x", null, null, null, Token));
    }

    [Fact]
    public async Task A_server_that_closes_loses_the_connection()
    {
        _server.Expect(async c =>
        {
            await c.SendAsync("220 fake\r\n");
            await c.ReadUntilAsync("EHLO\r\n");
            c.Close();
        });

        Assert.Equal("Connection to server lost.",
            await new Plugins.EmailSender().SendAsync("127.0.0.1", _server.Port, "a@b", "c@d", null, null, null, "x", null, null, null, Token));
    }

    [Fact]
    public async Task No_server_fails_to_connect()
    {
        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = (ushort)((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();

        Assert.Equal("Failed to connect to host",
            await new Plugins.EmailSender().SendAsync("127.0.0.1", port, "a@b", "c@d", null, null, null, "x", null, null, null, Token));
    }

    [Fact]
    public async Task No_greeting_within_three_seconds_times_out()
    {
        // EmailSender.cpp:50-68: Receive every 250 ms for 3 seconds.
        var time = new FakeTimeProvider();
        var send = new Plugins.EmailSender(time).SendAsync("127.0.0.1", _server.Port, "a@b", "c@d", null, null, null, "x", null, null, null, Token);

        await AdvanceUntilDoneAsync(time, send, TimeSpan.FromMilliseconds(250));

        Assert.Equal("Timeout while waiting for initial data from server.", await send);
    }

    [Fact]
    public async Task No_reply_within_five_seconds_times_out()
    {
        // EmailSender.cpp:316-361: Receive every 100 ms for 5 seconds.
        var time = new FakeTimeProvider();
        _server.Expect(c => c.SendAsync("220 fake\r\n"));
        var send = new Plugins.EmailSender(time).SendAsync("127.0.0.1", _server.Port, "a@b", "c@d", null, null, null, "x", null, null, null, Token);

        await AdvanceUntilDoneAsync(time, send, TimeSpan.FromMilliseconds(100));

        Assert.Equal("Timed out", await send);
    }

    [Fact]
    public async Task Debug_output_gets_each_reply()
    {
        _server.Expect(async c =>
        {
            await c.SendAsync("220 fake\r\n");
            await Exchange(c, "EHLO\r\n", "250 fake\r\n");
            await Exchange(c, "MAIL From: <a@b>\r\n", "550 no\r\n");
        });
        var output = new StringWriter();

        Assert.Equal("Failed on error code 550",
            await new Plugins.EmailSender().SendAsync("127.0.0.1", _server.Port, "a@b", "c@d", null, null, null, "x", null, output, null, Token));
        Assert.Equal("220 fake\r\n250 fake\r\n550 no\r\n", output.ToString());
    }

    [Fact]
    public async Task With_ssl_a_220_reply_starts_authentication_with_AUTH_PLAIN()
    {
        // OPEN_SSL_CLIENT_SUPPORT: TLS from the start (EmailSender.cpp:46-48); a reply holding 220 asks for
        // authentication (EmailSender.cpp:330-335), then EHLO with the sender and AUTH PLAIN of "\0sender\0password\0".
        using var certificate = CreateCertificate();
        _server.Expect(async c =>
        {
            await c.StartTlsAsync(certificate);
            await c.SendAsync("220 fake\r\n");
            await Exchange(c, "EHLO\r\n", "250-STARTTLS\r\n");
            await Exchange(c, "STARTTLS\r\n", "220 Ready\r\n");
            await Exchange(c, "EHLO a@b\r\n", "250 fake\r\n");
            await Exchange(c, "AUTH PLAIN AGFAYgBzZWNyZXQA\r\n", "235 Accepted\r\n");
            await Exchange(c, "MAIL From: <a@b>\r\n", "250 OK\r\n");
            await Exchange(c, "RCPT TO: <c@d>\r\n", "250 OK\r\n");
            await Exchange(c, "DATA\r\n", "354 Go ahead\r\n");
            await c.ReadUntilAsync("\r\n.\r\n");
            await c.SendAsync("250 Queued\r\n");
        });
        var sender = new Plugins.EmailSender
        {
            UseSsl = true,
            RemoteCertificateValidation = (_, presented, _, _) => presented?.GetCertHashString() == certificate.GetCertHashString(),
        };

        Assert.Null(await sender.SendAsync("localhost", _server.Port, "a@b", "c@d", null, null, null, "x", null, null, "secret", Token));
        await _server.CompletionAsync();
    }

    [Fact]
    public async Task With_ssl_authentication_needs_a_password()
    {
        using var certificate = CreateCertificate();
        _server.Expect(async c =>
        {
            await c.StartTlsAsync(certificate);
            await c.SendAsync("220 fake\r\n");
            await Exchange(c, "EHLO\r\n", "220 again\r\n");
            await Exchange(c, "EHLO a@b\r\n", "250 fake\r\n");
        });
        var sender = new Plugins.EmailSender
        {
            UseSsl = true,
            RemoteCertificateValidation = (_, presented, _, _) => presented?.GetCertHashString() == certificate.GetCertHashString(),
        };

        Assert.Equal("Password needed", await sender.SendAsync("localhost", _server.Port, "a@b", "c@d", null, null, null, "x", null, null, null, Token));
    }

    private static async Task Exchange(ScriptedServer.Conversation conversation, string expected, string reply)
    {
        Assert.Equal(expected, await conversation.ReadUntilAsync(expected[^2..]));
        await conversation.SendAsync(reply);
    }

    private static async Task AdvanceUntilDoneAsync(FakeTimeProvider time, Task task, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "The send never finished.");
            await Task.Delay(5, Token);
            time.Advance(step);
        }
    }

    internal static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }
}
