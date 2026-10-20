using System.Net.Security;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// EmailSender: sends one email over SMTP with a <see cref="TcpInterface"/> of its own, writing what RakNet writes and
/// reading the server's replies as RakNet does (EmailSender.cpp).
/// </summary>
/// <remarks>
/// <para>
/// Each reply is the next received packet, matched by the first code it contains: 235, 354 and 250 go on, 550 and 553
/// fail, and a packet with none of them is skipped. The server has 3 seconds for its greeting and 5 for each later
/// reply, polled every 250 and 100 milliseconds.
/// </para>
/// <para>
/// <see cref="UseSsl"/> is RakNet built with <c>OPEN_SSL_CLIENT_SUPPORT</c>: TLS from the connect on, and the AUTH PLAIN
/// login, which only that build reaches. Without it, as RakNet's default build, the password is never sent.
/// </para>
/// <para>Text is written as UTF-8, up to its first NUL as sprintf writes it; replies are read up to their first zero byte.</para>
/// </remarks>
public sealed class EmailSender
{
    private const string Authenticate = "AUTHENTICATE";
    private const string Continue = "CONTINUE";
    private const int BoundarySize = 60;

    private static readonly TimeSpan GreetingTimeout = TimeSpan.FromMilliseconds(3000);
    private static readonly TimeSpan GreetingPoll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(5000);
    private static readonly TimeSpan ReplyPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan QuitPause = TimeSpan.FromMilliseconds(30);

    private readonly TimeProvider _time;

    public EmailSender(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>RakNet's <c>OPEN_SSL_CLIENT_SUPPORT</c> build: TLS to the server, STARTTLS and AUTH PLAIN.</summary>
    public bool UseSsl { get; init; }

    /// <summary>Checks the server's certificate when <see cref="UseSsl"/> is set; see <see cref="TcpInterface.RemoteCertificateValidation"/>.</summary>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; init; }

    /// <summary>
    /// Send: connects to <paramref name="hostAddress"/>, sends the email and quits. Null on success, otherwise RakNet's
    /// text for the error (EmailSender.cpp:36-307).
    /// </summary>
    /// <param name="hostAddress">The mail server.</param>
    /// <param name="hostPort">Its port, usually 25.</param>
    /// <param name="sender">The envelope sender; null is <c>&lt;&gt;</c>.</param>
    /// <param name="recipient">The envelope recipient; null is <c>&lt;&gt;</c>.</param>
    /// <param name="senderName">The From header; null writes none.</param>
    /// <param name="recipientName">The To header; null writes none.</param>
    /// <param name="subject">The Subject header; null writes none.</param>
    /// <param name="body">The text, dot-stuffed as RakNet does it.</param>
    /// <param name="attachedFiles">Files sent as base64 parts of a multipart message; null or empty for none.</param>
    /// <param name="debugOutput">RakNet's doPrintf: each reply read is written here.</param>
    /// <param name="password">For AUTH PLAIN with <see cref="UseSsl"/>.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<string?> SendAsync(string hostAddress, ushort hostPort, string? sender, string? recipient, string? senderName,
        string? recipientName, string? subject, string body, IReadOnlyList<EmailAttachment>? attachedFiles, TextWriter? debugOutput,
        string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostAddress);
        ArgumentNullException.ThrowIfNull(body);
        using var tcp = new TcpInterface(_time) { RemoteCertificateValidation = RemoteCertificateValidation };
        if (!tcp.Start(0, 0))
            return "Unknown error starting TCP";
        var server = await Task.Run(() => tcp.Connect(hostAddress, hostPort, block: true), cancellationToken).ConfigureAwait(false);
        if (server == SystemAddress.Unassigned)
            return "Failed to connect to host";
        if (UseSsl)
            tcp.StartSslClient(server, hostAddress);

        void Write(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            tcp.Send(bytes, server, broadcast: false);
        }

