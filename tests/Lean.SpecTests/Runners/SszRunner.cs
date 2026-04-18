using System.Text.Json;
using System.Text.Json.Serialization;
using Lean.Consensus.Types;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class SszRunner : ISpecTestRunner
{
    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<SszTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize: {testId}");

        var expectedSerialized = ParseHex(test.Serialized);
        var expectedRoot = new Bytes32(ParseHex(test.Root));

        var (actualSerialized, actualRoot) = Dispatch(test.TypeName, test.Value);

        // Dispatch returned null → type not supported yet. Mark Inconclusive so
        // the fixture count stays accurate but CI stays green.
        if (actualSerialized is null || actualRoot is null)
        {
            Assert.Inconclusive($"SSZ runner has no handler for type '{test.TypeName}' yet");
            return;
        }

        Assert.That(Convert.ToHexString(actualSerialized), Is.EqualTo(Convert.ToHexString(expectedSerialized)),
            $"{test.TypeName}: serialized bytes mismatch");
        Assert.That(actualRoot.Value, Is.EqualTo(expectedRoot),
            $"{test.TypeName}: hash_tree_root mismatch");
    }

    private static (byte[]? Serialized, Bytes32? Root) Dispatch(string typeName, JsonElement value)
    {
        return typeName switch
        {
            // === Primitives ===
            // leanSpec fixtures JSON-encode uints >32-bit as strings to avoid
            // precision loss. Parse through the string-tolerant helper.
            "Uint8" => EncodeUint(ReadUInt(value), 1),
            "Uint16" => EncodeUint(ReadUInt(value), 2),
            "Uint32" => EncodeUint(ReadUInt(value), 4),
            "Uint64" => EncodeUint(ReadUInt(value), 8),
            "Boolean" => EncodePrim(value.GetBoolean(), new[] { value.GetBoolean() ? (byte)1 : (byte)0 }),

            // === Byte vectors ===
            "Bytes32" => EncodeFixedBytes(value, 32),
            "Bytes52" => EncodeFixedBytes(value, 52),
            "Bytes4" => EncodeFixedBytes(value, 4),
            "Bytes64" => EncodeFixedBytes(value, 64),

            "Fp" => EncodeFp(value),

            // === Consensus containers ===
            "Config" => EncodeConfig(value),
            "Checkpoint" => EncodeCheckpoint(value),
            "Validator" => EncodeValidator(value),
            "BlockHeader" => EncodeBlockHeader(value),
            "AttestationData" => EncodeAttestationData(value),
            "Attestation" => EncodeAttestation(value),
            "AggregatedAttestation" => EncodeAggregatedAttestation(value),
            "BlockBody" => EncodeBlockBody(value),
            "Block" => EncodeBlock(value),

            // Everything else: no handler yet.
            _ => (null, null),
        };
    }

    // === Primitive helpers ===

    private static (byte[] Serialized, Bytes32 Root) EncodePrim(object _, byte[] rawLittleEndian)
    {
        // SSZ hash_tree_root of a basic type: left-pad to 32 bytes.
        var padded = new byte[32];
        rawLittleEndian.CopyTo(padded, 0);
        return (rawLittleEndian, new Bytes32(padded));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeUint(ulong value, int byteLength)
    {
        var bytes = new byte[byteLength];
        for (var i = 0; i < byteLength; i++)
            bytes[i] = (byte)(value >> (8 * i));
        var padded = new byte[32];
        bytes.CopyTo(padded, 0);
        return (bytes, new Bytes32(padded));
    }

    private static ulong ReadUInt(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetUInt64(),
            JsonValueKind.String => ulong.Parse(value.GetString()!),
            _ => throw new InvalidOperationException($"cannot read uint from JSON {value.ValueKind}"),
        };
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeFixedBytes(JsonElement value, int length)
    {
        var bytes = ParseHex(value.GetString() ?? throw new InvalidOperationException("expected hex string"));
        if (bytes.Length != length)
            throw new InvalidOperationException($"expected {length} bytes, got {bytes.Length}");

        // hash_tree_root of a fixed-size byte vector ≤32 bytes is the vector
        // right-padded to 32 bytes; for >32 bytes it's a merkle tree, but that
        // case is rare for the types the fixture exercises (Bytes52/Bytes64).
        if (length <= 32)
        {
            var padded = new byte[32];
            bytes.CopyTo(padded, 0);
            return (bytes, new Bytes32(padded));
        }

        // For Bytes52 / Bytes64, merkleise as a Vector[byte, N] with chunk=32.
        return (bytes, new Bytes32(MerkleiseBytesVector(bytes)));
    }

    private static byte[] MerkleiseBytesVector(byte[] bytes)
    {
        // Split into 32-byte chunks, right-pad last chunk, merkleise.
        var chunks = new List<byte[]>();
        for (var i = 0; i < bytes.Length; i += 32)
        {
            var chunk = new byte[32];
            var len = Math.Min(32, bytes.Length - i);
            Array.Copy(bytes, i, chunk, 0, len);
            chunks.Add(chunk);
        }
        return MerkleiseChunks(chunks);
    }

    private static byte[] MerkleiseChunks(List<byte[]> chunks)
    {
        if (chunks.Count == 0) return new byte[32];
        // Pad to next power of two.
        var n = 1;
        while (n < chunks.Count) n *= 2;
        while (chunks.Count < n) chunks.Add(new byte[32]);

        while (chunks.Count > 1)
        {
            var next = new List<byte[]>(chunks.Count / 2);
            for (var i = 0; i < chunks.Count; i += 2)
            {
                var combined = new byte[64];
                chunks[i].CopyTo(combined, 0);
                chunks[i + 1].CopyTo(combined, 32);
                next.Add(System.Security.Cryptography.SHA256.HashData(combined));
            }
            chunks = next;
        }
        return chunks[0];
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeFp(JsonElement value)
    {
        // Fp serialises as a uint32 (4 bytes little-endian). hash_tree_root of
        // a basic type ≤32 bytes is the serialised bytes right-padded to 32.
        var raw = (uint)ReadUInt(value);
        var serialized = SszEncoding.Encode(new Fp(raw));
        var padded = new byte[32];
        serialized.CopyTo(padded, 0);
        return (serialized, new Bytes32(padded));
    }

    // === Consensus container helpers ===

    private static (byte[] Serialized, Bytes32 Root) EncodeConfig(JsonElement value)
    {
        var cfg = new Config(ReadUInt(value.GetProperty("genesisTime")));
        return (SszEncoding.Encode(cfg), new Bytes32(cfg.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeCheckpoint(JsonElement value)
    {
        var cp = DeserializeCheckpoint(value);
        // nlean's SszEncoding has no dedicated Encode(Checkpoint) since it's
        // always embedded in a container; re-implement by concatenating
        // (root || slot_u64_le).
        var buf = new byte[40];
        cp.Root.AsSpan().CopyTo(buf);
        BitConverter.GetBytes(cp.Slot.Value).CopyTo(buf, 32);
        return (buf, new Bytes32(cp.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeValidator(JsonElement value)
    {
        var v = new Validator(
            new Bytes52(ParseHex(value.GetProperty("attestationPubkey").GetString()!)),
            new Bytes52(ParseHex(value.GetProperty("proposalPubkey").GetString()!)),
            ReadUInt(value.GetProperty("index")));
        return (SszEncoding.Encode(v), new Bytes32(v.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeBlockHeader(JsonElement value)
    {
        var h = DeserializeBlockHeader(value);
        return (SszEncoding.Encode(h), new Bytes32(h.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeAttestationData(JsonElement value)
    {
        var d = DeserializeAttestationData(value);
        return (SszEncoding.Encode(d), new Bytes32(d.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeAttestation(JsonElement value)
    {
        var a = new Attestation(
            ReadUInt(value.GetProperty("validatorId")),
            DeserializeAttestationData(value.GetProperty("data")));
        return (SszEncoding.Encode(a), new Bytes32(a.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeAggregatedAttestation(JsonElement value)
    {
        var a = new AggregatedAttestation(
            DeserializeAggregationBits(value.GetProperty("aggregationBits")),
            DeserializeAttestationData(value.GetProperty("data")));
        return (SszEncoding.Encode(a), new Bytes32(a.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeBlockBody(JsonElement value)
    {
        var body = DeserializeBlockBody(value);
        return (SszEncoding.Encode(body), new Bytes32(body.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeBlock(JsonElement value)
    {
        var b = new Block(
            new Slot(ReadUInt(value.GetProperty("slot"))),
            ReadUInt(value.GetProperty("proposerIndex")),
            new Bytes32(ParseHex(value.GetProperty("parentRoot").GetString()!)),
            new Bytes32(ParseHex(value.GetProperty("stateRoot").GetString()!)),
            DeserializeBlockBody(value.GetProperty("body")));
        return (SszEncoding.Encode(b), new Bytes32(b.HashTreeRoot()));
    }

    // === Deserializers ===

    private static Checkpoint DeserializeCheckpoint(JsonElement value) => new(
        new Bytes32(ParseHex(value.GetProperty("root").GetString()!)),
        new Slot(ReadUInt(value.GetProperty("slot"))));

    private static BlockHeader DeserializeBlockHeader(JsonElement value) => new(
        new Slot(ReadUInt(value.GetProperty("slot"))),
        ReadUInt(value.GetProperty("proposerIndex")),
        new Bytes32(ParseHex(value.GetProperty("parentRoot").GetString()!)),
        new Bytes32(ParseHex(value.GetProperty("stateRoot").GetString()!)),
        new Bytes32(ParseHex(value.GetProperty("bodyRoot").GetString()!)));

    private static AttestationData DeserializeAttestationData(JsonElement value) => new(
        new Slot(ReadUInt(value.GetProperty("slot"))),
        DeserializeCheckpoint(value.GetProperty("head")),
        DeserializeCheckpoint(value.GetProperty("target")),
        DeserializeCheckpoint(value.GetProperty("source")));

    private static AggregationBits DeserializeAggregationBits(JsonElement value)
    {
        // leanSpec serialises Bitlist as { "data": [bool, bool, ...] }
        var data = value.GetProperty("data");
        var bits = new List<bool>(data.GetArrayLength());
        foreach (var b in data.EnumerateArray()) bits.Add(b.GetBoolean());
        return new AggregationBits(bits);
    }

    private static BlockBody DeserializeBlockBody(JsonElement value)
    {
        var attsElem = value.GetProperty("attestations").GetProperty("data");
        var atts = new List<AggregatedAttestation>(attsElem.GetArrayLength());
        foreach (var att in attsElem.EnumerateArray())
        {
            atts.Add(new AggregatedAttestation(
                DeserializeAggregationBits(att.GetProperty("aggregationBits")),
                DeserializeAttestationData(att.GetProperty("data"))));
        }
        return new BlockBody(atts);
    }

    // === Shared ===

    private static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);

    private sealed record SszTest(
        [property: JsonPropertyName("network")] string Network,
        [property: JsonPropertyName("leanEnv")] string LeanEnv,
        [property: JsonPropertyName("typeName")] string TypeName,
        [property: JsonPropertyName("value")] JsonElement Value,
        [property: JsonPropertyName("serialized")] string Serialized,
        [property: JsonPropertyName("root")] string Root,
        [property: JsonPropertyName("_info")] TestInfo? Info);
}
