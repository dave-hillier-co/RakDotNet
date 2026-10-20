using System.Text.Json;
using RakDotNet.Protocol.Security;

namespace RakDotNet.Tests.Security;

/// <summary>
/// Key pairs from the known-answer vectors tools/cat-vectors computes with RakNet's cat code (tests/vectors/cat):
/// the key pairs EasyHandshake::GenerateServerKey makes from recorded random draws, and the keys cat's server,
/// client and identity checks accept or refuse.
/// </summary>
internal static class CatKeyVectors
{
    private static readonly Lazy<string> Folder = new(FindFolder);

    public static IEnumerable<JsonElement> GeneratedKeyPairs() =>
        Load("curve").GetProperty("keyPairs").EnumerateArray().Where(k => k.GetProperty("ok").GetBoolean());

    public static IEnumerable<JsonElement> CheckedKeys() => Load("tunnel").GetProperty("keys").EnumerateArray();

    public static JsonElement CheckedKey(string name) => CheckedKeys().Single(k => k.GetProperty("name").GetString() == name);

    public static byte[] Hex(this JsonElement element, string property) =>
        Convert.FromHexString(element.GetProperty(property).GetString()!);

    private static JsonElement Load(string primitive)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder.Value, primitive + ".json")));
        return document.RootElement.Clone();
    }

    private static string FindFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "vectors", "cat");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("tests/vectors/cat was not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Hands out random bytes cat recorded, in order, in place of the system CSPRNG.</summary>
internal sealed class RecordedRandom(byte[] draws) : ISecureRandom
{
    private int _position;

    public int Remaining => draws.Length - _position;

    public void Fill(Span<byte> destination)
    {
        if (destination.Length > Remaining)
            throw new InvalidOperationException($"The vector recorded {draws.Length} random bytes; more were drawn.");
        draws.AsSpan(_position, destination.Length).CopyTo(destination);
        _position += destination.Length;
    }
}
