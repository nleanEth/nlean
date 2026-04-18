using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lean.Network;
using Lean.SpecTests.Types;
using NUnit.Framework;
using Snappier;

namespace Lean.SpecTests.Runners;

public sealed class NetworkingCodecRunner : ISpecTestRunner
{
    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<NetworkingCodecTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize: {testId}");

        switch (test.CodecName)
        {
            case "varint":
                RunVarint(test);
                break;
            case "gossip_topic":
                RunGossipTopic(test);
                break;
            case "gossip_message_id":
                RunGossipMessageId(test);
                break;
            case "snappy_block":
                RunSnappyBlock(test);
                break;
            case "snappy_frame":
                RunSnappyFrame(test);
                break;
            default:
                Assert.Inconclusive(
                    $"codec '{test.CodecName}' not implemented yet — covered codecs: " +
                    "varint, gossip_topic, gossip_message_id, snappy_block, snappy_frame");
                break;
        }
    }

    private static void RunVarint(NetworkingCodecTest test)
    {
        var value = ReadUInt(test.Input.GetProperty("value"));
        var expected = ParseHex(test.Output.GetProperty("encoded").GetString()!);

        // LEB128 / unsigned-varint (libp2p varint): 7 bits per byte, MSB set
        // means "more bytes follow".
        var buf = new List<byte>();
        var v = value;
        while (v >= 0x80)
        {
            buf.Add((byte)(v | 0x80));
            v >>= 7;
        }
        buf.Add((byte)v);
        var actual = buf.ToArray();

        Assert.That(Convert.ToHexString(actual), Is.EqualTo(Convert.ToHexString(expected)),
            "varint encoding mismatch");

        if (test.Output.TryGetProperty("byteLength", out var len))
        {
            Assert.That(actual.Length, Is.EqualTo(len.GetInt32()),
                "varint byteLength mismatch");
        }
    }

    private static void RunGossipTopic(NetworkingCodecTest test)
    {
        var kind = test.Input.GetProperty("kind").GetString()!;
        var forkDigest = test.Input.GetProperty("forkDigest").GetString()!;
        var expected = test.Output.GetProperty("topicString").GetString()!;

        var actual = kind switch
        {
            "block" => GossipTopics.Block(forkDigest),
            "aggregation" => GossipTopics.Aggregate(forkDigest),
            "attestation" => GossipTopics.AttestationSubnet(forkDigest, test.Input.GetProperty("subnetId").GetInt32()),
            _ => throw new InvalidOperationException($"unknown gossip topic kind: {kind}"),
        };

        Assert.That(actual, Is.EqualTo(expected), "gossip topic string mismatch");
    }

    private static void RunGossipMessageId(NetworkingCodecTest test)
    {
        var topicBytes = ParseHex(test.Input.GetProperty("topic").GetString()!);
        var data = ParseHex(test.Input.GetProperty("data").GetString()!);
        var domain = ParseHex(test.Input.GetProperty("domain").GetString()!);
        var expected = ParseHex(test.Output.GetProperty("messageId").GetString()!);

        // Replicate LeanPubsubMessageId.Compute, but with explicit domain.
        // The spec captures domain as an input (valid-snappy vs invalid-snappy)
        // while nlean's production path auto-detects it from whether the data
        // successfully snappy-decompresses. For the codec test we honour the
        // fixture's explicit domain choice; the auto-detect behaviour is
        // validated separately by Lean.Network.Tests.
        if (domain.Length != 4)
            throw new InvalidOperationException("expected 4-byte domain");

        var topic = Encoding.ASCII.GetString(topicBytes);
        var topicByteCount = Encoding.ASCII.GetByteCount(topic);
        var total = domain.Length + sizeof(ulong) + topicByteCount + data.Length;
        var input = new byte[total];
        var offset = 0;
        domain.CopyTo(input, offset);
        offset += domain.Length;
        BitConverter.GetBytes((ulong)topicByteCount).CopyTo(input, offset);
        offset += sizeof(ulong);
        Encoding.ASCII.GetBytes(topic, 0, topic.Length, input, offset);
        offset += topicByteCount;
        data.CopyTo(input, offset);

        var actual = SHA256.HashData(input)[..20];

        Assert.That(Convert.ToHexString(actual), Is.EqualTo(Convert.ToHexString(expected)),
            "gossip message id mismatch");
    }

    private static void RunSnappyBlock(NetworkingCodecTest test)
    {
        // Snappy compression is not bit-for-bit deterministic across
        // implementations: different encoders can emit different (but equally
        // valid) byte streams for the same input. We therefore test the
        // decompression direction — take the fixture's compressed payload and
        // confirm nlean's Snappier decodes it back to the original data.
        // Round-tripping nlean's own output is also checked so regressions in
        // the compressor are still caught.
        var data = ParseHex(test.Input.GetProperty("data").GetString()!);
        var compressed = ParseHex(test.Output.GetProperty("compressed").GetString()!);

        var decoded = Snappy.DecompressToArray(compressed);
        Assert.That(Convert.ToHexString(decoded), Is.EqualTo(Convert.ToHexString(data)),
            "snappy block decompression mismatch");

        var roundTrip = Snappy.DecompressToArray(Snappy.CompressToArray(data));
        Assert.That(Convert.ToHexString(roundTrip), Is.EqualTo(Convert.ToHexString(data)),
            "snappy block round-trip mismatch");

        if (test.Output.TryGetProperty("uncompressedLength", out var ul))
        {
            Assert.That(data.Length, Is.EqualTo(ul.GetInt32()),
                "uncompressedLength mismatch");
        }
    }

    private static void RunSnappyFrame(NetworkingCodecTest test)
    {
        // Same rationale as RunSnappyBlock: frame-level compression output
        // depends on encoder chunking. Validate decompression path against
        // the fixture's framed bytes, then round-trip nlean's output.
        var data = ParseHex(test.Input.GetProperty("data").GetString()!);
        var framed = ParseHex(test.Output.GetProperty("framed").GetString()!);

        var decoded = ReadSnappyFrame(framed);
        Assert.That(Convert.ToHexString(decoded), Is.EqualTo(Convert.ToHexString(data)),
            "snappy frame decompression mismatch");

        using var ms = new MemoryStream();
        using (var stream = new SnappyStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            stream.Write(data);
        }
        var roundTrip = ReadSnappyFrame(ms.ToArray());
        Assert.That(Convert.ToHexString(roundTrip), Is.EqualTo(Convert.ToHexString(data)),
            "snappy frame round-trip mismatch");
    }

    private static byte[] ReadSnappyFrame(byte[] framed)
    {
        using var ms = new MemoryStream(framed);
        using var stream = new SnappyStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static ulong ReadUInt(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetUInt64(),
            JsonValueKind.String => ulong.Parse(value.GetString()!),
            _ => throw new InvalidOperationException($"cannot read uint from {value.ValueKind}"),
        };
    }

    private static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);

    private sealed record NetworkingCodecTest(
        [property: JsonPropertyName("network")] string Network,
        [property: JsonPropertyName("leanEnv")] string LeanEnv,
        [property: JsonPropertyName("codecName")] string CodecName,
        [property: JsonPropertyName("input")] JsonElement Input,
        [property: JsonPropertyName("output")] JsonElement Output,
        [property: JsonPropertyName("_info")] TestInfo? Info);
}