        // EmailSender.cpp:50-68: the first packet is the greeting, whatever it says.
        var greetingStart = _time.GetTimestamp();
        TcpPacket? packet = null;
        while (_time.GetElapsedTime(greetingStart) < GreetingTimeout)
        {
            packet = tcp.Receive();
            if (packet is not null)
            {
                debugOutput?.Write(Text(packet));
                break;
            }
            await Task.Delay(GreetingPoll, _time, cancellationToken).ConfigureAwait(false);
        }
        if (packet is null)
            return "Timeout while waiting for initial data from server.";

        Write("EHLO\r\n");
        string? response;
        var authenticate = false;
        while (true)
        {
            response = await GetResponseAsync(tcp, server, hostAddress, debugOutput, cancellationToken).ConfigureAwait(false);
            if (response == Authenticate)
            {
                authenticate = true;
                break;
            }
            if (response is not null && response != Continue)
                return response;
            if (response is null)
                break;
        }

        if (authenticate)
        {
            // EmailSender.cpp:96-122. The base64 text ends with its CRLF, which ends the command.
            Write($"EHLO {CString(sender)}\r\n");
            response = await GetResponseAsync(tcp, server, hostAddress, debugOutput, cancellationToken).ConfigureAwait(false);
            if (response is not null)
                return response;
            if (password is null)
                return "Password needed";
            var login = Encoding.UTF8.GetBytes($"\0{CString(sender)}\0{CString(password)}\0");
            Write($"AUTH PLAIN {Base64Encoder.Encode(login)}");
            response = await GetResponseAsync(tcp, server, hostAddress, debugOutput, cancellationToken).ConfigureAwait(false);
            if (response is not null)
                return response;
        }

        Write(sender is null ? "MAIL From: <>\r\n" : $"MAIL From: <{CString(sender)}>\r\n");
        response = await GetResponseAsync(tcp, server, hostAddress, debugOutput, cancellationToken).ConfigureAwait(false);
        if (response is not null)
            return response;

        Write(recipient is null ? "RCPT TO: <>\r\n" : $"RCPT TO: <{CString(recipient)}>\r\n");
        response = await GetResponseAsync(tcp, server, hostAddress, debugOutput, cancellationToken).ConfigureAwait(false);
        if (response is not null)
            return response;

        Write("DATA\r\n");
        response = await GetResponseAsync(tcp, server, hostAddress, debugOutput, cancellationToken).ConfigureAwait(false);
        if (response is not null)
            return response;

        if (subject is not null)
            Write($"Subject: {CString(subject)}\r\n");
        if (senderName is not null)
            Write($"From: {CString(senderName)}\r\n");
        if (recipientName is not null)
            Write($"To: {CString(recipientName)}\r\n");

        var hasAttachments = attachedFiles is { Count: > 0 };
        var boundary = hasAttachments ? CreateBoundary() : "";
        Write("MIME-version: 1.0\r\n");
        if (hasAttachments)
        {
            Write($"Content-type: multipart/mixed; BOUNDARY=\"{boundary}\"\r\n\r\n");
            Write($"This is a multi-part message in MIME format.\r\n\r\n--{boundary}\r\n");
        }
        Write("Content-Type: text/plain; charset=\"US-ASCII\"\r\n\r\n");
        tcp.Send(Encoding.UTF8.GetBytes(DotStuff(CString(body))), server, broadcast: false);

        if (hasAttachments)
        {
            foreach (var file in attachedFiles!)
            {
                Write($"\r\n--{boundary}\r\n");
                Write($"Content-Type: APPLICATION/Octet-Stream; SizeOnDisk={file.Data.Length}; name=\"{CString(file.FileName)}\"\r\n" +
                    $"Content-Transfer-Encoding: BASE64\r\nContent-Description: {CString(file.FileName)}\r\n\r\n");
                Write(Base64Encoder.Encode(file.Data.Span));
            }
            Write($"\r\n--{boundary}--\r\n");
        }

        Write("\r\n.\r\n");
        response = await GetResponseAsync(tcp, server, hostAddress, debugOutput, cancellationToken).ConfigureAwait(false);
        if (response is not null)
            return response;

