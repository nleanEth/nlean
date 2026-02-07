using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SszLeanSpecVectorsTests
{
    [Test]
    public void SignedBlockWithAttestationEncodingUsesFixedXmssSignature()
    {
        var emptySignature = XmssSignature.Empty();

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
                emptySignature));

        var encoded = SszEncoding.Encode(signedBlock);
        var messageBytes = SszEncoding.Encode(signedBlock.Message);
        var signatureBytes = SszEncoding.Encode(signedBlock.Signature);

        Assert.That(encoded.Length, Is.EqualTo(8 + messageBytes.Length + signatureBytes.Length));

        var messageOffset = BitConverter.ToInt32(encoded, 0);
        var signatureOffset = BitConverter.ToInt32(encoded, 4);
        Assert.That(messageOffset, Is.EqualTo(8));
        Assert.That(signatureOffset, Is.EqualTo(8 + messageBytes.Length));

        Assert.That(encoded.AsSpan(messageOffset, messageBytes.Length).ToArray(), Is.EqualTo(messageBytes));
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
        var expectedLength = SszEncoding.UInt64Length + SszEncoding.AttestationDataLength + XmssSignature.Length;

        Assert.That(encoded.Length, Is.EqualTo(expectedLength));
        Assert.That(encoded.AsSpan(expectedLength - XmssSignature.Length, XmssSignature.Length)
            .ToArray(), Is.EqualTo(emptySignature.Bytes.ToArray()));
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
