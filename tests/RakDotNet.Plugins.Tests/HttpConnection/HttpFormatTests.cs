using System.Text;

namespace RakDotNet.Plugins.Tests.HttpConnection;

/// <summary>RakString's HTTP helpers, which HTTPConnection2's users format requests with (RakString.cpp:890-1164).</summary>
public sealed class HttpFormatTests
{
    [Fact]
    public void A_get_names_the_path_and_the_host()
    {
        Assert.Equal("GET /path/file.txt HTTP/1.1\r\nHost: example.com\r\n\r\n", HttpFormat.FormatForGet("http://example.com/path/file.txt"));
    }

    [Fact]
    public void Extra_headers_come_first_on_their_own_line()
    {
        Assert.Equal("GET /a HTTP/1.1\r\nX-Auth: 1\r\nHost: example.com\r\n\r\n", HttpFormat.FormatForGet("example.com/a", "X-Auth: 1"));
    }

    [Fact]
    public void A_post_counts_the_body_in_bytes()
    {
        Assert.Equal(
            "POST /submit HTTP/1.1\r\nHost: example.com:8080\r\nContent-Type: text/plain\r\nContent-Length: 6\r\n\r\nhéllo",
            HttpFormat.FormatForPost("https://example.com:8080/submit", "text/plain", "héllo"));
    }

    [Fact]
    public void A_put_is_a_post_with_another_verb()
    {
        Assert.Equal(
            "PUT /x HTTP/1.1\r\nA: b\r\nHost: h\r\nContent-Type: c\r\nContent-Length: 1\r\n\r\nz",
            HttpFormat.FormatForPut("h/x", "c", "z", "A: b"));
    }

    [Fact]
    public void A_delete_closes_the_connection()
    {
        Assert.Equal(
            "DELETE /x HTTP/1.1\r\nContent-Length: 0\r\nHost: h\r\nConnection: close\r\n\r\n",
            HttpFormat.FormatForDelete("h/x"));
    }

    [Fact]
    public void A_uri_without_a_path_or_host_formats_nothing()
    {
        Assert.Equal("", HttpFormat.FormatForGet("http://example.com"));
        Assert.Equal("", HttpFormat.FormatForPost("/path", "c", "b"));
    }

    [Fact]
    public void Splitting_a_uri_strips_the_scheme_and_a_leading_www()
    {
        // RakString::SplitURI checks for "www." only at the very start (RakString.cpp:949-955).
        Assert.Equal(("http://", "example.com", "/a/b"), HttpFormat.SplitUri("http://example.com/a/b"));
        Assert.Equal(("www.", "example.com", "/"), HttpFormat.SplitUri("www.example.com/"));
        Assert.Equal(("http://", "www.example.com", "/"), HttpFormat.SplitUri("http://www.example.com/"));
    }

    [Fact]
    public void Url_decoding_turns_lower_case_escapes_into_bytes()
    {
        Assert.Equal("a b/c", Decode("a%20b%2fc"));
    }

    [Fact]
    public void Url_decoding_reads_upper_case_hex_digits_as_RakNet_does()
    {
        // RakString.cpp:912-917: the a-f test has an else that overwrites the A-F value, so 'F' counts 'F' - '0' = 22,
        // and %2F decodes to 2 * 16 + 22 = '6'.
        Assert.Equal("6", Decode("%2F"));
    }

    [Fact]
    public void Url_decoding_stops_at_a_decoded_zero_byte()
    {
        Assert.Equal("ab", Decode("ab%00cd"));
    }

    private static string Decode(string text) => Encoding.Latin1.GetString(HttpFormat.UrlDecode(Encoding.Latin1.GetBytes(text)));
}
