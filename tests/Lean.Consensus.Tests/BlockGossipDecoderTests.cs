using System.Buffers.Binary;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class BlockGossipDecoderTests
{
    [Test]
    public void DecodeAndValidate_ReturnsSuccess_ForValidSignedBlockPayload()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var payload = SszEncoding.Encode(signedBlock);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.None));
        Assert.That(result.SignedBlock, Is.Not.Null);
        Assert.That(result.SignedBlock!.Block.Slot.Value, Is.EqualTo(signedBlock.Block.Slot.Value));
        Assert.That(result.SignedBlock.Block.Body.Attestations.Count, Is.EqualTo(1));
        Assert.That(result.SignedBlock.Signature.AttestationSignatures.Count, Is.EqualTo(1));

        var expectedMessageRoot = new Bytes32(signedBlock.Block.HashTreeRoot());
        Assert.That(result.BlockMessageRoot, Is.EqualTo(expectedMessageRoot));
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForEmptyPayload()
    {
        var decoder = new SignedBlockGossipDecoder();

        var result = decoder.DecodeAndValidate(Array.Empty<byte>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.EmptyPayload));
        Assert.That(result.Reason, Does.Contain("non-empty"));
        Assert.That(result.SignedBlock, Is.Null);
    }

    [Test]
    public void DecodeAndValidate_ReturnsFailure_ForInvalidSignedBlockOffsets()
    {
        var decoder = new SignedBlockGossipDecoder();
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
        var decoder = new SignedBlockGossipDecoder();
        var payload = SszEncoding.Encode(CreateSignedBlock());
        // Truncate a few bytes from the end to corrupt the signature container
        var truncated = payload[..(payload.Length - 4)];

        var result = decoder.DecodeAndValidate(truncated);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
        Assert.That(result.Reason, Does.Contain("offset").Or.Contain("short").Or.Contain("signature"));
    }

    [Test]
    public void DecodeAndValidate_RejectsLegacySignatureListWithSingleXmssSignature()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlockWithoutAggregatedAttestations();
        var messageBytes = SszEncoding.Encode(signedBlock.Block);
        var legacySignatureListBytes = XmssSignature.Empty().EncodeBytes();

        var payload = BuildSignedBlockPayload(messageBytes, legacySignatureListBytes);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
    }

    [Test]
    public void DecodeAndValidate_RejectsLegacySignatureList_WhenCountMatchesAttestationsPlusProposer()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var messageBytes = SszEncoding.Encode(signedBlock.Block);
        var singleSignatureBytes = XmssSignature.Empty().EncodeBytes();

        var legacySignatureListBytes = new byte[singleSignatureBytes.Length * 2];
        singleSignatureBytes.CopyTo(legacySignatureListBytes.AsSpan(0, singleSignatureBytes.Length));
        singleSignatureBytes.CopyTo(legacySignatureListBytes.AsSpan(singleSignatureBytes.Length, singleSignatureBytes.Length));
        var payload = BuildSignedBlockPayload(messageBytes, legacySignatureListBytes);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
    }

    [Test]
    public void DecodeAndValidate_RejectsDualOffsetBlockSignatures_WithProposerFirstLayout()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var messageBytes = SszEncoding.Encode(signedBlock.Block);
        var dualOffsetSignatures = EncodeDualOffsetSignatures(signedBlock.Signature, attestationSignaturesFirst: false);
        var payload = BuildSignedBlockPayload(messageBytes, dualOffsetSignatures);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
    }

    [Test]
    public void DecodeAndValidate_AcceptsFixedProposerBlockSignaturesLayout()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var messageBytes = SszEncoding.Encode(signedBlock.Block);
        var fixedProposerSignatures = EncodeLegacyFixedProposerSignatures(signedBlock.Signature);
        var payload = BuildSignedBlockPayload(messageBytes, fixedProposerSignatures);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.SignedBlock, Is.Not.Null);
    }

    [Test]
    public void DecodeAndValidate_AcceptsDualOffsetBlockSignatures_WithAttestationsFirstLayout()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var payload = SszEncoding.Encode(signedBlock);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.SignedBlock, Is.Not.Null);
        Assert.That(result.SignedBlock!.Signature.AttestationSignatures.Count, Is.EqualTo(1));
    }

    [Test]
    public void DecodeAndValidate_RejectsLegacySignatureList_WhenCountDoesNotMatchAttestationsPlusProposer()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var messageBytes = SszEncoding.Encode(signedBlock.Block);
        var singleSignatureBytes = XmssSignature.Empty().EncodeBytes();
        var legacySignatureListBytes = new byte[singleSignatureBytes.Length * 3];
        singleSignatureBytes.CopyTo(legacySignatureListBytes.AsSpan(0, singleSignatureBytes.Length));
        singleSignatureBytes.CopyTo(legacySignatureListBytes.AsSpan(singleSignatureBytes.Length, singleSignatureBytes.Length));
        singleSignatureBytes.CopyTo(legacySignatureListBytes.AsSpan(singleSignatureBytes.Length * 2, singleSignatureBytes.Length));
        var payload = BuildSignedBlockPayload(messageBytes, legacySignatureListBytes);

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
    }

    [Test]
    public void DecodeAndValidate_RejectsLegacy3028ProposerSignatureInContainer()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var messageBytes = SszEncoding.Encode(signedBlock.Block);
        const int legacySignatureLength = 3028;
        var signatureContainer = new byte[4 + legacySignatureLength];
        BinaryPrimitives.WriteUInt32LittleEndian(signatureContainer.AsSpan(0, 4), (uint)signatureContainer.Length);

        var payload = new byte[8 + messageBytes.Length + signatureContainer.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)(8 + messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(8));
        signatureContainer.CopyTo(payload.AsSpan(8 + messageBytes.Length));

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
    }

    [Test]
    public void DecodeAndValidate_RejectsLegacyValidatorAttestationListInBlockBody()
    {
        var decoder = new SignedBlockGossipDecoder();
        var signedBlock = CreateSignedBlock();
        var legacyAttestation = new Attestation(1, signedBlock.Block.Body.Attestations[0].Data);
        var payload = BuildPayloadWithLegacyBodyAttestations(signedBlock, new[] { legacyAttestation });

        var result = decoder.DecodeAndValidate(payload);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Failure, Is.EqualTo(BlockGossipDecodeFailure.InvalidSsz));
    }

    private static SignedBlock CreateSignedBlock(XmssSignature? proposerSignature = null)
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

        var signatures = new BlockSignatures(
            new[]
            {
                new AggregatedSignatureProof(
                    new AggregationBits(new[] { true, true, false, true }),
                    new byte[] { 0xAA, 0xBB, 0xCC })
            },
            proposerSignature ?? XmssSignature.Empty());

        return new SignedBlock(block, signatures);
    }

    private static SignedBlock CreateSignedBlockWithoutAggregatedAttestations()
    {
        var block = new Block(
            new Slot(12),
            7,
            new Bytes32(Enumerable.Repeat((byte)4, 32).ToArray()),
            new Bytes32(Enumerable.Repeat((byte)5, 32).ToArray()),
            new BlockBody(Array.Empty<AggregatedAttestation>()));

        return new SignedBlock(
            block,
            new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), XmssSignature.Empty()));
    }

    private static byte[] BuildPayloadWithLegacyBodyAttestations(
        SignedBlock signedBlock,
        IReadOnlyList<Attestation> legacyAttestations)
    {
        var messageBytes = EncodeBlockWithLegacyBody(
            signedBlock.Block,
            legacyAttestations);
        var signatureBytes = SszEncoding.Encode(signedBlock.Signature);
        return BuildSignedBlockPayload(messageBytes, signatureBytes);
    }

    private static byte[] BuildSignedBlockPayload(
        byte[] messageBytes,
        byte[] signatureBytes)
    {
        var payload = new byte[8 + messageBytes.Length + signatureBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)(8 + messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(8));
        signatureBytes.CopyTo(payload.AsSpan(8 + messageBytes.Length));
        return payload;
    }

    private static byte[] EncodeDualOffsetSignatures(
        BlockSignatures signatures,
        bool attestationSignaturesFirst)
    {
        var proposerSignatureBytes = signatures.ProposerSignature.Bytes.ToArray();
        var attestationSignaturesBytes = SszEncoding.Encode(signatures.AttestationSignatures);
        var fixedLength = SszEncoding.UInt32Length * 2;
        var payload = new byte[fixedLength + proposerSignatureBytes.Length + attestationSignaturesBytes.Length];

        if (attestationSignaturesFirst)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, SszEncoding.UInt32Length), (uint)fixedLength);
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(SszEncoding.UInt32Length, SszEncoding.UInt32Length),
                (uint)(fixedLength + attestationSignaturesBytes.Length));
            attestationSignaturesBytes.CopyTo(payload.AsSpan(fixedLength, attestationSignaturesBytes.Length));
            proposerSignatureBytes.CopyTo(
                payload.AsSpan(fixedLength + attestationSignaturesBytes.Length, proposerSignatureBytes.Length));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, SszEncoding.UInt32Length), (uint)fixedLength);
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(SszEncoding.UInt32Length, SszEncoding.UInt32Length),
                (uint)(fixedLength + proposerSignatureBytes.Length));
            proposerSignatureBytes.CopyTo(payload.AsSpan(fixedLength, proposerSignatureBytes.Length));
            attestationSignaturesBytes.CopyTo(
                payload.AsSpan(fixedLength + proposerSignatureBytes.Length, attestationSignaturesBytes.Length));
        }

        return payload;
    }

    private static byte[] EncodeLegacyFixedProposerSignatures(BlockSignatures signatures)
    {
        var proposerSignatureBytes = signatures.ProposerSignature.Bytes.ToArray();
        var attestationSignaturesBytes = SszEncoding.Encode(signatures.AttestationSignatures);
        var attestationOffset = SszEncoding.UInt32Length + proposerSignatureBytes.Length;
        var payload = new byte[attestationOffset + attestationSignaturesBytes.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, SszEncoding.UInt32Length), (uint)attestationOffset);
        proposerSignatureBytes.CopyTo(payload.AsSpan(SszEncoding.UInt32Length, proposerSignatureBytes.Length));
        attestationSignaturesBytes.CopyTo(payload.AsSpan(attestationOffset, attestationSignaturesBytes.Length));

        return payload;
    }

    private static byte[] EncodeBlockWithLegacyBody(
        Block block,
        IReadOnlyList<Attestation> legacyAttestations)
    {
        var blockBodyBytes = EncodeLegacyBlockBody(legacyAttestations);
        var blockFixedLength = (SszEncoding.UInt64Length * 2) + (SszEncoding.Bytes32Length * 2) + SszEncoding.UInt32Length;
        var blockBytes = new byte[blockFixedLength + blockBodyBytes.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(blockBytes.AsSpan(0, SszEncoding.UInt64Length), block.Slot.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(blockBytes.AsSpan(SszEncoding.UInt64Length, SszEncoding.UInt64Length), block.ProposerIndex);
        block.ParentRoot.AsSpan().CopyTo(blockBytes.AsSpan(SszEncoding.UInt64Length * 2, SszEncoding.Bytes32Length));
        block.StateRoot.AsSpan().CopyTo(blockBytes.AsSpan((SszEncoding.UInt64Length * 2) + SszEncoding.Bytes32Length, SszEncoding.Bytes32Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            blockBytes.AsSpan((SszEncoding.UInt64Length * 2) + (SszEncoding.Bytes32Length * 2), SszEncoding.UInt32Length),
            (uint)blockFixedLength);
        blockBodyBytes.CopyTo(blockBytes.AsSpan(blockFixedLength));

        return blockBytes;
    }

    private static byte[] EncodeLegacyBlockBody(IReadOnlyList<Attestation> legacyAttestations)
    {
        var payload = new byte[SszEncoding.UInt32Length + (legacyAttestations.Count * SszEncoding.AttestationLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, SszEncoding.UInt32Length), (uint)SszEncoding.UInt32Length);

        var offset = SszEncoding.UInt32Length;
        foreach (var attestation in legacyAttestations)
        {
            var encoded = SszEncoding.Encode(attestation);
            encoded.CopyTo(payload.AsSpan(offset, encoded.Length));
            offset += encoded.Length;
        }

        return payload;
    }
}
