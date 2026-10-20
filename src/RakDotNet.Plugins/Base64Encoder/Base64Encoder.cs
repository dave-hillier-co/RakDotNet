using System.Text;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's Base64Encoding and Base64Map (Base64Encoder.cpp), the layout <see cref="EmailSender"/> and
/// <see cref="DynDns"/> put on the wire: the standard alphabet, a CRLF after every 76th character written (padding is
/// not counted), and a CRLF at the end, so empty input gives "\r\n".
/// </summary>
public static class Base64Encoder
{
    private const int LineLength = 76;

    /// <summary>Base64Map: the 64 characters, in value order.</summary>
    public const string Map = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>Base64Encoding of <paramref name="data"/>.</summary>
    /// <remarks>
    /// With one byte left over, RakNet reads the byte after the input for the second character's low bits
    /// (Base64Encoder.cpp:51-55). Its callers pass C strings, whose next byte is the terminator, so it is read as zero.
    /// </remarks>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder(((data.Length + 2) / 3 * 4) + (data.Length / 57 * 2) + 4);
        var charCount = 0;

        void Put(int value)
        {
            output.Append(Map[value & 63]);
            if (++charCount % LineLength == 0)
            {
                output.Append("\r\n");
                charCount = 0;
            }
        }

        var groups = data.Length / 3;
        for (var j = 0; j < groups; j++)
        {
            int a = data[j * 3], b = data[(j * 3) + 1], c = data[(j * 3) + 2];
            Put(a >> 2);
            Put((a << 4) | (b >> 4));
            Put((b << 2) | (c >> 6));
            Put(c);
        }

        var rest = data[(groups * 3)..];
        if (rest.Length == 1)
        {
            Put(rest[0] >> 2);
            Put(rest[0] << 4);
            output.Append("==");
        }
        else if (rest.Length == 2)
        {
            Put(rest[0] >> 2);
            Put((rest[0] << 4) | (rest[1] >> 4));
            Put(rest[1] << 2);
            output.Append('=');
        }

        output.Append("\r\n");
        return output.ToString();
    }
}
