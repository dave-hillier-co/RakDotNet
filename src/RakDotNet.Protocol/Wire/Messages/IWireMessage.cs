using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// A wire structure with the codec shape from docs/conventions.md: an instance <see cref="Write"/> and a
/// static <see cref="TryRead"/> that returns false on malformed or truncated input and never throws for bad data.
/// </summary>
public interface IWireMessage<TSelf>
    where TSelf : IWireMessage<TSelf>
{
    void Write(ref BitStreamWriter writer);

    static abstract bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out TSelf value);
}
