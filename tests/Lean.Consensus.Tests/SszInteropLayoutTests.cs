using System.Buffers.Binary;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SszInteropLayoutTests
{
    [Test]
    public void SignedAttestation_ZeroEncodingMatchesVariableSizeLayout()
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

        // Fixed part: ValidatorId(8) + AttestationData(128) + offset(4) = 140
        // Signature (empty): fixed(36) + path(4) + hashes(0) = 40
        // Total = 180
        var signatureBytes = SszEncoding.Encode(XmssSignature.Empty());
        var fixedSize = SszEncoding.UInt64Length + SszEncoding.AttestationDataLength + SszEncoding.UInt32Length;
        var expectedLength = fixedSize + signatureBytes.Length;
        Assert.That(encoded.Length, Is.EqualTo(expectedLength));

        // The offset at position (8+128)=136 should point to the start of the signature data
        var signatureOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            encoded.AsSpan(SszEncoding.UInt64Length + SszEncoding.AttestationDataLength, 4));
        Assert.That(signatureOffset, Is.EqualTo(fixedSize));
    }

    [Test]
    public void SignedBlockWithAttestation_ZeroEncodingMatchesVariableSizeLayout()
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
        var messageBytes = SszEncoding.Encode(signedBlock.Message);
        var signatureBytes = SszEncoding.Encode(signedBlock.Signature);

        // SignedBlockWithAttestation: 2 variable fields = 2 offsets (8 bytes fixed)
        var fixedSize = SszEncoding.UInt32Length * 2;
        Assert.That(encoded.Length, Is.EqualTo(fixedSize + messageBytes.Length + signatureBytes.Length));

        // Verify offsets
        var messageOffset = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(0, 4));
        var signatureOffset = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4, 4));
        Assert.That(messageOffset, Is.EqualTo(fixedSize));
        Assert.That(signatureOffset, Is.EqualTo(fixedSize + messageBytes.Length));

        // Verify data
        Assert.That(encoded.AsSpan((int)messageOffset, messageBytes.Length).ToArray(), Is.EqualTo(messageBytes));
        Assert.That(encoded.AsSpan((int)signatureOffset, signatureBytes.Length).ToArray(), Is.EqualTo(signatureBytes));
    }
}
