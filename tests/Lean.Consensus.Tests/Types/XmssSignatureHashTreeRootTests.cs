using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Types;

[TestFixture]
public class XmssSignatureHashTreeRootTests
{
    [Test]
    public void HashTreeRoot_IsContainerRoot_NotBytesVector()
    {
        var siblings = new HashDigestList(new[]
        {
            new HashDigestVector(Enumerable.Repeat(new Fp(1), 8)),
            new HashDigestVector(Enumerable.Repeat(new Fp(2), 8)),
            new HashDigestVector(Enumerable.Repeat(new Fp(3), 8))
        });
        var path = new HashTreeOpening(siblings);
        var rho = new Randomness(Enumerable.Range(1, 7).Select(i => new Fp((uint)i)));
        var hashes = new HashDigestList(new[]
        {
            new HashDigestVector(Enumerable.Repeat(new Fp(10), 8)),
            new HashDigestVector(Enumerable.Repeat(new Fp(20), 8))
        });

        var sig = new XmssSignature(path, rho, hashes);
        var root = sig.HashTreeRoot();

        // Root must be 32 bytes
        Assert.That(root, Has.Length.EqualTo(32));

        // The same signature produces the same root (deterministic)
        var root2 = sig.HashTreeRoot();
        Assert.That(root, Is.EqualTo(root2));

        // A different signature produces a different root
        var sig2 = new XmssSignature(
            new HashTreeOpening(HashDigestList.Empty()),
            Randomness.Zero(),
            HashDigestList.Empty());
        var root3 = sig2.HashTreeRoot();
        Assert.That(root, Is.Not.EqualTo(root3));
    }

    [Test]
    public void Empty_HasZeroFieldValues()
    {
        var sig = XmssSignature.Empty();

        Assert.That(sig.Path.Siblings.Elements, Has.Count.EqualTo(0));
        Assert.That(sig.Rho.Elements, Has.All.EqualTo(Fp.Zero));
        Assert.That(sig.Rho.Elements, Has.Count.EqualTo(7));
        Assert.That(sig.Hashes.Elements, Has.Count.EqualTo(0));

        // Empty should produce a valid 32-byte root
        var root = sig.HashTreeRoot();
        Assert.That(root, Has.Length.EqualTo(32));
    }

    [Test]
    public void HashDigestVector_HashTreeRoot_MerkleizesFields()
    {
        var elements = Enumerable.Range(1, 8).Select(i => new Fp((uint)i));
        var vector = new HashDigestVector(elements);
        var root = vector.HashTreeRoot();

        Assert.That(root, Has.Length.EqualTo(32));

        // Same values produce same root
        var vector2 = new HashDigestVector(Enumerable.Range(1, 8).Select(i => new Fp((uint)i)));
        Assert.That(vector2.HashTreeRoot(), Is.EqualTo(root));

        // Different values produce different root
        var vector3 = new HashDigestVector(Enumerable.Repeat(Fp.Zero, 8));
        Assert.That(vector3.HashTreeRoot(), Is.Not.EqualTo(root));
    }

    [Test]
    public void HashDigestList_HashTreeRoot_UsesListMerkleization()
    {
        // Empty list should have a consistent root
        var emptyList = HashDigestList.Empty();
        var emptyRoot = emptyList.HashTreeRoot();
        Assert.That(emptyRoot, Has.Length.EqualTo(32));

        // Repeated call returns same root
        Assert.That(emptyList.HashTreeRoot(), Is.EqualTo(emptyRoot));

        // Non-empty list should differ from empty
        var nonEmptyList = new HashDigestList(new[]
        {
            HashDigestVector.Zero()
        });
        var nonEmptyRoot = nonEmptyList.HashTreeRoot();
        Assert.That(nonEmptyRoot, Is.Not.EqualTo(emptyRoot));

        // List with two elements differs from list with one
        var twoElementList = new HashDigestList(new[]
        {
            HashDigestVector.Zero(),
            HashDigestVector.Zero()
        });
        Assert.That(twoElementList.HashTreeRoot(), Is.Not.EqualTo(nonEmptyRoot));
    }

    [Test]
    public void Randomness_HashTreeRoot_MerkleizesFields()
    {
        var elements = Enumerable.Range(1, 7).Select(i => new Fp((uint)i));
        var rho = new Randomness(elements);
        var root = rho.HashTreeRoot();

        Assert.That(root, Has.Length.EqualTo(32));

        // Same values produce same root
        var rho2 = new Randomness(Enumerable.Range(1, 7).Select(i => new Fp((uint)i)));
        Assert.That(rho2.HashTreeRoot(), Is.EqualTo(root));

        // Different values produce different root
        var rho3 = Randomness.Zero();
        Assert.That(rho3.HashTreeRoot(), Is.Not.EqualTo(root));
    }
}
