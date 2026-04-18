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
            case "xor_distance":
                RunXorDistance(test);
                break;
            case "log2_distance":
                RunLog2Distance(test);
                break;
            default:
                Assert.Inconclusive(
                    $"codec '{test.CodecName}' not implemented yet — covered codecs: " +
                    "varint, gossip_topic, gossip_message_id, snappy_block, snappy_frame, " +
                    "xor_distance, log2_distance");
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

    private static void RunXorDistance(NetworkingCodecTest test)
    {
        var a = ParseHex(test.Input.GetProperty("nodeA").GetString()!);
        var b = ParseHex(test.Input.GetProperty("nodeB").GetString()!);

        if (a.Length != b.Length)
            throw new InvalidOperationException("xor_distance operands must be same length");

        var actual = new byte[a.Length];
        for (var i = 0; i < a.Length; i++) actual[i] = (byte)(a[i] ^ b[i]);

        // The fixture serialises `distance` as compact big-endian hex (leading
        // zero bytes trimmed) and may emit odd-length strings like "0x3" for
        // single-nibble values, so compare after normalising both sides to
        // shortest-big-endian form.
        var expected = StripLeadingZeroBytes(ParseFlexHex(test.Output.GetProperty("distance").GetString()!));
        var actualNormalised = StripLeadingZeroBytes(actual);

        Assert.That(Convert.ToHexString(actualNormalised), Is.EqualTo(Convert.ToHexString(expected)),
            "xor_distance mismatch");
    }

    private static byte[] ParseFlexHex(string hex)
    {
        var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if ((s.Length & 1) == 1) s = "0" + s;
        return s.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(s);
    }

    private static byte[] StripLeadingZeroBytes(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length - 1 && bytes[i] == 0) i++;
        return bytes[i..];
    }

    private static void RunLog2Distance(NetworkingCodecTest test)
    {
        var a = ParseHex(test.Input.GetProperty("nodeA").GetString()!);
        var b = ParseHex(test.Input.GetProperty("nodeB").GetString()!);
        var expected = test.Output.GetProperty("distance").GetInt32();

        if (a.Length != b.Length)
            throw new InvalidOperationException("log2_distance operands must be same length");

        // log2_distance is the position of the most significant differing bit,
        // treating a and b as big-endian bit strings. 0 means identical; for
        // nodes differing only in the lowest bit, distance = 1.
        var actual = 0;
        for (var byteIdx = 0; byteIdx < a.Length; byteIdx++)
        {
            var diff = a[byteIdx] ^ b[byteIdx];
            if (diff == 0) continue;
            // Find position of the highest set bit in `diff`.
            var bitsRemaining = (a.Length - byteIdx) * 8;
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((diff & (1 << bit)) != 0)
                {
                    actual = bitsRemaining - (7 - bit);
                    break;
                }
            }
            break;
        }

        Assert.That(actual, Is.EqualTo(expected), "log2_distance mismatch");
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
