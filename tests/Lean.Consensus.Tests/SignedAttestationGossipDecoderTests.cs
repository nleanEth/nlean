using Lean.Consensus;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SignedAttestationGossipDecoderTests
{
    [Test]
    public void DecodeAndValidate_ReturnsSuccess_ForValidSignedAttestation()
    {
        var decoder = new SignedAttestationGossipDecoder();
        var attestation = CreateSignedAttestation();
        var payload = SszEncoding.Encode(attestation);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Failure, Is.EqualTo(AttestationGossipDecodeFailure.None));
        Assert.That(result.Attestation, Is.Not.Null);
        Assert.That(result.Attestation!.ValidatorId, Is.EqualTo(attestation.ValidatorId));
        Assert.That(result.Attestation.Message.Slot.Value, Is.EqualTo(attestation.Message.Slot.Value));
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForEmptyPayload()
    {
        var decoder = new SignedAttestationGossipDecoder();

        var result = decoder.DecodeAndValidate(Array.Empty<byte>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(AttestationGossipDecodeFailure.EmptyPayload));
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForInvalidLength()
    {
        var decoder = new SignedAttestationGossipDecoder();
        var payload = SszEncoding.Encode(CreateSignedAttestation());
        var truncated = payload[..^1];

        var result = decoder.DecodeAndValidate(truncated);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(AttestationGossipDecodeFailure.InvalidSsz));
    }

    private static SignedAttestation CreateSignedAttestation()
    {
        var data = new AttestationData(
            new Slot(3),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x01, 32).ToArray()), new Slot(2)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x02, 32).ToArray()), new Slot(2)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x03, 32).ToArray()), new Slot(1)));

        return new SignedAttestation(7, data, XmssSignature.Empty());
    }
}
