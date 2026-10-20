using System.Text;

namespace RakDotNet.Plugins;

/// <summary>
/// RakString's HTTP helpers: FormatForGET, FormatForPOST, FormatForPUT and FormatForDELETE, whose output is passed to
/// <see cref="HttpConnection2.TransmitRequest"/>, the SplitURI they use, and the URLDecode
/// <see cref="HttpConnection"/> applies to what it receives (RakString.cpp:890-1164).
/// </summary>
/// <remarks>Lengths are bytes, as RakString's strlen counts them; strings are written as UTF-8.</remarks>
public static class HttpFormat
{
    /// <summary>FormatForGET; empty when the URI has no host or no path (RakString.cpp:1091-1126).</summary>
    public static string FormatForGet(string uri, string? extraHeaders = null)
    {
        var (_, host, path) = SplitUri(uri);
        if (host.Length == 0 || path.Length == 0)
            return "";
        return string.IsNullOrEmpty(extraHeaders)
            ? $"GET {path} HTTP/1.1\r\nHost: {host}\r\n\r\n"
            : $"GET {path} HTTP/1.1\r\n{extraHeaders}\r\nHost: {host}\r\n\r\n";
    }

    /// <summary>FormatForPOST (RakString.cpp:1083-1086).</summary>
    public static string FormatForPost(string uri, string contentType, string body, string? extraHeaders = null) =>
        FormatForPutOrPost("POST", uri, contentType, body, extraHeaders);

    /// <summary>FormatForPUT (RakString.cpp:1087-1090).</summary>
    public static string FormatForPut(string uri, string contentType, string body, string? extraHeaders = null) =>
        FormatForPutOrPost("PUT", uri, contentType, body, extraHeaders);

    /// <summary>FormatForDELETE (RakString.cpp:1127-1164).</summary>
    public static string FormatForDelete(string uri, string? extraHeaders = null)
    {
        var (_, host, path) = SplitUri(uri);
        if (host.Length == 0 || path.Length == 0)
            return "";
        return string.IsNullOrEmpty(extraHeaders)
            ? $"DELETE {path} HTTP/1.1\r\nContent-Length: 0\r\nHost: {host}\r\nConnection: close\r\n\r\n"
            : $"DELETE {path} HTTP/1.1\r\n{extraHeaders}\r\nContent-Length: 0\r\nHost: {host}\r\nConnection: close\r\n\r\n";
    }

    /// <summary>
    /// SplitURI: a leading "http://" or "https://", else a leading "www.", then the host up to the first '/', then the
    /// rest (RakString.cpp:939-991).
    /// </summary>
    public static (string Header, string Domain, string Path) SplitUri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var start = uri.StartsWith("http://", StringComparison.Ordinal) ? 7
            : uri.StartsWith("https://", StringComparison.Ordinal) ? 8
            : 0;
        // The "www." check looks at the start of the URI, not after the scheme (RakString.cpp:954-955).
        if (uri.StartsWith("www.", StringComparison.Ordinal))
            start += 4;
        var slash = uri.IndexOf('/', start);
        var domainEnd = slash < 0 ? uri.Length : slash;
        return (uri[..start], uri[start..domainEnd], uri[domainEnd..]);
    }

    /// <summary>
    /// URLDecode as RakString does it, on the bytes up to the first zero, and returning the bytes up to the first zero
    /// it decodes (RakString.cpp:890-938).
    /// </summary>
    /// <remarks>
    /// RakNet's hex conversion has an if without an else before the a-f test, so an upper-case A-F digit takes the value
    /// of its character minus '0' (%2F decodes to '6'). An escape at the end reads the terminator and, past it, zero.
    /// </remarks>
    public static byte[] UrlDecode(ReadOnlySpan<byte> text)
    {
        text = CString(text);
        var output = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == (byte)'%')
            {
                var high = HexValue(++i < text.Length ? text[i] : (byte)0);
                var low = HexValue(++i < text.Length ? text[i] : (byte)0);
                c = unchecked((byte)(high * 16 + low));
            }
            if (c == 0)
                break;
            output.Add(c);
        }
        return [.. output];
    }

    /// <summary>The bytes up to the first zero: what C string functions see of RakNet's null-terminated packet data.</summary>
    internal static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        return end < 0 ? data : data[..end];
    }

    private static int HexValue(byte digit)
    {
        // RakString.cpp:912-917, char arithmetic on a signed char.
        var c = (sbyte)digit;
        int value = 0;
        if (c >= 'A' && c <= 'F')
            value = c - 'A' + 10;
        if (c >= 'a' && c <= 'f')
            value = c - 'a' + 10;
        else
            value = c - '0';
        return value;
    }

    // FormatForPUTOrPost (RakString.cpp:1028-1082).
    private static string FormatForPutOrPost(string type, string uri, string contentType, string body, string? extraHeaders)
    {
        var (_, host, path) = SplitUri(uri);
        if (host.Length == 0 || path.Length == 0)
            return "";
        var length = Encoding.UTF8.GetByteCount(body);
        return string.IsNullOrEmpty(extraHeaders)
            ? $"{type} {path} HTTP/1.1\r\nHost: {host}\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\n\r\n{body}"
            : $"{type} {path} HTTP/1.1\r\n{extraHeaders}\r\nHost: {host}\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\n\r\n{body}";
    }
}