        Write("QUIT\r\n");
        await Task.Delay(QuitPause, _time, cancellationToken).ConfigureAwait(false);
        if (debugOutput is not null)
        {
            while (tcp.Receive() is { } after)
                debugOutput.Write(Text(after));
        }
        tcp.Stop();
        return null;
    }

    /// <summary>
    /// The body as EmailSender.cpp:178-246 rewrites it, with the CRLF it appends: a line holding "." or ".." gets one
    /// more dot and ends with CRLF, whether it ended with CRLF or a bare LF. The first character is never rewritten, and
    /// an empty body is sent as its terminating zero.
    /// </summary>
    internal static string DotStuff(string body)
    {
        var length = body.Length;
        char At(int index) => index < length ? body[index] : '\0';

        var output = new StringBuilder((length * 3) + 3);
        output.Append(At(0));
        for (var i = 1; i < length; i++)
        {
            if (i < length - 2 && body[i - 1] == '\n' && body[i] == '.' && At(i + 1) == '\r' && At(i + 2) == '\n')
            {
                output.Append("..\r\n");
                i += 2;
            }
            else if (i <= length - 3 && body[i - 1] == '\n' && body[i] == '.' && At(i + 1) == '.' && At(i + 2) == '\r' && At(i + 3) == '\n')
            {
                output.Append("...\r\n");
                i += 3;
            }
            else if (i < length - 1 && body[i - 1] == '\n' && body[i] == '.' && At(i + 1) == '\n')
            {
                output.Append("..\r\n");
                i += 1;
            }
            else if (i <= length - 2 && body[i - 1] == '\n' && body[i] == '.' && At(i + 1) == '.' && At(i + 2) == '\n')
            {
                output.Append("...\r\n");
                i += 2;
            }
            else
            {
                output.Append(body[i]);
            }
        }
        output.Append("\r\n");
        return output.ToString();
    }

    // EmailSender.cpp:310-363.
    private async Task<string?> GetResponseAsync(TcpInterface tcp, SystemAddress server, string hostAddress, TextWriter? debugOutput,
        CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            if (tcp.HasLostConnection() == server)
                return "Connection to server lost.";
            if (tcp.Receive() is { } packet)
            {
                var text = Text(packet);
                debugOutput?.Write(text);
                if (UseSsl && text.Contains("220", StringComparison.Ordinal))
                {
                    tcp.StartSslClient(packet.Address, hostAddress);
                    return Authenticate;
                }
                if (text.Contains("235", StringComparison.Ordinal))
                    return null;
                if (text.Contains("354", StringComparison.Ordinal))
                    return null;
                if (UseSsl && text.Contains("250-STARTTLS", StringComparison.Ordinal))
                {
                    tcp.Send("STARTTLS\r\n"u8, packet.Address, broadcast: false);
                    return Continue;
                }
                if (text.Contains("250", StringComparison.Ordinal))
                    return null;
                if (text.Contains("550", StringComparison.Ordinal))
                    return "Failed on error code 550";
                if (text.Contains("553", StringComparison.Ordinal))
                    return "Failed on error code 553";
            }
            if (_time.GetElapsedTime(start) > ReplyTimeout)
                return "Timed out";
            await Task.Delay(ReplyPoll, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    // EmailSender.cpp:163-176: characters of the base64 alphabet. RakNet seeds its generator with the time, so the
    // boundary is unpredictable there too.
    private static string CreateBoundary()
    {
        var boundary = new char[BoundarySize];
        for (var i = 0; i < boundary.Length; i++)
            boundary[i] = Base64Encoder.Map[Random.Shared.Next(64)];
        return new string(boundary);
    }

    private static string Text(TcpPacket packet)
    {
        var data = packet.Data.Span;
        var end = data.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? data[..end] : data);
    }

    private static string CString(string? text) => text is null ? "" : ConsoleText.CString(text);
}
