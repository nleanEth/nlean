using System.Collections;
using System.Runtime.InteropServices;
using Lean.Consensus.Types;
using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SszRootTests
{
    [Test]
    public void Bytes32RootIsIdentity()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var value = new Bytes32(bytes);

        var root = value.HashTreeRoot();

        Assert.That(root, Is.EqualTo(bytes));
    }

    [Test]
    public void CheckpointRootMatchesMerkleization()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        var checkpoint = new Checkpoint(new Bytes32(bytes), new Slot(5));

        var expected = HashContainer(
            HashBytes32(bytes),
            HashUInt64(5));

        Assert.That(checkpoint.HashTreeRoot(), Is.EqualTo(expected));
    }

    [Test]
    public void BlockBodyRootUsesListMixInLength()
    {
        var data = new AttestationData(
            new Slot(10),
            new Checkpoint(new Bytes32(new byte[32]), new Slot(1)),
            new Checkpoint(new Bytes32(new byte[32]), new Slot(2)),
            new Checkpoint(new Bytes32(new byte[32]), new Slot(3)));

        var att1 = new AggregatedAttestation(AggregationBits.FromValidatorIndices(new ulong[] { 1, 3 }), data);
        var att2 = new AggregatedAttestation(AggregationBits.FromValidatorIndices(new ulong[] { 2 }), data);
        var body = new BlockBody(new[] { att1, att2 });

        var expected = HashList(
            new[] { att1.HashTreeRoot(), att2.HashTreeRoot() },
            maxLength: 2);

        Assert.That(body.HashTreeRoot(), Is.EqualTo(expected));
    }

    [Test]
    public void AggregationBitsRootMatchesBitlistMerkleization()
    {
        var bits = new[] { true, false, true, true, false };
        var aggregation = new AggregationBits(bits);

        var expected = HashBitlist(bits);

        Assert.That(aggregation.HashTreeRoot(), Is.EqualTo(expected));
    }

    [TestCase(new[] { true, true, false, true, false, true, false, false }, "e3c680050925d8be5b3c4f4c2b5619010db0015f1bfed7643c0b4fc3700d2d15")]
    [TestCase(new[] { false, true, false, true }, "62a896c7f7f5be6f6d17063247bf1d2cd0410dbd1fdc1400c097d4e09574cca2")]
    [TestCase(new[] { false, true, false }, "0094579cfc7b716038d416a311465309bea202baa922b224a7b08f01599642fb")]
    public void AggregationBitsRootMatchesLeanSpecVectors(bool[] bits, string expectedRootHex)
    {
        var aggregation = new AggregationBits(bits);

        var expected = Convert.FromHexString(expectedRootHex);

        Assert.That(aggregation.HashTreeRoot(), Is.EqualTo(expected));
    }

    [Test]
    public void AggregatedSignatureProofRootMatchesContainer()
    {
        var participants = new AggregationBits(new[] { true, false, true });
        var proof = new byte[] { 1, 2, 3, 4, 5 };
        var signature = new AggregatedSignatureProof(participants, proof);

        var expected = HashContainer(
            participants.HashTreeRoot(),
            HashBytes(proof));

        Assert.That(signature.HashTreeRoot(), Is.EqualTo(expected));
    }

    [Test]
    public void BlockSignaturesRootMatchesContainer()
    {
        var proofs = new List<AggregatedSignatureProof>
        {
            new(AggregationBits.FromValidatorIndices(new ulong[] { 0 }), new byte[] { 9, 9 }),
            new(AggregationBits.FromValidatorIndices(new ulong[] { 1, 2 }), new byte[] { 7, 7, 7 })
        };

        var signature = XmssSignature.Empty();
        var signatures = new BlockSignatures(proofs, signature);

        var proofRoots = proofs.Select(p => p.HashTreeRoot()).ToList();
        var attestationRoot = HashList(proofRoots, (ulong)proofs.Count);
        var signatureRoot = signature.HashTreeRoot();

        var expected = HashContainer(
            attestationRoot,
            signatureRoot);

        Assert.That(signatures.HashTreeRoot(), Is.EqualTo(expected));
    }

    [Test]
    public void BlockHeaderRootMatchesContainer()
    {
        var header = new BlockHeader(
            new Slot(12),
            7,
            new Bytes32(Enumerable.Repeat((byte)1, 32).ToArray()),
            new Bytes32(Enumerable.Repeat((byte)2, 32).ToArray()),
            new Bytes32(Enumerable.Repeat((byte)3, 32).ToArray()));

        var expected = HashContainer(
            HashUInt64(header.Slot.Value),
            HashUInt64(header.ProposerIndex),
            HashBytes32(header.ParentRoot.AsSpan().ToArray()),
            HashBytes32(header.StateRoot.AsSpan().ToArray()),
            HashBytes32(header.BodyRoot.AsSpan().ToArray()));

        Assert.That(header.HashTreeRoot(), Is.EqualTo(expected));
    }

    [Test]
    public void BlockRootMatchesContainer()
    {
        var body = new BlockBody(Array.Empty<AggregatedAttestation>());
        var block = new Block(
            new Slot(22),
            11,
            new Bytes32(Enumerable.Repeat((byte)9, 32).ToArray()),
            new Bytes32(Enumerable.Repeat((byte)8, 32).ToArray()),
            body);

        var expected = HashContainer(
            HashUInt64(block.Slot.Value),
            HashUInt64(block.ProposerIndex),
            HashBytes32(block.ParentRoot.AsSpan().ToArray()),
            HashBytes32(block.StateRoot.AsSpan().ToArray()),
            block.Body.HashTreeRoot());

        Assert.That(block.HashTreeRoot(), Is.EqualTo(expected));
    }

    [Test]
    public void SignedBlockWithAttestationRootMatchesContainer()
    {
        var body = new BlockBody(Array.Empty<AggregatedAttestation>());
        var block = new Block(
            new Slot(30),
            1,
            new Bytes32(Enumerable.Repeat((byte)4, 32).ToArray()),
            new Bytes32(Enumerable.Repeat((byte)5, 32).ToArray()),
            body);
        var message = new BlockWithAttestation(block, new Attestation(3, new AttestationData(
            new Slot(30),
            Checkpoint.Default(),
            Checkpoint.Default(),
            Checkpoint.Default())));
        var signature = new BlockSignatures(
            Array.Empty<AggregatedSignatureProof>(),
            XmssSignature.Empty());
        var signed = new SignedBlockWithAttestation(message, signature);

        var expected = HashContainer(
            message.HashTreeRoot(),
            signature.HashTreeRoot());

        Assert.That(signed.HashTreeRoot(), Is.EqualTo(expected));
    }

    private static byte[] HashContainer(params byte[][] fieldRoots)
    {
        var roots = new UInt256[fieldRoots.Length];
        for (var i = 0; i < fieldRoots.Length; i++)
        {
            Merkle.Merkleize(out roots[i], fieldRoots[i]);
        }

        Merkle.Merkleize(out UInt256 root, roots);
        return ToBytes(root);
    }

    private static byte[] HashList(IReadOnlyList<byte[]> elementRoots, ulong maxLength)
    {
        var roots = new UInt256[elementRoots.Count];
        for (var i = 0; i < elementRoots.Count; i++)
        {
            Merkle.Merkleize(out roots[i], elementRoots[i]);
        }

        Merkle.Merkleize(out UInt256 root, roots, maxLength);
        Merkle.MixIn(ref root, elementRoots.Count);
        return ToBytes(root);
    }

    private static byte[] HashBytes(byte[] value)
    {
        Merkle.Merkleize(out UInt256 root, value);
        Merkle.MixIn(ref root, value.Length);
        return ToBytes(root);
    }

    private static byte[] HashBitlist(bool[] bits)
    {
        var bitArray = new BitArray(bits);
        Merkle.Merkleize(out UInt256 root, bitArray, (ulong)bits.Length);
        return ToBytes(root);
    }

    private static byte[] HashUInt64(ulong value)
    {
        Merkle.Merkleize(out UInt256 root, value);
        return ToBytes(root);
    }

    private static byte[] HashBytes32(byte[] value)
    {
        Merkle.Merkleize(out UInt256 root, value);
        return ToBytes(root);
    }

    private static byte[] ToBytes(UInt256 value)
    {
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).ToArray();
    }
}
