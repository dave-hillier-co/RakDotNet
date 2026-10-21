using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.TcpInterface;

/// <summary>
/// StartSSLClient: after connecting, the client continues over TLS (TCPInterface.cpp:420-439, 1312-1415), against an
/// in-process TLS server with a self-signed certificate.
/// </summary>
public sealed class TcpInterfaceTlsTests : IDisposable
{
    private readonly X509Certificate2 _certificate = CreateCertificate();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Plugins.TcpInterface _client = new();

    public TcpInterfaceTlsTests()
    {
        _listener.Start();
        _client.Start(0, 0);
    }

    private ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _client.Dispose();
        _listener.Stop();
        _listener.Dispose();
        _certificate.Dispose();
    }

    [Fact]
    public async Task Data_after_starting_tls_is_encrypted_both_ways()
    {
        var token = TestContext.Current.CancellationToken;
        _client.RemoteCertificateValidation = (_, certificate, _, _) => certificate?.GetCertHashString() == _certificate.GetCertHashString();
        var serverTask = EchoOverTlsAsync(token);

        var server = _client.Connect("127.0.0.1", Port, block: true);
        _client.StartSslClient(server, "localhost");
        _client.Send("hello"u8, server, broadcast: false);

        Assert.Equal("hello"u8.ToArray(), Poll.Bytes(_client, server, 5));
        Assert.True(_client.IsSslActive(server));
        Assert.Equal("hello"u8.ToArray(), await serverTask);
    }

    [Fact]
    public async Task A_certificate_that_fails_validation_loses_the_connection()
    {
        var token = TestContext.Current.CancellationToken;
        _ = EchoOverTlsAsync(token);

        // .NET's default validation rejects the self-signed certificate.
        var server = _client.Connect("127.0.0.1", Port, block: true);
        _client.StartSslClient(server, "localhost");
        _client.Send("secret"u8, server, broadcast: false);

        Assert.Equal(server, Poll.Address(_client.HasLostConnection, "the lost connection"));
        Assert.False(_client.ReceiveHasPackets());
    }

    [Fact]
    public void Closing_the_connection_ends_its_tls()
    {
        var server = _client.Connect("127.0.0.1", Port, block: true);
        _client.StartSslClient(server);
        Assert.True(_client.IsSslActive(server));

        _client.CloseConnection(server);

        Assert.False(_client.IsSslActive(server));
        _ = SystemAddress.Unassigned;
    }

    private async Task<byte[]> EchoOverTlsAsync(CancellationToken token)
    {
        using var socket = await _listener.AcceptSocketAsync(token);
        await using var ssl = new SslStream(new NetworkStream(socket, ownsSocket: false));
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, token);
        }
        catch (Exception exception) when (exception is IOException or System.Security.Authentication.AuthenticationException)
        {
            return [];
        }
        var buffer = new byte[5];
        var read = 0;
        while (read < buffer.Length)
            read += await ssl.ReadAsync(buffer.AsMemory(read), token);
        await ssl.WriteAsync(buffer, token);
        await ssl.FlushAsync(token);
        // Keep the connection open until the client has read the reply.
        await Task.Delay(500, token);
        return buffer;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // A certificate with an ephemeral key cannot serve TLS on every platform; one loaded from PKCS#12 can.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }
}
