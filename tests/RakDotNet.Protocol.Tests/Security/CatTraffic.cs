using System.Text.Json;
using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

/// <summary>
/// Replays a traffic script tools/cat-vectors recorded between a keyed client and server: proofs, encrypted
/// datagrams and decryptions of what the other side sent or of raw (tampered) bytes, each with cat's outcome.
/// </summary>
internal static class CatTraffic
{
    public static void Replay(JsonElement ops, AuthenticatedEncryption clientAuth, AuthenticatedEncryption serverAuth)
    {
        var sent = new Dictionary<string, List<byte[]>> { ["client"] = [], ["server"] = [] };
        var step = 0;
        foreach (var op in ops.EnumerateArray())
        {
            step++;
            var by = op.GetProperty("by").GetString()!;
            var self = by == "client" ? clientAuth : serverAuth;
            var other = by == "client" ? "server" : "client";
            switch (op.GetProperty("op").GetString())
            {
                case "proof":
                {
                    var proof = new byte[op.GetProperty("bytes").GetInt32()];
                    self.GenerateProof(proof);
                    AssertHex(op.Hex("proof"), proof);
                    var peer = by == "client" ? serverAuth : clientAuth;
                    Assert.Equal(op.GetProperty("validated").GetBoolean(), peer.ValidateProof(proof));
                    proof[^1] ^= 1;
                    Assert.Equal(op.GetProperty("tamperedValidated").GetBoolean(), peer.ValidateProof(proof));
                    break;
                }
                case "encrypt":
                {
                    var cipher = Encrypt(self, op.Hex("plain"));
                    AssertHex(op.Hex("cipher"), cipher, $"step {step}");
                    sent[by].Add(cipher);
                    break;
                }
                case "encryptMany":
                {
                    var bytes = op.GetProperty("bytes").GetInt32();
                    for (var i = 0; i < op.GetProperty("count").GetInt32(); i++)
                        sent[by].Add(Encrypt(self, Enumerable.Range(0, bytes).Select(j => (byte)(i + j)).ToArray()));
                    break;
                }
                case "decrypt":
                    AssertDecrypt(self, sent[other][op.GetProperty("index").GetInt32()], op, step);
                    break;
                case "decryptRaw":
                    AssertDecrypt(self, op.Hex("cipher"), op, step);
                    break;
                default:
                    throw new InvalidOperationException(op.GetProperty("op").GetString());
            }
        }
    }

    private static byte[] Encrypt(AuthenticatedEncryption auth, byte[] plain)
    {
        var buffer = new byte[plain.Length + AuthenticatedEncryption.OverheadBytes];
        plain.CopyTo(buffer, 0);
        Assert.True(auth.Encrypt(buffer, plain.Length, out var length));
        Assert.Equal(buffer.Length, length);
        return buffer;
    }

    private static void AssertDecrypt(AuthenticatedEncryption auth, byte[] cipher, JsonElement op, int step)
    {
        var buffer = (byte[])cipher.Clone();
        var ok = auth.Decrypt(buffer, out var length);
        Assert.True(op.GetProperty("ok").GetBoolean() == ok, $"step {step}: expected ok={op.GetProperty("ok").GetBoolean()}");
        if (ok)
            AssertHex(op.Hex("plain"), buffer[..length], $"step {step}");
    }

    public static void AssertHex(byte[] expected, byte[] actual, string? because = null) =>
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            $"{because}\nexpected {Convert.ToHexStringLower(expected)}\nactual   {Convert.ToHexStringLower(actual)}");
}
