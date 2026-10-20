using System.Diagnostics;

namespace RakDotNet.Plugins;

/// <summary>
/// PacketOutputWindowLogger: a <see cref="PacketLogger"/> that writes each line to an attached debugger's output, as
/// OutputDebugString does (PacketOutputWindowLogger.cpp:30-49).
/// </summary>
public class PacketOutputWindowLogger : PacketLogger
{
    public PacketOutputWindowLogger()
        : base(TextWriter.Null)
    {
    }

    public override void WriteLog(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Debugger.Log(0, null, line + "\n");
    }
}
