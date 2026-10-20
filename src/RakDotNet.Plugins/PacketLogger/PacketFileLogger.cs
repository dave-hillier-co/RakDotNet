using System.Globalization;
using System.Text;

namespace RakDotNet.Plugins;

/// <summary>
/// PacketFileLogger: a <see cref="PacketLogger"/> that writes to a CSV file opened by <see cref="StartLog"/>, flushing
/// after every line. Lines logged before <see cref="StartLog"/> are dropped, as in RakNet.
/// </summary>
/// <remarks>Ported from PacketFileLogger.cpp.</remarks>
public class PacketFileLogger : PacketLogger, IDisposable
{
    private readonly Lock _gate = new();
    private StreamWriter? _file;

    public PacketFileLogger()
        : base(TextWriter.Null)
    {
    }

    /// <summary>
    /// StartLog (PacketFileLogger.cpp:31-48): opens <c>{prefix}_{time}.csv</c>, or <c>PacketLog_{time}.csv</c> for a null
    /// prefix, where time is the host's <see cref="IPluginHost.GetTime"/> as a signed 32-bit number (0 before attach),
    /// and writes the header. The prefix may include a directory.
    /// </summary>
    public void StartLog(string? filenamePrefix)
    {
        var time = unchecked((int)(uint)(Host?.GetTime() ?? 0));
        var name = string.Create(CultureInfo.InvariantCulture, $"{filenamePrefix ?? "PacketLog"}_{time}.csv");
        lock (_gate)
        {
            _file?.Dispose();
            _file = new StreamWriter(name, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        LogHeader();
    }

    /// <summary>Writes the line and <c>\n</c> to the file, if one is open, and flushes.</summary>
    public override void WriteLog(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (_file is null)
                return;
            _file.Write(line);
            _file.Write('\n');
            _file.Flush();
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
