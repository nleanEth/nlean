using System.Buffers.Binary;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Types;

[TestFixture]
public class XmssSignatureSszEncodingTests
{
    [Test]
    public void Encode_EmptySignature_HasCorrectLayout()
    {
        // Empty signature: empty path, zero rho, empty hashes
        var sig = XmssSignature.Empty();
        var encoded = SszEncoding.Encode(sig);

        // Fixed part = offset_path(4) + rho(28) + offset_hashes(4) = 36
        // path_data = HashTreeOpening Container with 1 offset field = 4 bytes (offset for empty siblings)
        // hashes_data = 0 bytes (empty list)
        // Total = 36 + 4 + 0 = 40
        Assert.That(encoded, Has.Length.EqualTo(40));

        // Verify offset_path points to byte 36 (start of dynamic area)
        var pathOffset = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(0, 4));
        Assert.That(pathOffset, Is.EqualTo(36));

        // Verify offset_hashes points to byte 40 (after path data)
        var hashesOffset = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(32, 4));
        Assert.That(hashesOffset, Is.EqualTo(40));
    }

    [Test]
    public void Encode_SignatureWithData_HasCorrectLength()
    {
        // 1 sibling (HashDigestVector = 8 Fp values = 32 bytes), 1 hash (32 bytes)
        var sibling = new HashDigestVector(Enumerable.Repeat(new Fp(1), 8));
        var siblings = new HashDigestList(new[] { sibling });
        var path = new HashTreeOpening(siblings);
        var rho = Randomness.Zero();
        var hash = new HashDigestVector(Enumerable.Repeat(new Fp(2), 8));
        var hashes = new HashDigestList(new[] { hash });

        var sig = new XmssSignature(path, rho, hashes);
        var encoded = SszEncoding.Encode(sig);

        // Fixed = 36
        // path_data = HashTreeOpening Container: offset(4) + siblings_data(32) = 36
        // hashes_data = 1 element * 32 bytes = 32
        // Total = 36 + 36 + 32 = 104
        Assert.That(encoded, Has.Length.EqualTo(104));
    }

    [Test]
    public void Encode_RhoBytes_AreInCorrectPosition()
    {
        // Create a signature with known rho values
        var rhoElements = Enumerable.Range(1, 7).Select(i => new Fp((uint)i));
        var rho = new Randomness(rhoElements);
        var sig = new XmssSignature(
            new HashTreeOpening(HashDigestList.Empty()),
            rho,
            HashDigestList.Empty());

        var encoded = SszEncoding.Encode(sig);

        // Rho is at offset 4 (after path offset) and spans 28 bytes (7 * 4)
        // Each Fp is 4 bytes little-endian
        for (var i = 0; i < 7; i++)
        {
            var fpValue = BinaryPrimitives.ReadUInt32LittleEndian(
                encoded.AsSpan(4 + (i * SszEncoding.FpLength), SszEncoding.FpLength));
            Assert.That(fpValue, Is.EqualTo((uint)(i + 1)),
                $"Rho element {i} should be {i + 1}");
        }
    }

    [Test]
    public void Encode_RoundTripsWithEncodeBytes()
    {
        // Verify that SszEncoding.Encode and EncodeBytes produce the same result
        // for structured (non-legacy) signatures
        var sibling = new HashDigestVector(Enumerable.Repeat(new Fp(42), 8));
        var siblings = new HashDigestList(new[] { sibling });
        var path = new HashTreeOpening(siblings);
        var rho = new Randomness(Enumerable.Range(10, 7).Select(i => new Fp((uint)i)));
        var hashes = new HashDigestList(new[]
        {
            new HashDigestVector(Enumerable.Repeat(new Fp(99), 8))
        });

        var sig = new XmssSignature(path, rho, hashes);

        var fromSszEncoding = SszEncoding.Encode(sig);
        var fromEncodeBytes = sig.EncodeBytes();

        Assert.That(fromSszEncoding, Is.EqualTo(fromEncodeBytes));
    }
}
