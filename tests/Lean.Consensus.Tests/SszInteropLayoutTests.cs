using System.Buffers.Binary;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SszInteropLayoutTests
{
    [Test]
    public void SignedAttestation_ZeroEncodingMatchesFixedSignatureLayout()
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

        // XmssSignature is fixed-size (no offset field in parent).
        // Layout: ValidatorId(8) + AttestationData(128) + XmssSignature(inline)
        var signatureBytes = SszEncoding.Encode(XmssSignature.Empty());
        var fixedSize = SszEncoding.UInt64Length + SszEncoding.AttestationDataLength;
        var expectedLength = fixedSize + signatureBytes.Length;
        Assert.That(encoded.Length, Is.EqualTo(expectedLength));

        // XmssSignature starts directly at position 136 (no offset)
        Assert.That(encoded.AsSpan(fixedSize, signatureBytes.Length).ToArray(), Is.EqualTo(signatureBytes));
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
