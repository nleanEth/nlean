using System.Linq;
using Lean.Consensus;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SignedAggregatedAttestationGossipDecoderTests
{
    [Test]
    public void DecodeAndValidate_ReturnsSuccess_ForValidPayload()
    {
        var decoder = new SignedAggregatedAttestationGossipDecoder();
        var original = CreateSignedAggregatedAttestation();
        var payload = SszEncoding.Encode(original);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Failure, Is.EqualTo(AggregatedAttestationGossipDecodeFailure.None));
        Assert.That(result.Attestation, Is.Not.Null);
        Assert.That(result.Attestation!.Data.Slot.Value, Is.EqualTo(original.Data.Slot.Value));
        Assert.That(result.Attestation.Data.Head.Slot.Value, Is.EqualTo(original.Data.Head.Slot.Value));
        Assert.That(result.Attestation.Data.Target.Slot.Value, Is.EqualTo(original.Data.Target.Slot.Value));
        Assert.That(result.Attestation.Data.Source.Slot.Value, Is.EqualTo(original.Data.Source.Slot.Value));
    }

    [Test]
    public void DecodeAndValidate_RoundTrip_PreservesAllFields()
    {
        var decoder = new SignedAggregatedAttestationGossipDecoder();
        var original = CreateSignedAggregatedAttestation();
        var payload = SszEncoding.Encode(original);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        var decoded = result.Attestation!;

        // Verify AttestationData fields
        Assert.That(decoded.Data.Slot.Value, Is.EqualTo(original.Data.Slot.Value));
        Assert.That(decoded.Data.Head.Root.AsSpan().ToArray(), Is.EqualTo(original.Data.Head.Root.AsSpan().ToArray()));
        Assert.That(decoded.Data.Head.Slot.Value, Is.EqualTo(original.Data.Head.Slot.Value));
        Assert.That(decoded.Data.Target.Root.AsSpan().ToArray(), Is.EqualTo(original.Data.Target.Root.AsSpan().ToArray()));
        Assert.That(decoded.Data.Target.Slot.Value, Is.EqualTo(original.Data.Target.Slot.Value));
        Assert.That(decoded.Data.Source.Root.AsSpan().ToArray(), Is.EqualTo(original.Data.Source.Root.AsSpan().ToArray()));
        Assert.That(decoded.Data.Source.Slot.Value, Is.EqualTo(original.Data.Source.Slot.Value));

        // Verify AggregatedSignatureProof participants
        Assert.That(decoded.Proof.Participants.Bits.Count, Is.EqualTo(original.Proof.Participants.Bits.Count));
        for (var i = 0; i < original.Proof.Participants.Bits.Count; i++)
        {
            Assert.That(decoded.Proof.Participants.Bits[i], Is.EqualTo(original.Proof.Participants.Bits[i]),
                $"Participant bit {i} mismatch");
        }

        // Verify proof data
        Assert.That(decoded.Proof.ProofData, Is.EqualTo(original.Proof.ProofData));
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForEmptyPayload()
    {
        var decoder = new SignedAggregatedAttestationGossipDecoder();

        var result = decoder.DecodeAndValidate(Array.Empty<byte>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(AggregatedAttestationGossipDecodeFailure.EmptyPayload));
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForTruncatedPayload()
    {
        var decoder = new SignedAggregatedAttestationGossipDecoder();
        var payload = new byte[50]; // Too short for fixed part (108 bytes)

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(AggregatedAttestationGossipDecodeFailure.InvalidSsz));
    }

    [Test]
    public void DecodeAndValidate_RoundTrip_EmptyParticipants()
    {
        var decoder = new SignedAggregatedAttestationGossipDecoder();
        var data = new AttestationData(
            new Slot(1),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)));
        var proof = new AggregatedSignatureProof(
            new AggregationBits(Array.Empty<bool>()),
            new byte[] { 0xDE, 0xAD });
        var original = new SignedAggregatedAttestation(data, proof);
        var payload = SszEncoding.Encode(original);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Attestation!.Proof.Participants.Bits.Count, Is.EqualTo(0));
        Assert.That(result.Attestation.Proof.ProofData, Is.EqualTo(new byte[] { 0xDE, 0xAD }));
    }

    [Test]
    public void DecodeAndValidate_RoundTrip_MultipleParticipants()
    {
        var decoder = new SignedAggregatedAttestationGossipDecoder();
        var data = new AttestationData(
            new Slot(10),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray()), new Slot(9)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0xBB, 32).ToArray()), new Slot(8)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0xCC, 32).ToArray()), new Slot(7)));
        var proof = new AggregatedSignatureProof(
            new AggregationBits(new[] { true, false, true, true, false, true, false, false, true }),
            Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray());
        var original = new SignedAggregatedAttestation(data, proof);
        var payload = SszEncoding.Encode(original);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        var decoded = result.Attestation!;
        Assert.That(decoded.Proof.Participants.Bits.Count, Is.EqualTo(9));
        Assert.That(decoded.Proof.Participants.Bits[0], Is.True);
        Assert.That(decoded.Proof.Participants.Bits[1], Is.False);
        Assert.That(decoded.Proof.Participants.Bits[2], Is.True);
        Assert.That(decoded.Proof.Participants.Bits[8], Is.True);
        Assert.That(decoded.Proof.ProofData, Is.EqualTo(original.Proof.ProofData));
    }

    private static SignedAggregatedAttestation CreateSignedAggregatedAttestation()
    {
        var data = new AttestationData(
            new Slot(5),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x01, 32).ToArray()), new Slot(4)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x02, 32).ToArray()), new Slot(3)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x03, 32).ToArray()), new Slot(2)));
        var proof = new AggregatedSignatureProof(
            new AggregationBits(new[] { true, false, true }),
            new byte[] { 0x10, 0x20, 0x30, 0x40 });
        return new SignedAggregatedAttestation(data, proof);
    }
}
