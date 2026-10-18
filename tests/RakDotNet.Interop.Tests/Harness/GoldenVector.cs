using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>A captured scenario, as described in docs/golden-vectors.md.</summary>
public sealed record GoldenVector(
    string Scenario,
    string Description,
    string Source,
    DateTimeOffset Captured,
    IReadOnlyDictionary<string, GoldenPeer> Peers,
    IReadOnlyList<GoldenDatagram> Datagrams);

/// <param name="PeerGuid">RakNetGUID.g; stored as the decimal string "guid".</param>
public sealed record GoldenPeer(string Role, ulong PeerGuid);

/// <param name="TimeMs">Milliseconds since the first captured datagram.</param>
/// <param name="From">A key of <see cref="GoldenVector.Peers"/>.</param>
/// <param name="To">A key of <see cref="GoldenVector.Peers"/>.</param>
public sealed record GoldenDatagram(int Index, long TimeMs, string From, string To, byte[] Bytes)
{
    public string Hex => Convert.ToHexStringLower(Bytes);
}

public static class GoldenVectorWriter
{
    public const string DefaultSource = "interop-peer, RakNet 1a169895a900c9fc4841c556e16514182b75faf8";

    /// <summary>
    /// Builds a vector from a single-client proxy recording: the client side is peer "a", the server is "b".
    /// </summary>
    public static GoldenVector FromProxy(
        string scenario,
        string description,
        DateTimeOffset captured,
        ulong clientGuid,
        ulong serverGuid,
        IEnumerable<ProxyDatagram> datagrams,
        string source = DefaultSource)
    {
        var list = datagrams.ToList();
        if (list.Select(d => d.Client).Distinct().Count() > 1)
        {
            throw new ArgumentException("A golden vector has one client peer; the recording has several client endpoints.", nameof(datagrams));
        }

        return new GoldenVector(
            scenario,
            description,
            source,
            captured,
            new Dictionary<string, GoldenPeer> { ["a"] = new("client", clientGuid), ["b"] = new("server", serverGuid) },
            list.Select((d, i) => new GoldenDatagram(
                i,
                (long)Math.Floor(d.Elapsed.TotalMilliseconds),
                d.Direction == ProxyDirection.AToB ? "a" : "b",
                d.Direction == ProxyDirection.AToB ? "b" : "a",
                d.Payload)).ToList());
    }

    /// <summary>
    /// The documented layout: two-space indentation, one peer and one datagram per line, so recaptures diff cleanly.
    /// </summary>
    public static string ToJson(GoldenVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"scenario\": ").Append(Quote(vector.Scenario)).Append(",\n");
        json.Append("  \"description\": ").Append(Quote(vector.Description)).Append(",\n");
        json.Append("  \"source\": ").Append(Quote(vector.Source)).Append(",\n");
        json.Append("  \"captured\": ").Append(Quote(vector.Captured.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append(",\n");
        json.Append("  \"peers\": {\n");
        json.AppendJoin(",\n", vector.Peers.Select(peer =>
            $"    {Quote(peer.Key)}: {{ \"role\": {Quote(peer.Value.Role)}, \"guid\": {Quote(peer.Value.PeerGuid.ToString(CultureInfo.InvariantCulture))} }}"));
        json.Append("\n  },\n");
        json.Append("  \"datagrams\": [");
        if (vector.Datagrams.Count > 0)
        {
            json.Append('\n');
            json.AppendJoin(",\n", vector.Datagrams.Select(d => string.Create(CultureInfo.InvariantCulture,
                $"    {{ \"index\": {d.Index}, \"timeMs\": {d.TimeMs}, \"from\": {Quote(d.From)}, \"to\": {Quote(d.To)}, \"hex\": {Quote(d.Hex)} }}")));
            json.Append("\n  ");
        }
        json.Append("]\n");
        json.Append("}\n");
        return json.ToString();
    }

    public static void WriteFile(string path, GoldenVector vector)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, ToJson(vector), new UTF8Encoding(false));
    }

    // Relaxed escaping keeps descriptions readable (no \u0027 for quotes); the output is a file, never HTML.
    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Quote(string value) => JsonSerializer.Serialize(value, QuoteOptions);
}

public static class GoldenVectorReader
{
    public static GoldenVector ReadFile(string path) => Parse(File.ReadAllText(path));

    /// <exception cref="InvalidDataException">The text is not a valid golden vector.</exception>
    public static GoldenVector Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("A golden vector is a JSON object.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Golden vector is not valid JSON: {e.Message}", e);
        }

        var peers = new Dictionary<string, GoldenPeer>();
        foreach (var (key, node) in Object(root, "peers", "peers"))
        {
            var peer = node as JsonObject ?? throw new InvalidDataException($"peers.{key} is not an object.");
            var guidText = String(peer, "guid", $"peers.{key}.guid");
            if (!ulong.TryParse(guidText, NumberStyles.None, CultureInfo.InvariantCulture, out var guid))
            {
                throw new InvalidDataException($"peers.{key}.guid '{guidText}' is not a decimal uint64.");
            }
            peers[key] = new GoldenPeer(String(peer, "role", $"peers.{key}.role"), guid);
        }

        var datagrams = new List<GoldenDatagram>();
        foreach (var node in Array(root, "datagrams"))
        {
            var where = $"datagrams[{datagrams.Count}]";
            var datagram = node as JsonObject ?? throw new InvalidDataException($"{where} is not an object.");
            var index = Integer(datagram, "index", where);
            if (index != datagrams.Count)
            {
                throw new InvalidDataException($"{where}.index is {index}; indices count up from 0.");
            }
            var from = PeerKey(datagram, "from", where, peers);
            var to = PeerKey(datagram, "to", where, peers);
            datagrams.Add(new GoldenDatagram((int)index, Integer(datagram, "timeMs", where), from, to, Hex(String(datagram, "hex", $"{where}.hex"), where)));
        }

        var capturedText = String(root, "captured", "captured");
        if (!DateTimeOffset.TryParse(capturedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var captured))
        {
            throw new InvalidDataException($"captured '{capturedText}' is not an ISO 8601 time.");
        }

        return new GoldenVector(
            String(root, "scenario", "scenario"),
            String(root, "description", "description"),
            String(root, "source", "source"),
            captured,
            peers,
            datagrams);
    }

    private static string PeerKey(JsonObject datagram, string field, string where, Dictionary<string, GoldenPeer> peers)
    {
        var key = String(datagram, field, $"{where}.{field}");
        return peers.ContainsKey(key) ? key : throw new InvalidDataException($"{where}.{field} '{key}' is not a key of peers.");
    }

    private static byte[] Hex(string hex, string where)
    {
        if (hex.Length % 2 != 0 || !hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException($"{where}.hex must be lowercase hex with no separators.");
        }
        return Convert.FromHexString(hex);
    }

    private static string String(JsonObject parent, string field, string where) =>
        parent[field] is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : throw new InvalidDataException($"{where} must be a string.");

    private static long Integer(JsonObject parent, string field, string where) =>
        parent[field] is JsonValue value && value.TryGetValue(out long number)
            ? number
            : throw new InvalidDataException($"{where}.{field} must be an integer.");

    private static JsonObject Object(JsonObject parent, string field, string where) =>
        parent[field] as JsonObject ?? throw new InvalidDataException($"{where} must be an object.");

    private static JsonArray Array(JsonObject parent, string field) =>
        parent[field] as JsonArray ?? throw new InvalidDataException($"{field} must be an array.");
}
