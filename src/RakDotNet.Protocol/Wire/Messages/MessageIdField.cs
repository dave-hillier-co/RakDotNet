namespace RakDotNet.Protocol.Wire.Messages;

internal static class MessageIdField
{
    public static void Write(ref BitStreamWriter writer, MessageId id) => writer.WriteByte((byte)id);

    public static bool TryRead(ref BitStreamReader reader, MessageId expected) =>
        reader.TryReadByte(out var id) && id == (byte)expected;
}
