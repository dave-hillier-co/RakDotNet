using System.Globalization;
using System.Text.Json;

namespace RakDotNet.Protocol.Tests.Security;

/// <summary>
/// Loads the known-answer vectors tools/cat-vectors computes with RakNet's DependentExtensions/cat
/// (tests/vectors/cat, docs/golden-vectors.md). They sit in a subfolder so the scenario vector tests, which
/// enumerate tests/vectors/*.json, do not see them; they are read from the source tree.
/// </summary>
internal static class CatVectors
{
    private static readonly Lazy<string> Folder = new(FindFolder);

    public static JsonElement Load(string primitive)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder.Value, primitive + ".json")));
        return document.RootElement.Clone();
    }

    public static byte[] Hex(this JsonElement element, string property) =>
        Convert.FromHexString(element.GetProperty(property).GetString()!);

    public static ulong UInt64(this JsonElement element, string property) =>
        ulong.Parse(element.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);

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
