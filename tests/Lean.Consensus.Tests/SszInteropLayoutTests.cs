using System.Buffers.Binary;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SszInteropLayoutTests
{
    [Test]
    public void SignedAttestation_ZeroEncodingMatchesReamZeamVector()
    {
        var signedAttestation = new SignedAttestation(
            0,
            new AttestationData(
                new Slot(0),
                new Checkpoint(Bytes32.Zero(), new Slot(0)),
                new Checkpoint(Bytes32.Zero(), new Slot(0)),
                new Checkpoint(Bytes32.Zero(), new Slot(0))),
            XmssSignature.Empty());

        var encoded = SszEncoding.Encode(signedAttestation);

        // Zeam and Ream both assert this devnet2 signed-attestation vector as all-zero 3248 bytes.
        Assert.That(encoded.Length, Is.EqualTo(3248));
        Assert.That(encoded, Is.All.EqualTo((byte)0));
    }

    [Test]
    public void SignedBlockWithAttestation_ZeroEncodingMatchesReamZeamLayout()
    {
        var signedBlock = new SignedBlockWithAttestation(
            new BlockWithAttestation(
                new Block(
                    new Slot(0),
                    0,
                    Bytes32.Zero(),
                    Bytes32.Zero(),
                    new BlockBody(Array.Empty<AggregatedAttestation>())),
                new Attestation(
                    0,
                    new AttestationData(
                        new Slot(0),
                        new Checkpoint(Bytes32.Zero(), new Slot(0)),
                        new Checkpoint(Bytes32.Zero(), new Slot(0)),
                        new Checkpoint(Bytes32.Zero(), new Slot(0))))),
            new BlockSignatures(
                Array.Empty<AggregatedSignatureProof>(),
                XmssSignature.Empty()));

        var encoded = SszEncoding.Encode(signedBlock);

        var expected = new byte[3352];
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0, SszEncoding.UInt32Length), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(4, SszEncoding.UInt32Length), 236);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(8, SszEncoding.UInt32Length), 140);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(228, SszEncoding.UInt32Length), 84);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(232, SszEncoding.UInt32Length), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(236, SszEncoding.UInt32Length), 3116);

        Assert.That(encoded.Length, Is.EqualTo(expected.Length));
        Assert.That(encoded, Is.EqualTo(expected));
    }
}
