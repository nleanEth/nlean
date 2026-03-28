using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SszLeanSpecVectorsTests
{
    [Test]
    public void SignedBlockEncodingUsesFixedXmssSignature()
    {
        var emptySignature = XmssSignature.Empty();

        var signedBlock = new SignedBlock(
            new Block(
                new Slot(0),
                0,
                Bytes32.Zero(),
                Bytes32.Zero(),
                new BlockBody(Array.Empty<AggregatedAttestation>())),
            new BlockSignatures(
                Array.Empty<AggregatedSignatureProof>(),
                emptySignature));

        var encoded = SszEncoding.Encode(signedBlock);
        var blockBytes = SszEncoding.Encode(signedBlock.Block);
        var signatureBytes = SszEncoding.Encode(signedBlock.Signature);

        Assert.That(encoded.Length, Is.EqualTo(8 + blockBytes.Length + signatureBytes.Length));

        var blockOffset = BitConverter.ToInt32(encoded, 0);
        var signatureOffset = BitConverter.ToInt32(encoded, 4);
        Assert.That(blockOffset, Is.EqualTo(8));
        Assert.That(signatureOffset, Is.EqualTo(8 + blockBytes.Length));

        Assert.That(encoded.AsSpan(blockOffset, blockBytes.Length).ToArray(), Is.EqualTo(blockBytes));
        Assert.That(encoded.AsSpan(signatureOffset, signatureBytes.Length).ToArray(), Is.EqualTo(signatureBytes));
    }

    [Test]
    public void SignedAttestationEncodingUsesFixedXmssSignature()
    {
        var emptySignature = XmssSignature.Empty();

        var signedAttestation = new SignedAttestation(
            0,
            new AttestationData(
                new Slot(0),
                new Checkpoint(Bytes32.Zero(), new Slot(0)),
                new Checkpoint(Bytes32.Zero(), new Slot(0)),
                new Checkpoint(Bytes32.Zero(), new Slot(0))),
            emptySignature);

        var encoded = SszEncoding.Encode(signedAttestation);
        var signatureBytes = SszEncoding.Encode(emptySignature);
        // XmssSignature is fixed-size (no offset field).
        // Fixed: ValidatorId(8) + AttestationData(128) = 136
        var fixedSize = SszEncoding.UInt64Length + SszEncoding.AttestationDataLength;
        var expectedLength = fixedSize + signatureBytes.Length;

        Assert.That(encoded.Length, Is.EqualTo(expectedLength));
        Assert.That(encoded.AsSpan(fixedSize, signatureBytes.Length).ToArray(), Is.EqualTo(signatureBytes));
    }

    [Test]
    public void StateEncodingMatchesLeanSpec()
    {
        var header = new BlockHeader(
            new Slot(0),
            0,
            Bytes32.Zero(),
            Bytes32.Zero(),
            Bytes32.Zero());

        var checkpoint = new Checkpoint(Bytes32.Zero(), new Slot(0));

        var state = new State(
            new Config(1000),
            new Slot(0),
            header,
            checkpoint,
            checkpoint,
            Array.Empty<Bytes32>(),
            Array.Empty<bool>(),
            Array.Empty<Validator>(),
            Array.Empty<Bytes32>(),
            Array.Empty<bool>());

        var encoded = SszEncoding.Encode(state);
        var expectedHex =
            "e80300000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000e4000000e4000000e5000000e5000000e5" +
            "0000000101";

        Assert.That(encoded, Is.EqualTo(Convert.FromHexString(expectedHex)));
    }
}
