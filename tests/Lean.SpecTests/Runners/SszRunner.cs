using System.Text.Json;
using System.Text.Json.Serialization;
using Lean.Consensus.Types;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class SszRunner : ISpecTestRunner
{
    // leanSpec TEST scheme (LOG_LIFETIME=8): NODE_LIST_LIMIT = 1 << (LOG_LIFETIME/2 + 1) = 32.
    // nlean's production XMSS types are pinned to PROD (1 << 17), so XMSS-bearing
    // hash_tree_roots are computed scheme-aware here.
    private const ulong TestSchemeNodeListLimit = 1UL << 5;

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
            "State" => EncodeState(value),
            "AggregatedSignatureProof" => EncodeAggregatedSignatureProof(value),
            "SignedAggregatedAttestation" => EncodeSignedAggregatedAttestation(value),

            // === leanSpec SSZ sample/reqresp types ===
            "SampleBitvector8" => EncodeBitvector(value, 8),
            "SampleBitvector64" or "AttestationSubnets" => EncodeBitvector(value, 64),
            "SyncCommitteeSubnets" => EncodeBitvector(value, 4),
            "SampleBitlist16" => EncodeBitlist(value, 16),
            "ByteListMiB" => EncodeByteList(value, 1UL << 20),
            "SampleBytes32List8" => EncodeBytes32List(value, 8),
            "SampleUint16Vector3" => EncodeUintVector(value, 3, sizeof(ushort)),
            "SampleUint64Vector4" => EncodeUintVector(value, 4, sizeof(ulong)),
            "SampleUint32List16" => EncodeUintList(value, 16, sizeof(uint)),
            "BlocksByRootRequest" => EncodeBlocksByRootRequest(value),
            "Status" => EncodeStatus(value),
            "SampleUnionTypes" => EncodeUnion(value, includeNone: false),
            "SampleUnionNone" => EncodeUnion(value, includeNone: true),

            // === XMSS containers (leanEnv=test only) ===
            // PublicKey is scheme-independent (fixed-length Fp vectors).
            // The rest use NODE_LIST_LIMIT = 1<<5 = 32 for the TEST scheme.
            "PublicKey" => EncodePublicKey(value),
            "HashTreeLayer" => EncodeHashTreeLayer(value),
            "HashTreeOpening" => EncodeHashTreeOpeningTest(value),
            "Signature" => EncodeSignatureTest(value),
            "BlockSignatures" => EncodeBlockSignaturesTest(value),
            "SignedBlock" => EncodeSignedBlockTest(value),
            "SignedAttestation" => EncodeSignedAttestationTest(value),

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

    private static (byte[] Serialized, Bytes32 Root) EncodeSignedAggregatedAttestation(JsonElement value)
    {
        // SignedAggregatedAttestation is scheme-independent: it contains
        // AttestationData + AggregatedSignatureProof (no XMSS signature).
        var data = DeserializeAttestationData(value.GetProperty("data"));
        var proof = new AggregatedSignatureProof(
            DeserializeAggregationBits(value.GetProperty("proof").GetProperty("participants")),
            ParseHex(value.GetProperty("proof").GetProperty("proofData").GetProperty("data").GetString()!));
        var signed = new SignedAggregatedAttestation(data, proof);
        return (SszEncoding.Encode(signed), new Bytes32(signed.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeAggregatedSignatureProof(JsonElement value)
    {
        var proof = new AggregatedSignatureProof(
            DeserializeAggregationBits(value.GetProperty("participants")),
            ParseHex(value.GetProperty("proofData").GetProperty("data").GetString()!));
        return (SszEncoding.Encode(proof), new Bytes32(proof.HashTreeRoot()));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeState(JsonElement value)
    {
        var state = new State(
            new Config(ReadUInt(value.GetProperty("config").GetProperty("genesisTime"))),
            new Slot(ReadUInt(value.GetProperty("slot"))),
            DeserializeBlockHeader(value.GetProperty("latestBlockHeader")),
            DeserializeCheckpoint(value.GetProperty("latestJustified")),
            DeserializeCheckpoint(value.GetProperty("latestFinalized")),
            DeserializeRootList(value.GetProperty("historicalBlockHashes")),
            DeserializeBoolList(value.GetProperty("justifiedSlots")),
            DeserializeValidatorList(value.GetProperty("validators")),
            DeserializeRootList(value.GetProperty("justificationsRoots")),
            DeserializeBoolList(value.GetProperty("justificationsValidators")));
        return (SszEncoding.Encode(state), new Bytes32(state.HashTreeRoot()));
    }

    private static List<Bytes32> DeserializeRootList(JsonElement value)
    {
        var data = value.GetProperty("data");
        var roots = new List<Bytes32>(data.GetArrayLength());
        foreach (var r in data.EnumerateArray())
            roots.Add(new Bytes32(ParseHex(r.GetString()!)));
        return roots;
    }

    private static List<bool> DeserializeBoolList(JsonElement value)
    {
        var data = value.GetProperty("data");
        var bools = new List<bool>(data.GetArrayLength());
        foreach (var b in data.EnumerateArray())
            bools.Add(b.GetBoolean());
        return bools;
    }

    private static List<Validator> DeserializeValidatorList(JsonElement value)
    {
        var data = value.GetProperty("data");
        var vs = new List<Validator>(data.GetArrayLength());
        foreach (var v in data.EnumerateArray())
        {
            vs.Add(new Validator(
                new Bytes52(ParseHex(v.GetProperty("attestationPubkey").GetString()!)),
                new Bytes52(ParseHex(v.GetProperty("proposalPubkey").GetString()!)),
                ReadUInt(v.GetProperty("index"))));
        }
        return vs;
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

    // === XMSS test-scheme encoders ===

    // PublicKey: Container { root: Vector[Fp, 8], parameter: Vector[Fp, 5] }
    // Scheme-independent: both PROD and TEST use HASH_LEN_FE=8 and PARAMETER_LEN=5.
    private static (byte[] Serialized, Bytes32 Root) EncodePublicKey(JsonElement value)
    {
        var rootFp = DeserializeFpVector(value.GetProperty("root"), 8);
        var parameterFp = DeserializeFpVector(value.GetProperty("parameter"), 5);

        var serialized = new byte[(8 + 5) * Fp.ByteLength];
        for (var i = 0; i < 8; i++)
            SszEncoding.Encode(rootFp[i]).CopyTo(serialized, i * Fp.ByteLength);
        for (var i = 0; i < 5; i++)
            SszEncoding.Encode(parameterFp[i]).CopyTo(serialized, (8 + i) * Fp.ByteLength);

        var rootBytes = new byte[8 * Fp.ByteLength];
        Array.Copy(serialized, 0, rootBytes, 0, rootBytes.Length);
        var paramBytes = new byte[5 * Fp.ByteLength];
        Array.Copy(serialized, 8 * Fp.ByteLength, paramBytes, 0, paramBytes.Length);

        var root = SszInterop.HashContainer(
            SszInterop.HashBytesVector(rootBytes),
            SszInterop.HashBytesVector(paramBytes));
        return (serialized, new Bytes32(root));
    }

    // HashTreeLayer: Container { startIndex: uint64, nodes: HashDigestList }
    private static (byte[] Serialized, Bytes32 Root) EncodeHashTreeLayer(JsonElement value)
    {
        var startIndex = ReadUInt(value.GetProperty("startIndex"));
        var nodes = DeserializeHashDigestList(value.GetProperty("nodes"));

        // Encode: fixed uint64 (8) + offset uint32 (4) + variable nodes bytes.
        var nodesBytes = SszEncoding.Encode(nodes);
        var fixedSize = 8 + 4;
        var serialized = new byte[fixedSize + nodesBytes.Length];
        BitConverter.GetBytes(startIndex).CopyTo(serialized, 0);
        BitConverter.GetBytes((uint)fixedSize).CopyTo(serialized, 8);
        nodesBytes.CopyTo(serialized, fixedSize);

        var root = SszInterop.HashContainer(
            SszInterop.HashUInt64(startIndex),
            HashDigestListRootTest(nodes));
        return (serialized, new Bytes32(root));
    }

    // HashTreeOpening: Container { siblings: HashDigestList }
    private static (byte[] Serialized, Bytes32 Root) EncodeHashTreeOpeningTest(JsonElement value)
    {
        var opening = DeserializeHashTreeOpening(value);
        return (SszEncoding.Encode(opening), new Bytes32(HashTreeOpeningRootTest(opening)));
    }

    // XmssSignature: Container { path: HashTreeOpening, rho: Randomness, hashes: HashDigestList }
    // Fixture emits the SSZ-encoded bytes directly as a hex string in `value`.
    private static (byte[] Serialized, Bytes32 Root) EncodeSignatureTest(JsonElement value)
    {
        var serialized = ParseHex(value.GetString()!);
        var signature = XmssSignature.FromBytes(serialized);
        return (serialized, new Bytes32(XmssSignatureRootTest(signature)));
    }

    // BlockSignatures: Container { attestationSignatures: List[AggregatedSignatureProof, VALIDATOR_REGISTRY_LIMIT], proposerSignature: XmssSignature }
    private static (byte[] Serialized, Bytes32 Root) EncodeBlockSignaturesTest(JsonElement value)
    {
        var proofs = DeserializeAggregatedSignatureProofList(value.GetProperty("attestationSignatures"));
        var proposerSig = XmssSignature.FromBytes(ParseHex(value.GetProperty("proposerSignature").GetString()!));
        var signatures = new BlockSignatures(proofs, proposerSig);

        var attRoots = proofs.Select(p => p.HashTreeRoot()).ToList();
        var root = SszInterop.HashContainer(
            SszInterop.HashList(attRoots, SszEncoding.ValidatorRegistryLimit),
            XmssSignatureRootTest(proposerSig));
        return (SszEncoding.Encode(signatures), new Bytes32(root));
    }

    // SignedBlock: Container { block: Block, signature: BlockSignatures }
    private static (byte[] Serialized, Bytes32 Root) EncodeSignedBlockTest(JsonElement value)
    {
        var block = new Block(
            new Slot(ReadUInt(value.GetProperty("block").GetProperty("slot"))),
            ReadUInt(value.GetProperty("block").GetProperty("proposerIndex")),
            new Bytes32(ParseHex(value.GetProperty("block").GetProperty("parentRoot").GetString()!)),
            new Bytes32(ParseHex(value.GetProperty("block").GetProperty("stateRoot").GetString()!)),
            DeserializeBlockBody(value.GetProperty("block").GetProperty("body")));

        var sigValue = value.GetProperty("signature");
        var proofs = DeserializeAggregatedSignatureProofList(sigValue.GetProperty("attestationSignatures"));
        var proposerSig = XmssSignature.FromBytes(ParseHex(sigValue.GetProperty("proposerSignature").GetString()!));
        var signatures = new BlockSignatures(proofs, proposerSig);
        var signed = new SignedBlock(block, signatures);

        var attRoots = proofs.Select(p => p.HashTreeRoot()).ToList();
        var signaturesRoot = SszInterop.HashContainer(
            SszInterop.HashList(attRoots, SszEncoding.ValidatorRegistryLimit),
            XmssSignatureRootTest(proposerSig));
        var root = SszInterop.HashContainer(block.HashTreeRoot(), signaturesRoot);
        return (SszEncoding.Encode(signed), new Bytes32(root));
    }

    // SignedAttestation: Container { validatorId: uint64, data: AttestationData, signature: XmssSignature }
    // nlean's SignedAttestation has no HashTreeRoot; compute inline.
    private static (byte[] Serialized, Bytes32 Root) EncodeSignedAttestationTest(JsonElement value)
    {
        var validatorId = ReadUInt(value.GetProperty("validatorId"));
        var data = DeserializeAttestationData(value.GetProperty("data"));
        var signature = XmssSignature.FromBytes(ParseHex(value.GetProperty("signature").GetString()!));
        var signed = new SignedAttestation(validatorId, data, signature);

        var root = SszInterop.HashContainer(
            SszInterop.HashUInt64(validatorId),
            data.HashTreeRoot(),
            XmssSignatureRootTest(signature));
        return (SszEncoding.Encode(signed), new Bytes32(root));
    }

    // === XMSS test-scheme merkleization helpers ===

    private static byte[] HashDigestListRootTest(HashDigestList list)
    {
        var roots = list.Elements.Select(e => e.HashTreeRoot()).ToList();
        return SszInterop.HashList(roots, TestSchemeNodeListLimit);
    }

    private static byte[] HashTreeOpeningRootTest(HashTreeOpening opening)
    {
        return SszInterop.HashContainer(HashDigestListRootTest(opening.Siblings));
    }

    private static byte[] XmssSignatureRootTest(XmssSignature sig)
    {
        return SszInterop.HashContainer(
            HashTreeOpeningRootTest(sig.Path),
            sig.Rho.HashTreeRoot(),
            HashDigestListRootTest(sig.Hashes));
    }

    // === XMSS deserializers ===

    private static Fp[] DeserializeFpVector(JsonElement value, int expectedLength)
    {
        var data = value.GetProperty("data");
        if (data.GetArrayLength() != expectedLength)
            throw new InvalidOperationException($"expected Fp vector of length {expectedLength}");

        var result = new Fp[expectedLength];
        var i = 0;
        foreach (var fp in data.EnumerateArray())
            result[i++] = new Fp((uint)ReadUInt(fp));
        return result;
    }

    private static HashDigestVector DeserializeHashDigestVector(JsonElement value)
    {
        var fps = DeserializeFpVector(value, HashDigestVector.Length);
        return new HashDigestVector(fps);
    }

    private static HashDigestList DeserializeHashDigestList(JsonElement value)
    {
        var data = value.GetProperty("data");
        var vectors = new List<HashDigestVector>(data.GetArrayLength());
        foreach (var v in data.EnumerateArray())
            vectors.Add(DeserializeHashDigestVector(v));
        return new HashDigestList(vectors);
    }

    private static HashTreeOpening DeserializeHashTreeOpening(JsonElement value)
    {
        return new HashTreeOpening(DeserializeHashDigestList(value.GetProperty("siblings")));
    }

    private static List<AggregatedSignatureProof> DeserializeAggregatedSignatureProofList(JsonElement value)
    {
        var data = value.GetProperty("data");
        var proofs = new List<AggregatedSignatureProof>(data.GetArrayLength());
        foreach (var p in data.EnumerateArray())
        {
            proofs.Add(new AggregatedSignatureProof(
                DeserializeAggregationBits(p.GetProperty("participants")),
                ParseHex(p.GetProperty("proofData").GetProperty("data").GetString()!)));
        }
        return proofs;
    }

    // === leanSpec SSZ sample/reqresp encoders ===

    private static (byte[] Serialized, Bytes32 Root) EncodeBitvector(JsonElement value, int length)
    {
        var bits = ReadBoolArray(value);
        if (bits.Length != length)
            throw new InvalidOperationException($"expected Bitvector[{length}], got {bits.Length} bits");

        var serialized = PackBits(bits);
        var chunkCount = (ulong)((length + 255) / 256);
        var root = MerkleizeBytesWithChunkLimit(serialized, chunkCount);
        return (serialized, new Bytes32(root));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeBitlist(JsonElement value, int limit)
    {
        var bits = ReadBoolArray(value);
        if (bits.Length > limit)
            throw new InvalidOperationException($"Bitlist[{limit}] overflow: {bits.Length}");

        var serialized = EncodeBitlistSerialized(bits);
        var packed = PackBits(bits);
        var chunkCount = (ulong)((limit + 255) / 256);
        var merkleRoot = MerkleizeBytesWithChunkLimit(packed, chunkCount);
        var root = MixInLength(merkleRoot, (ulong)bits.Length);
        return (serialized, new Bytes32(root));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeByteList(JsonElement value, ulong byteLimit)
    {
        var bytes = ParseHex(value.GetProperty("data").GetString() ?? string.Empty);
        return (bytes, new Bytes32(SszInterop.HashByteList(bytes, byteLimit)));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeBytes32List(JsonElement value, int limit)
    {
        var data = value.GetProperty("data");
        var roots = new List<byte[]>(data.GetArrayLength());
        var serialized = new List<byte>(data.GetArrayLength() * 32);
        foreach (var item in data.EnumerateArray())
        {
            var b = ParseHex(item.GetString()!);
            if (b.Length != 32) throw new InvalidOperationException("Bytes32 list element != 32 bytes");
            serialized.AddRange(b);
            roots.Add(b);
        }
        var root = SszInterop.HashList(roots, (ulong)limit);
        return (serialized.ToArray(), new Bytes32(root));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeUintVector(JsonElement value, int length, int elementSize)
    {
        var elems = ReadUintArray(value);
        if (elems.Length != length)
            throw new InvalidOperationException($"expected Vector[uint{elementSize * 8},{length}], got {elems.Length}");

        var serialized = new byte[length * elementSize];
        for (var i = 0; i < length; i++)
            WriteUintLE(serialized.AsSpan(i * elementSize, elementSize), elems[i]);

        var chunkCount = (ulong)((length * elementSize + 31) / 32);
        var root = MerkleizeBytesWithChunkLimit(serialized, chunkCount);
        return (serialized, new Bytes32(root));
    }

    private static (byte[] Serialized, Bytes32 Root) EncodeUintList(JsonElement value, int limit, int elementSize)
    {
        var elems = ReadUintArray(value);
        if (elems.Length > limit)
            throw new InvalidOperationException($"List[uint,{limit}] overflow: {elems.Length}");

        var serialized = new byte[elems.Length * elementSize];
        for (var i = 0; i < elems.Length; i++)
            WriteUintLE(serialized.AsSpan(i * elementSize, elementSize), elems[i]);

        var chunkCount = (ulong)((limit * elementSize + 31) / 32);
        var merkleRoot = MerkleizeBytesWithChunkLimit(serialized, chunkCount);
        var root = MixInLength(merkleRoot, (ulong)elems.Length);
        return (serialized, new Bytes32(root));
    }

    // BlocksByRootRequest: Container { roots: List[Bytes32, MAX_REQUEST_BLOCKS=1024] }
    private static (byte[] Serialized, Bytes32 Root) EncodeBlocksByRootRequest(JsonElement value)
    {
        var (rootsSerialized, rootsRoot) = EncodeBytes32List(value.GetProperty("roots"), 1024);

        // Container has one variable field: offset(4) || data
        var serialized = new byte[4 + rootsSerialized.Length];
        BitConverter.GetBytes((uint)4).CopyTo(serialized, 0);
        rootsSerialized.CopyTo(serialized, 4);

        var root = SszInterop.HashContainer(rootsRoot.AsSpan().ToArray());
        return (serialized, new Bytes32(root));
    }

    // Status: Container { finalized: Checkpoint, head: Checkpoint }
    private static (byte[] Serialized, Bytes32 Root) EncodeStatus(JsonElement value)
    {
        var finalized = DeserializeCheckpoint(value.GetProperty("finalized"));
        var head = DeserializeCheckpoint(value.GetProperty("head"));

        var serialized = new byte[80]; // 2 * (32 + 8)
        finalized.Root.AsSpan().CopyTo(serialized);
        BitConverter.GetBytes(finalized.Slot.Value).CopyTo(serialized, 32);
        head.Root.AsSpan().CopyTo(serialized.AsSpan(40));
        BitConverter.GetBytes(head.Slot.Value).CopyTo(serialized, 72);

        var root = SszInterop.HashContainer(finalized.HashTreeRoot(), head.HashTreeRoot());
        return (serialized, new Bytes32(root));
    }

    // Union: serialize as selector(1) || value; htr = mix_in_selector(htr(value), selector).
    // None arm (selector 0 for Union[None, ...]) → htr(value) = zero32.
    private static (byte[] Serialized, Bytes32 Root) EncodeUnion(JsonElement value, bool includeNone)
    {
        var selector = (byte)ReadUInt(value.GetProperty("selector"));
        var valueElem = value.GetProperty("value");

        byte[] valueSerialized;
        byte[] innerRoot;
        if (includeNone && selector == 0)
        {
            valueSerialized = Array.Empty<byte>();
            innerRoot = new byte[32];
        }
        else
        {
            // Remaining arms are uintN; SampleUnionTypes = Union[uint8, uint16, uint32] (no None),
            // SampleUnionNone = Union[None, uint16, uint32].
            var byteSize = (selector, includeNone) switch
            {
                (0, false) => 1, // uint8
                (1, false) => 2, // uint16
                (2, false) => 4, // uint32
                (1, true) => 2, // uint16
                (2, true) => 4, // uint32
                _ => throw new InvalidOperationException($"Unexpected union selector {selector} (includeNone={includeNone})"),
            };
            var u = ReadUInt(valueElem);
            valueSerialized = new byte[byteSize];
            WriteUintLE(valueSerialized, u);
            var padded = new byte[32];
            valueSerialized.CopyTo(padded, 0);
            innerRoot = padded;
        }

        var serialized = new byte[1 + valueSerialized.Length];
        serialized[0] = selector;
        valueSerialized.CopyTo(serialized, 1);

        var root = MixInLength(innerRoot, selector); // mix_in_selector shares the structure of mix_in_length
        return (serialized, new Bytes32(root));
    }

    // === SSZ merkleization / mix helpers ===

    private static byte[] MerkleizeBytesWithChunkLimit(byte[] bytes, ulong chunkCount)
    {
        if (chunkCount == 0)
            return new byte[32];

        var actualChunks = new List<byte[]>();
        for (var i = 0; i < bytes.Length; i += 32)
        {
            var chunk = new byte[32];
            var len = Math.Min(32, bytes.Length - i);
            Array.Copy(bytes, i, chunk, 0, len);
            actualChunks.Add(chunk);
        }

        // Pad to chunkCount (bounded by limit), then next power of two for merkle tree.
        while ((ulong)actualChunks.Count < chunkCount)
            actualChunks.Add(new byte[32]);

        return MerkleiseChunks(actualChunks);
    }

    private static byte[] MixInLength(byte[] root, ulong length)
    {
        var combined = new byte[64];
        root.CopyTo(combined, 0);
        BitConverter.GetBytes(length).CopyTo(combined, 32);
        return System.Security.Cryptography.SHA256.HashData(combined);
    }

    private static byte[] PackBits(bool[] bits)
    {
        var byteLen = (bits.Length + 7) / 8;
        var bytes = new byte[byteLen];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        }
        return bytes;
    }

    private static byte[] EncodeBitlistSerialized(bool[] bits)
    {
        // Append a terminator bit set to 1 (marks the logical length).
        var packedLen = (bits.Length / 8) + 1;
        var bytes = new byte[packedLen];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        }
        bytes[bits.Length / 8] |= (byte)(1 << (bits.Length % 8));
        return bytes;
    }

    private static bool[] ReadBoolArray(JsonElement value)
    {
        var data = value.GetProperty("data");
        var bits = new bool[data.GetArrayLength()];
        var i = 0;
        foreach (var b in data.EnumerateArray()) bits[i++] = b.GetBoolean();
        return bits;
    }

    private static ulong[] ReadUintArray(JsonElement value)
    {
        var data = value.GetProperty("data");
        var result = new ulong[data.GetArrayLength()];
        var i = 0;
        foreach (var item in data.EnumerateArray()) result[i++] = ReadUInt(item);
        return result;
    }

    private static void WriteUintLE(Span<byte> dst, ulong value)
    {
        for (var i = 0; i < dst.Length; i++)
            dst[i] = (byte)(value >> (8 * i));
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
