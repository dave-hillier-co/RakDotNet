using System.Text;

namespace RakDotNet.Plugins;

/// <summary>
/// The C string handling the console classes share: text is bytes, one character each (Latin-1), ends at its first
/// zero byte, and is at most REMOTE_MAX_TEXT_INPUT - 1 characters long where RakNet formats it into a buffer.
/// </summary>
internal static class ConsoleText
{
    /// <summary>REMOTE_MAX_TEXT_INPUT (TransportInterface.h:24): the size of every console text buffer.</summary>
    public const int MaxTextInput = 2048;

    /// <summary>The characters of a C string, up to its first zero byte.</summary>
    public static string FromBytes(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end >= 0 ? data[..end] : data);
    }

    public static byte[] ToBytes(string text) => Encoding.Latin1.GetBytes(text);

    /// <summary>The text as strlen sees it: up to the first NUL.</summary>
    public static string CString(string text)
    {
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>What _vsnprintf into a buffer of <paramref name="size"/> keeps: at most size - 1 characters.</summary>
    public static string Truncate(string text, int size) => text.Length < size ? text : text[..Math.Max(0, size - 1)];

    /// <summary>
    /// A string RakNet passes as the format with no arguments, as ShowPrompt passes the prompt
    /// (ConsoleServer.cpp:303-306) and LogCommandParser its text (LogCommandParser.cpp:174): <c>%%</c> prints one
    /// <c>%</c>. Any other conversion has no argument to read, which is undefined in C; it is left as written.
    /// </summary>
    public static string FormatWithoutArguments(string text) =>
        text.Contains("%%", StringComparison.Ordinal) ? text.Replace("%%", "%", StringComparison.Ordinal) : text;

    /// <summary>_stricmp / strcasecmp: compares the characters lower-cased in the C locale.</summary>
    public static int CompareIgnoreCase(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var difference = ToLower(left[i]) - ToLower(right[i]);
            if (difference != 0)
                return difference;
        }
        return left.Length - right.Length;
    }

    public static bool EqualsIgnoreCase(string left, string right) => CompareIgnoreCase(left, right) == 0;

    /// <summary>
    /// atoi: leading white space, an optional sign, then decimal digits; 0 when there are none. Overflow, undefined
    /// in C, wraps.
    /// </summary>
    public static int Atoi(string? text)
    {
        if (text is null)
            return 0;
        var i = 0;
        while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
            i++;
        var negative = false;
        if (i < text.Length && text[i] is '+' or '-')
        {
            negative = text[i] == '-';
            i++;
        }
        var value = 0;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
            value = unchecked(value * 10 + (text[i] - '0'));
        return negative ? unchecked(-value) : value;
    }

    private static char ToLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
}
