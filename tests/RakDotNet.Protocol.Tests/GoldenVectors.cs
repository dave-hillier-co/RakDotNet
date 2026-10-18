using System.Globalization;
using System.Text.Json;

namespace RakDotNet.Protocol.Tests;

/// <summary>One captured datagram of a golden vector (docs/golden-vectors.md).</summary>
internal sealed record CapturedDatagram(int Index, long TimeMs, string From, string To, byte[] Bytes);

/// <summary>Loads the golden vectors in tests/vectors, copied next to the test assembly.</summary>
internal static class GoldenVectors
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "vectors");

    public static IReadOnlyList<CapturedDatagram> Load(string scenario)
    {
        using var document = Open(scenario);
        return document
            .RootElement.GetProperty("datagrams")
            .EnumerateArray()
            .Select(d => new CapturedDatagram(
                d.GetProperty("index").GetInt32(),
                d.GetProperty("timeMs").GetInt64(),
                d.GetProperty("from").GetString()!,
                d.GetProperty("to").GetString()!,
                Convert.FromHexString(d.GetProperty("hex").GetString()!)
            ))
            .ToList();
    }

    /// <summary>The GUIDs of peer "a" (the client) and peer "b" (the server).</summary>
    public static (ulong Client, ulong Server) PeerGuids(string scenario)
    {
        using var document = Open(scenario);
        var peers = document.RootElement.GetProperty("peers");
        return (Guid(peers, "a"), Guid(peers, "b"));
    }

    private static ulong Guid(JsonElement peers, string key) =>
        ulong.Parse(peers.GetProperty(key).GetProperty("guid").GetString()!, CultureInfo.InvariantCulture);

    private static JsonDocument Open(string scenario) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, scenario + ".json")));
}
