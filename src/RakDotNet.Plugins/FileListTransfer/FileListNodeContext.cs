using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// FileListNodeContext (FileListNodeContext.h): user data stored with each file of a <see cref="FileList"/> and sent
/// with it.
/// </summary>
/// <remarks>
/// RakNet's dataPtr and dataLength members are local bookkeeping that never travel, and are left out. Two encodings
/// exist: <see cref="Write"/> and <see cref="TryRead"/> are the BitStream operators FileListTransfer uses, the op byte and
/// the three extra values as big-endian u32s; <see cref="FileList.Serialize"/> writes the op and the first two extra
/// values compressed instead, and drops the third.
/// </remarks>
public readonly record struct FileListNodeContext(byte Op, uint ExtraData1, uint ExtraData2, uint ExtraData3)
{
    /// <summary>Bytes <see cref="Write"/> takes.</summary>
    public const int SerializedLength = 1 + 3 * sizeof(uint);

    /// <summary>operator&lt;&lt; (FileListNodeContext.h:34-41).</summary>
    public void Write(ref BitStreamWriter writer)
    {
        writer.WriteByte(Op);
        writer.WriteUInt32(ExtraData1);
        writer.WriteUInt32(ExtraData2);
        writer.WriteUInt32(ExtraData3);
    }

    /// <summary>operator&gt;&gt; (FileListNodeContext.h:42-56); false when the stream is too short.</summary>
    public static bool TryRead(ref BitStreamReader reader, out FileListNodeContext value)
    {
        value = default;
        if (!reader.TryReadByte(out var op) || !reader.TryReadUInt32(out var extra1) || !reader.TryReadUInt32(out var extra2)
            || !reader.TryReadUInt32(out var extra3))
            return false;
        value = new FileListNodeContext(op, extra1, extra2, extra3);
        return true;
    }
}
