using System.Buffers.Binary;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class BlockGossipDecoderTests
{
    [Test]
    public void DecodeAndValidate_ReturnsSuccess_ForValidSignedBlockPayload()
    {
        var decoder = new SignedBlockWithAttestationGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var payload = SszEncoding.Encode(signedBlock);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.None));
        Assert.That(result.SignedBlock, Is.Not.Null);
        Assert.That(result.SignedBlock!.Message.Block.Slot.Value, Is.EqualTo(signedBlock.Message.Block.Slot.Value));
        Assert.That(result.SignedBlock.Message.Block.Body.Attestations.Count, Is.EqualTo(1));
        Assert.That(result.SignedBlock.Signature.AttestationSignatures.Count, Is.EqualTo(1));

        var expectedMessageRoot = new Bytes32(signedBlock.Message.HashTreeRoot());
        Assert.That(result.BlockMessageRoot, Is.EqualTo(expectedMessageRoot));
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForEmptyPayload()
    {
        var decoder = new SignedBlockWithAttestationGossipDecoder();

        var result = decoder.DecodeAndValidate(Array.Empty<byte>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.EmptyPayload));
        Assert.That(result.Reason, Does.Contain("non-empty"));
        Assert.That(result.SignedBlock, Is.Null);
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForInvalidSignedBlockOffsets()
    {
        var decoder = new SignedBlockWithAttestationGossipDecoder();
        var payload = SszEncoding.Encode(CreateSignedBlock());
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, SszEncoding.UInt32Length), 12);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
        Assert.That(result.Reason, Does.Contain("message offset"));
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForTruncatedPayload()
    {
        var decoder = new SignedBlockWithAttestationGossipDecoder();
        var payload = SszEncoding.Encode(CreateSignedBlock());
        var truncated = payload[..(payload.Length - XmssSignature.Length)];

        var result = decoder.DecodeAndValidate(truncated);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
        Assert.That(result.Reason, Does.Contain("offset").Or.Contain("short"));
    }

    private static SignedBlockWithAttestation CreateSignedBlock()
    {
        var proposerAttestationData = new AttestationData(
            new Slot(11),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)1, 32).ToArray()), new Slot(9)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)2, 32).ToArray()), new Slot(10)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)3, 32).ToArray()), new Slot(8)));

        var aggregatedAttestation = new AggregatedAttestation(
            new AggregationBits(new[] { true, false, true, true }),
            proposerAttestationData);

        var block = new Block(
            new Slot(12),
            7,
            new Bytes32(Enumerable.Repeat((byte)4, 32).ToArray()),
            new Bytes32(Enumerable.Repeat((byte)5, 32).ToArray()),
            new BlockBody(new[] { aggregatedAttestation }));

        var blockWithAttestation = new BlockWithAttestation(
            block,
            new Attestation(42, proposerAttestationData));

        var signatures = new BlockSignatures(
            new[]
            {
                new AggregatedSignatureProof(
                    new AggregationBits(new[] { true, true, false, true }),
                    new byte[] { 0xAA, 0xBB, 0xCC })
            },
            XmssSignature.Empty());

        return new SignedBlockWithAttestation(blockWithAttestation, signatures);
    }
}
