using System.Buffers.Binary;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Types;

[TestFixture]
public class XmssSignatureSszDecodingTests
{
    [Test]
    public void Roundtrip_EmptySignature()
    {
        var original = XmssSignature.Empty();
        var encoded = SszEncoding.Encode(original);
        var decoded = SszDecoding.DecodeXmssSignature(encoded);

        Assert.That(decoded.Path.Siblings.Elements, Has.Count.EqualTo(0));
        Assert.That(decoded.Hashes.Elements, Has.Count.EqualTo(0));
        for (var i = 0; i < Randomness.Length; i++)
        {
            Assert.That(decoded.Rho.Elements[i], Is.EqualTo(Fp.Zero));
        }
    }

    [Test]
    public void Roundtrip_SignatureWithData()
    {
        // 2 siblings
        var sibling1 = new HashDigestVector(Enumerable.Range(1, 8).Select(i => new Fp((uint)i)));
        var sibling2 = new HashDigestVector(Enumerable.Range(11, 8).Select(i => new Fp((uint)i)));
        var siblings = new HashDigestList(new[] { sibling1, sibling2 });
        var path = new HashTreeOpening(siblings);

        // specific rho
        var rho = new Randomness(Enumerable.Range(100, 7).Select(i => new Fp((uint)i)));

        // 1 hash
        var hash = new HashDigestVector(Enumerable.Range(200, 8).Select(i => new Fp((uint)i)));
        var hashes = new HashDigestList(new[] { hash });

        var original = new XmssSignature(path, rho, hashes);
        var encoded = SszEncoding.Encode(original);
        var decoded = SszDecoding.DecodeXmssSignature(encoded);

        // Verify siblings
        Assert.That(decoded.Path.Siblings.Elements, Has.Count.EqualTo(2));
        for (var i = 0; i < 8; i++)
        {
            Assert.That(decoded.Path.Siblings.Elements[0].Elements[i],
                Is.EqualTo(new Fp((uint)(i + 1))));
            Assert.That(decoded.Path.Siblings.Elements[1].Elements[i],
                Is.EqualTo(new Fp((uint)(i + 11))));
        }

        // Verify rho
        for (var i = 0; i < 7; i++)
        {
            Assert.That(decoded.Rho.Elements[i], Is.EqualTo(new Fp((uint)(i + 100))));
        }

        // Verify hashes
        Assert.That(decoded.Hashes.Elements, Has.Count.EqualTo(1));
        for (var i = 0; i < 8; i++)
        {
            Assert.That(decoded.Hashes.Elements[0].Elements[i],
                Is.EqualTo(new Fp((uint)(i + 200))));
        }
    }

    [Test]
    public void Roundtrip_HashTreeRoot_Matches()
    {
        var sibling = new HashDigestVector(Enumerable.Repeat(new Fp(42), 8));
        var path = new HashTreeOpening(new HashDigestList(new[] { sibling }));
        var rho = new Randomness(Enumerable.Range(10, 7).Select(i => new Fp((uint)i)));
        var hash = new HashDigestVector(Enumerable.Repeat(new Fp(99), 8));
        var hashes = new HashDigestList(new[] { hash });

        var original = new XmssSignature(path, rho, hashes);
        var encoded = SszEncoding.Encode(original);
        var decoded = SszDecoding.DecodeXmssSignature(encoded);

        Assert.That(decoded.HashTreeRoot(), Is.EqualTo(original.HashTreeRoot()));
    }

    [Test]
    public void DecodeHashDigestVector_KnownValues()
    {
        // Build 32 bytes: 8 Fp values [10, 20, 30, 40, 50, 60, 70, 80]
        var data = new byte[SszEncoding.HashDigestVectorLength];
        uint[] values = { 10, 20, 30, 40, 50, 60, 70, 80 };
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(i * Fp.ByteLength, Fp.ByteLength), values[i]);
        }

        var vector = SszDecoding.DecodeHashDigestVector(data);

        for (var i = 0; i < values.Length; i++)
        {
            Assert.That(vector.Elements[i], Is.EqualTo(new Fp(values[i])),
                $"Element {i} should be {values[i]}");
        }
    }

    [Test]
    public void DecodeRandomness_KnownValues()
    {
        // Build 28 bytes: 7 Fp values [5, 15, 25, 35, 45, 55, 65]
        var data = new byte[SszEncoding.RandomnessLength];
        uint[] values = { 5, 15, 25, 35, 45, 55, 65 };
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(i * Fp.ByteLength, Fp.ByteLength), values[i]);
        }

        var randomness = SszDecoding.DecodeRandomness(data);

        for (var i = 0; i < values.Length; i++)
        {
            Assert.That(randomness.Elements[i], Is.EqualTo(new Fp(values[i])),
                $"Element {i} should be {values[i]}");
        }
    }
}
