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
