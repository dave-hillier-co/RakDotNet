using System.Net;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed class GoldenVectorTests
{
    private static GoldenVector Sample() => new(
        "unconnected-ping",
        "Native client pings a native server with an offline ping response of 'hello'.",
        "interop-peer, RakNet 1a169895a900c9fc4841c556e16514182b75faf8",
        new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.Zero),
        new Dictionary<string, GoldenPeer>
        {
            ["a"] = new("client", 1520003954185392640UL),
            ["b"] = new("server", 18446744073709551615UL),
        },
        [
            new GoldenDatagram(0, 0, "a", "b", [0x01, 0xAB]),
            new GoldenDatagram(1, 1, "b", "a", [0x1c]),
        ]);

    [Fact]
    public void Writes_the_documented_format()
    {
        var json = GoldenVectorWriter.ToJson(Sample());

        var expected = """
            {
              "scenario": "unconnected-ping",
              "description": "Native client pings a native server with an offline ping response of 'hello'.",
              "source": "interop-peer, RakNet 1a169895a900c9fc4841c556e16514182b75faf8",
              "captured": "2026-10-18T19:00:00Z",
              "peers": {
                "a": { "role": "client", "guid": "1520003954185392640" },
                "b": { "role": "server", "guid": "18446744073709551615" }
              },
              "datagrams": [
                { "index": 0, "timeMs": 0, "from": "a", "to": "b", "hex": "01ab" },
                { "index": 1, "timeMs": 1, "from": "b", "to": "a", "hex": "1c" }
              ]
            }

            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, json);
    }

    [Fact]
    public void Written_vectors_read_back_unchanged()
    {
        var original = Sample();

        var read = GoldenVectorReader.Parse(GoldenVectorWriter.ToJson(original));

        Assert.Equal(original.Scenario, read.Scenario);
        Assert.Equal(original.Description, read.Description);
        Assert.Equal(original.Source, read.Source);
        Assert.Equal(original.Captured, read.Captured);
        Assert.Equal(original.Peers, read.Peers);
        Assert.Equal(original.Datagrams.Count, read.Datagrams.Count);
        for (var i = 0; i < original.Datagrams.Count; i++)
        {
            Assert.Equal(original.Datagrams[i].From, read.Datagrams[i].From);
            Assert.Equal(original.Datagrams[i].To, read.Datagrams[i].To);
            Assert.Equal(original.Datagrams[i].TimeMs, read.Datagrams[i].TimeMs);
            Assert.Equal(original.Datagrams[i].Bytes, read.Datagrams[i].Bytes);
        }
    }

    [Fact]
    public void Text_in_strings_is_json_escaped()
    {
        var vector = Sample() with { Description = "quote \" and newline \n" };

        var read = GoldenVectorReader.Parse(GoldenVectorWriter.ToJson(vector));

        Assert.Equal("quote \" and newline \n", read.Description);
    }

    [Fact]
    public void Reads_files_and_writes_them()
    {
        var path = Path.Combine(Path.GetTempPath(), $"golden-{Guid.NewGuid():N}.json");
        try
        {
            GoldenVectorWriter.WriteFile(path, Sample());

            Assert.Equal("unconnected-ping", GoldenVectorReader.ReadFile(path).Scenario);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Proxy_recordings_map_client_to_a_and_server_to_b_with_whole_milliseconds()
    {
        var client = new IPEndPoint(IPAddress.Loopback, 5000);
        var recorded = new[]
        {
            new ProxyDatagram(0, TimeSpan.Zero, ProxyDirection.AToB, client, [0x01], default),
            new ProxyDatagram(1, TimeSpan.FromMilliseconds(2.9), ProxyDirection.BToA, client, [0x1c, 0x00], default),
        };

        var vector = GoldenVectorWriter.FromProxy("s", "d", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), clientGuid: 1, serverGuid: 2, recorded);

        Assert.Equal(GoldenVectorWriter.DefaultSource, vector.Source);
        Assert.Equal(new GoldenPeer("client", 1), vector.Peers["a"]);
        Assert.Equal(new GoldenPeer("server", 2), vector.Peers["b"]);
        Assert.Collection(
            vector.Datagrams,
            d => Assert.Equal((0, 0L, "a", "b", "01"), (d.Index, d.TimeMs, d.From, d.To, d.Hex)),
            d => Assert.Equal((1, 2L, "b", "a", "1c00"), (d.Index, d.TimeMs, d.From, d.To, d.Hex)));
    }

    [Fact]
    public void Proxy_recordings_with_several_clients_are_rejected()
    {
        var recorded = new[]
        {
            new ProxyDatagram(0, TimeSpan.Zero, ProxyDirection.AToB, new IPEndPoint(IPAddress.Loopback, 5000), [0x01], default),
            new ProxyDatagram(1, TimeSpan.Zero, ProxyDirection.AToB, new IPEndPoint(IPAddress.Loopback, 5001), [0x01], default),
        };

        Assert.Throws<ArgumentException>(() => GoldenVectorWriter.FromProxy("s", "d", DateTimeOffset.UnixEpoch, 1, 2, recorded));
    }

    public static TheoryData<string, string> Corruptions => new()
    {
        { "unknown peer key", """{"op":"set","path":"datagrams.0.from","value":"c"}""" },
        { "uppercase hex", """{"op":"set","path":"datagrams.0.hex","value":"01AB"}""" },
        { "odd length hex", """{"op":"set","path":"datagrams.0.hex","value":"01a"}""" },
        { "index out of sequence", """{"op":"set","path":"datagrams.1.index","value":5}""" },
        { "numeric guid", """{"op":"set","path":"peers.a.guid","value":12}""" },
        { "missing scenario", """{"op":"remove","path":"scenario"}""" },
        { "missing datagrams", """{"op":"remove","path":"datagrams"}""" },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Malformed_vectors_are_rejected_with_a_reason(string reason, string corruption)
    {
        var root = JsonNode.Parse(GoldenVectorWriter.ToJson(Sample()))!;
        Apply(root, JsonNode.Parse(corruption)!);

        var error = Assert.Throws<InvalidDataException>(() => GoldenVectorReader.Parse(root.ToJsonString()));

        Assert.False(string.IsNullOrWhiteSpace(error.Message), reason);
    }

    private static void Apply(JsonNode root, JsonNode corruption)
    {
        var parts = ((string)corruption["path"]!).Split('.');
        var parent = root;
        foreach (var part in parts[..^1])
        {
            parent = int.TryParse(part, out var i) ? parent[i]! : parent[part]!;
        }

        if ((string?)corruption["op"] == "remove")
        {
            parent.AsObject().Remove(parts[^1]);
        }
        else if (int.TryParse(parts[^1], out var index))
        {
            parent[index] = corruption["value"]!.DeepClone();
        }
        else
        {
            parent[parts[^1]] = corruption["value"]!.DeepClone();
        }
    }
}
