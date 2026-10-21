namespace RakDotNet.Plugins.Tests.Base64Encoder;

/// <summary>
/// Base64Encoding (Base64Encoder.cpp:17-94): the standard alphabet, a CRLF after every 76 characters written, and a
/// CRLF at the end. Expected text is derived from the C++.
/// </summary>
public sealed class Base64EncoderTests
{
    [Fact]
    public void The_map_is_the_standard_alphabet()
    {
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/", Plugins.Base64Encoder.Map);
    }

    [Theory]
    [InlineData("", "\r\n")]
    [InlineData("Man", "TWFu\r\n")]
    [InlineData("Ma", "TWE=\r\n")]
    [InlineData("M", "TQ==\r\n")]
    [InlineData("user:pass", "dXNlcjpwYXNz\r\n")]
    public void Output_ends_with_a_CRLF(string input, string expected)
    {
        Assert.Equal(expected, Plugins.Base64Encoder.Encode(System.Text.Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void One_byte_left_over_reads_the_byte_after_it_as_zero()
    {
        // Base64Encoder.cpp:51-55 reads inputData[j*3+1], one past the end; for a C string that is its terminator.
        Assert.Equal("/w==\r\n", Plugins.Base64Encoder.Encode([0xFF]));
    }

    [Fact]
    public void A_CRLF_follows_every_76th_character()
    {
        var output = Plugins.Base64Encoder.Encode(new byte[60]);

        // 60 bytes are 80 characters: 76, a line break, 4, then the closing CRLF.
        Assert.Equal(new string('A', 76) + "\r\n" + "AAAA" + "\r\n", output);
    }

    [Fact]
    public void Exactly_76_characters_end_with_two_CRLFs()
    {
        Assert.Equal(new string('A', 76) + "\r\n\r\n", Plugins.Base64Encoder.Encode(new byte[57]));
    }

    [Fact]
    public void Padding_is_not_counted_towards_the_line_length()
    {
        // 55 bytes: 74 characters and "==" make a line of 76 with no break inside it (Base64Encoder.cpp:57-60).
        Assert.Equal(new string('A', 74) + "==\r\n", Plugins.Base64Encoder.Encode(new byte[55]));
        // 56 bytes: 75 characters and "=".
        Assert.Equal(new string('A', 75) + "=\r\n", Plugins.Base64Encoder.Encode(new byte[56]));
    }

    [Fact]
    public void A_break_can_fall_between_the_characters_of_the_last_group()
    {
        // 58 bytes: 76 characters, a break after the 76th, then the two characters of the last byte and "==".
        Assert.Equal(new string('A', 76) + "\r\nAA==\r\n", Plugins.Base64Encoder.Encode(new byte[58]));
    }

    [Fact]
    public void Matches_the_standard_encoding_within_a_line()
    {
        var data = new byte[48];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 37);

        Assert.Equal(Convert.ToBase64String(data) + "\r\n", Plugins.Base64Encoder.Encode(data));
    }
}
