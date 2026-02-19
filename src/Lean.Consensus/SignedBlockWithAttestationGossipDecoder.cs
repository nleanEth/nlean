using System.Buffers.Binary;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class SignedBlockWithAttestationGossipDecoder
{
    private const int SignedBlockFixedLength = SszEncoding.UInt32Length * 2;
    private const int BlockWithAttestationFixedLength = SszEncoding.UInt32Length + SszEncoding.AttestationLength;
    private const int BlockFixedLength = (SszEncoding.UInt64Length * 2) + (SszEncoding.Bytes32Length * 2) + SszEncoding.UInt32Length;
    private const int BlockBodyFixedLength = SszEncoding.UInt32Length;
    private const int AggregatedAttestationFixedLength = SszEncoding.UInt32Length + SszEncoding.AttestationDataLength;
    private const int AggregatedSignatureProofFixedLength = SszEncoding.UInt32Length * 2;

    public BlockGossipDecodeResult DecodeAndValidate(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return DecodeAndValidate(payload.AsSpan());
    }

    public BlockGossipDecodeResult DecodeAndValidate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return BlockGossipDecodeResult.Fail(
                BlockGossipDecodeFailure.EmptyPayload,
                "Gossip payload must be non-empty.");
        }

        if (!TryDecodeSignedBlockWithAttestation(payload, out var signedBlock, out var reason))
        {
            return BlockGossipDecodeResult.Fail(BlockGossipDecodeFailure.InvalidSsz, reason);
        }

        try
        {
            var messageRootBytes = signedBlock.Message.HashTreeRoot();
            if (messageRootBytes.Length != SszEncoding.Bytes32Length)
            {
                return BlockGossipDecodeResult.Fail(
                    BlockGossipDecodeFailure.MessageRootDerivationFailed,
                    $"Derived block message root length {messageRootBytes.Length} is invalid.");
            }

            var messageRoot = new Bytes32(messageRootBytes);
            return BlockGossipDecodeResult.Success(signedBlock, messageRoot);
        }
        catch (Exception ex)
        {
            return BlockGossipDecodeResult.Fail(
                BlockGossipDecodeFailure.MessageRootDerivationFailed,
                $"Failed to derive block message root: {ex.Message}");
        }
    }

    private static bool TryDecodeSignedBlockWithAttestation(
        ReadOnlySpan<byte> bytes,
        out SignedBlockWithAttestation value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length < SignedBlockFixedLength)
        {
            reason = $"SignedBlockWithAttestation payload is too short: {bytes.Length} bytes.";
            return false;
        }

        var messageOffset = ReadOffset(bytes, 0);
        var signatureOffset = ReadOffset(bytes, SszEncoding.UInt32Length);

        if (messageOffset != SignedBlockFixedLength)
        {
            reason = $"Signed block message offset must be {SignedBlockFixedLength}, got {messageOffset}.";
            return false;
        }

        if (signatureOffset < messageOffset || signatureOffset > bytes.Length)
        {
            reason = $"Signed block signature offset {signatureOffset} is outside payload bounds.";
            return false;
        }

        var messageBytes = bytes.Slice(messageOffset, signatureOffset - messageOffset);
        var signatureBytes = bytes[signatureOffset..];
        if (messageBytes.Length == 0)
        {
            reason = "Signed block message section is empty.";
            return false;
        }

        if (signatureBytes.Length == 0)
        {
            reason = "Signed block signatures section is empty.";
            return false;
        }

        if (!TryDecodeBlockWithAttestation(messageBytes, out var message, out reason))
        {
            reason = $"Invalid signed block message: {reason}";
            return false;
        }

        if (!TryDecodeBlockSignatures(signatureBytes, out var signatures, out reason))
        {
            reason = $"Invalid signed block signatures: {reason}";
            return false;
        }

        value = new SignedBlockWithAttestation(message, signatures);
        return true;
    }

    private static bool TryDecodeBlockWithAttestation(
        ReadOnlySpan<byte> bytes,
        out BlockWithAttestation value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length < BlockWithAttestationFixedLength)
        {
            reason = $"BlockWithAttestation payload is too short: {bytes.Length} bytes.";
            return false;
        }

        var blockOffset = ReadOffset(bytes, 0);
        if (blockOffset != BlockWithAttestationFixedLength)
        {
            reason = $"Block offset must be {BlockWithAttestationFixedLength}, got {blockOffset}.";
            return false;
        }

        if (blockOffset > bytes.Length)
        {
            reason = $"Block offset {blockOffset} exceeds payload length {bytes.Length}.";
            return false;
        }

        var attestationBytes = bytes.Slice(SszEncoding.UInt32Length, SszEncoding.AttestationLength);
        var blockBytes = bytes[blockOffset..];
        if (blockBytes.Length == 0)
        {
            reason = "Block section is empty.";
            return false;
        }

        if (!TryDecodeAttestation(attestationBytes, out var proposerAttestation, out reason))
        {
            reason = $"Invalid proposer attestation: {reason}";
            return false;
        }

        if (!TryDecodeBlock(blockBytes, out var block, out reason))
        {
            reason = $"Invalid block: {reason}";
            return false;
        }

        value = new BlockWithAttestation(block, proposerAttestation);
        return true;
    }

    private static bool TryDecodeBlockSignatures(
        ReadOnlySpan<byte> bytes,
        out BlockSignatures value,
        out string reason)
    {
        return TryDecodeBlockSignaturesContainer(bytes, out value, out reason);
    }

    private static bool TryDecodeBlockSignaturesContainer(
        ReadOnlySpan<byte> bytes,
        out BlockSignatures value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length < SszEncoding.UInt32Length + XmssSignature.Length)
        {
            reason = $"BlockSignatures payload is too short: {bytes.Length} bytes.";
            return false;
        }

        var attestationSignaturesOffset = ReadOffset(bytes, 0);
        if (attestationSignaturesOffset > bytes.Length)
        {
            reason = $"BlockSignatures offset {attestationSignaturesOffset} exceeds payload length {bytes.Length}.";
            return false;
        }

        var proposerSignatureLength = attestationSignaturesOffset - SszEncoding.UInt32Length;
        if (proposerSignatureLength != XmssSignature.Length)
        {
            reason = $"BlockSignatures proposer-signature length must be {XmssSignature.Length}, got {proposerSignatureLength}.";
            return false;
        }

        XmssSignature proposerSignature;
        try
        {
            proposerSignature = XmssSignature.FromBytes(bytes.Slice(SszEncoding.UInt32Length, proposerSignatureLength));
        }
        catch (Exception ex)
        {
            reason = $"Invalid proposer XMSS signature bytes: {ex.Message}";
            return false;
        }

        var attestationSignaturesBytes = bytes[attestationSignaturesOffset..];
        if (!TryDecodeAggregatedSignatureProofList(attestationSignaturesBytes, out var attestationSignatures, out reason))
        {
            reason = $"Invalid attestation signatures list: {reason}";
            return false;
        }

        value = new BlockSignatures(attestationSignatures, proposerSignature);
        return true;
    }

    private static bool TryDecodeBlock(ReadOnlySpan<byte> bytes, out Block value, out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length < BlockFixedLength)
        {
            reason = $"Block payload is too short: {bytes.Length} bytes.";
            return false;
        }

        var bodyOffset = ReadOffset(bytes, (SszEncoding.UInt64Length * 2) + (SszEncoding.Bytes32Length * 2));
        if (bodyOffset != BlockFixedLength)
        {
            reason = $"Block body offset must be {BlockFixedLength}, got {bodyOffset}.";
            return false;
        }

        if (bodyOffset > bytes.Length)
        {
            reason = $"Block body offset {bodyOffset} exceeds payload length {bytes.Length}.";
            return false;
        }

        var slot = new Slot(ReadUInt64(bytes, 0));
        var proposerIndex = ReadUInt64(bytes, SszEncoding.UInt64Length);
        var parentRoot = ReadBytes32(bytes, SszEncoding.UInt64Length * 2);
        var stateRoot = ReadBytes32(bytes, (SszEncoding.UInt64Length * 2) + SszEncoding.Bytes32Length);

        var bodyBytes = bytes[bodyOffset..];
        if (!TryDecodeBlockBody(bodyBytes, out var body, out reason))
        {
            reason = $"Invalid block body: {reason}";
            return false;
        }

        value = new Block(slot, proposerIndex, parentRoot, stateRoot, body);
        return true;
    }

    private static bool TryDecodeBlockBody(ReadOnlySpan<byte> bytes, out BlockBody value, out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length < BlockBodyFixedLength)
        {
            reason = $"BlockBody payload is too short: {bytes.Length} bytes.";
            return false;
        }

        var attestationsOffset = ReadOffset(bytes, 0);
        if (attestationsOffset != BlockBodyFixedLength)
        {
            reason = $"BlockBody attestations offset must be {BlockBodyFixedLength}, got {attestationsOffset}.";
            return false;
        }

        if (attestationsOffset > bytes.Length)
        {
            reason = $"BlockBody attestations offset {attestationsOffset} exceeds payload length {bytes.Length}.";
            return false;
        }

        var attestationsBytes = bytes[attestationsOffset..];
        if (!TryDecodeAggregatedAttestationList(attestationsBytes, out var attestations, out reason))
        {
            reason = $"Invalid aggregated attestations list: {reason}";
            return false;
        }

        value = new BlockBody(attestations);
        return true;
    }

    private static bool TryDecodeAttestation(ReadOnlySpan<byte> bytes, out Attestation value, out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length != SszEncoding.AttestationLength)
        {
            reason = $"Attestation must be exactly {SszEncoding.AttestationLength} bytes, got {bytes.Length}.";
            return false;
        }

        var validatorId = ReadUInt64(bytes, 0);
        var dataBytes = bytes.Slice(SszEncoding.UInt64Length, SszEncoding.AttestationDataLength);
        if (!TryDecodeAttestationData(dataBytes, out var data, out reason))
        {
            reason = $"Invalid attestation data: {reason}";
            return false;
        }

        value = new Attestation(validatorId, data);
        return true;
    }

    private static bool TryDecodeAttestationData(ReadOnlySpan<byte> bytes, out AttestationData value, out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length != SszEncoding.AttestationDataLength)
        {
            reason = $"AttestationData must be exactly {SszEncoding.AttestationDataLength} bytes, got {bytes.Length}.";
            return false;
        }

        var slot = new Slot(ReadUInt64(bytes, 0));
        var headBytes = bytes.Slice(SszEncoding.UInt64Length, SszEncoding.CheckpointLength);
        var targetBytes = bytes.Slice(SszEncoding.UInt64Length + SszEncoding.CheckpointLength, SszEncoding.CheckpointLength);
        var sourceBytes = bytes.Slice(SszEncoding.UInt64Length + (SszEncoding.CheckpointLength * 2), SszEncoding.CheckpointLength);

        if (!TryDecodeCheckpoint(headBytes, out var head, out reason))
        {
            reason = $"Invalid head checkpoint: {reason}";
            return false;
        }

        if (!TryDecodeCheckpoint(targetBytes, out var target, out reason))
        {
            reason = $"Invalid target checkpoint: {reason}";
            return false;
        }

        if (!TryDecodeCheckpoint(sourceBytes, out var source, out reason))
        {
            reason = $"Invalid source checkpoint: {reason}";
            return false;
        }

        value = new AttestationData(slot, head, target, source);
        return true;
    }

    private static bool TryDecodeCheckpoint(ReadOnlySpan<byte> bytes, out Checkpoint value, out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length != SszEncoding.CheckpointLength)
        {
            reason = $"Checkpoint must be exactly {SszEncoding.CheckpointLength} bytes, got {bytes.Length}.";
            return false;
        }

        var root = new Bytes32(bytes[..SszEncoding.Bytes32Length].ToArray());
        var slot = new Slot(ReadUInt64(bytes, SszEncoding.Bytes32Length));
        value = new Checkpoint(root, slot);
        return true;
    }

    private static bool TryDecodeAggregatedAttestationList(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<AggregatedAttestation> value,
        out string reason)
    {
        reason = string.Empty;
        if (bytes.Length == 0)
        {
            value = Array.Empty<AggregatedAttestation>();
            return true;
        }

        if (TryReadVariableListOffsets(bytes, "AggregatedAttestation", out var offsets, out reason))
        {
            var attestations = new List<AggregatedAttestation>(offsets.Length);
            for (var i = 0; i < offsets.Length; i++)
            {
                var start = offsets[i];
                var end = i == offsets.Length - 1 ? bytes.Length : offsets[i + 1];
                if (end < start)
                {
                    value = null!;
                    reason = $"AggregatedAttestation element {i} has invalid slice bounds [{start}, {end}).";
                    return false;
                }

                var elementBytes = bytes.Slice(start, end - start);
                if (!TryDecodeAggregatedAttestation(elementBytes, out var attestation, out reason))
                {
                    value = null!;
                    reason = $"Invalid AggregatedAttestation[{i}]: {reason}";
                    return false;
                }

                attestations.Add(attestation);
            }

            value = attestations;
            return true;
        }
        value = null!;
        return false;
    }

    private static bool TryDecodeAggregatedAttestation(
        ReadOnlySpan<byte> bytes,
        out AggregatedAttestation value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length < AggregatedAttestationFixedLength)
        {
            reason = $"AggregatedAttestation payload is too short: {bytes.Length} bytes.";
            return false;
        }

        var aggregationBitsOffset = ReadOffset(bytes, 0);
        if (aggregationBitsOffset != AggregatedAttestationFixedLength)
        {
            reason = $"AggregatedAttestation aggregation-bits offset must be {AggregatedAttestationFixedLength}, got {aggregationBitsOffset}.";
            return false;
        }

        if (aggregationBitsOffset > bytes.Length)
        {
            reason = $"AggregatedAttestation aggregation-bits offset {aggregationBitsOffset} exceeds payload length {bytes.Length}.";
            return false;
        }

        var attestationDataBytes = bytes.Slice(SszEncoding.UInt32Length, SszEncoding.AttestationDataLength);
        if (!TryDecodeAttestationData(attestationDataBytes, out var data, out reason))
        {
            reason = $"Invalid AggregatedAttestation data: {reason}";
            return false;
        }

        var aggregationBitsBytes = bytes[aggregationBitsOffset..];
        if (!TryDecodeBitlist(aggregationBitsBytes, out var bits, out reason))
        {
            reason = $"Invalid AggregatedAttestation aggregation bits: {reason}";
            return false;
        }

        value = new AggregatedAttestation(new AggregationBits(bits), data);
        return true;
    }

    private static bool TryDecodeAggregatedSignatureProofList(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<AggregatedSignatureProof> value,
        out string reason)
    {
        reason = string.Empty;
        if (bytes.Length == 0)
        {
            value = Array.Empty<AggregatedSignatureProof>();
            return true;
        }

        if (!TryReadVariableListOffsets(bytes, "AggregatedSignatureProof", out var offsets, out reason))
        {
            value = null!;
            return false;
        }

        var proofs = new List<AggregatedSignatureProof>(offsets.Length);
        for (var i = 0; i < offsets.Length; i++)
        {
            var start = offsets[i];
            var end = i == offsets.Length - 1 ? bytes.Length : offsets[i + 1];
            if (end < start)
            {
                value = null!;
                reason = $"AggregatedSignatureProof element {i} has invalid slice bounds [{start}, {end}).";
                return false;
            }

            var elementBytes = bytes.Slice(start, end - start);
            if (!TryDecodeAggregatedSignatureProof(elementBytes, out var proof, out reason))
            {
                value = null!;
                reason = $"Invalid AggregatedSignatureProof[{i}]: {reason}";
                return false;
            }

            proofs.Add(proof);
        }

        value = proofs;
        return true;
    }

    private static bool TryDecodeAggregatedSignatureProof(
        ReadOnlySpan<byte> bytes,
        out AggregatedSignatureProof value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (bytes.Length < AggregatedSignatureProofFixedLength)
        {
            reason = $"AggregatedSignatureProof payload is too short: {bytes.Length} bytes.";
            return false;
        }

        var participantsOffset = ReadOffset(bytes, 0);
        var proofOffset = ReadOffset(bytes, SszEncoding.UInt32Length);

        if (participantsOffset != AggregatedSignatureProofFixedLength)
        {
            reason = $"AggregatedSignatureProof participants offset must be {AggregatedSignatureProofFixedLength}, got {participantsOffset}.";
            return false;
        }

        if (proofOffset < participantsOffset || proofOffset > bytes.Length)
        {
            reason = $"AggregatedSignatureProof proof offset {proofOffset} is outside payload bounds.";
            return false;
        }

        var participantsBytes = bytes.Slice(participantsOffset, proofOffset - participantsOffset);
        var proofData = bytes[proofOffset..];

        if (!TryDecodeBitlist(participantsBytes, out var participants, out reason))
        {
            reason = $"Invalid participants bitlist: {reason}";
            return false;
        }

        value = new AggregatedSignatureProof(new AggregationBits(participants), proofData.ToArray());
        return true;
    }

    private static bool TryReadVariableListOffsets(
        ReadOnlySpan<byte> bytes,
        string elementType,
        out int[] offsets,
        out string reason)
    {
        offsets = Array.Empty<int>();
        reason = string.Empty;

        if (bytes.Length < SszEncoding.UInt32Length)
        {
            reason = $"{elementType} list payload is too short to contain offsets.";
            return false;
        }

        var firstOffset = ReadOffset(bytes, 0);
        if (firstOffset < SszEncoding.UInt32Length)
        {
            reason = $"{elementType} list first offset must be at least {SszEncoding.UInt32Length}, got {firstOffset}.";
            return false;
        }

        if (firstOffset > bytes.Length)
        {
            reason = $"{elementType} list first offset {firstOffset} exceeds payload length {bytes.Length}.";
            return false;
        }

        if (firstOffset % SszEncoding.UInt32Length != 0)
        {
            reason = $"{elementType} list first offset {firstOffset} is not aligned to 4-byte offsets.";
            return false;
        }

        var count = firstOffset / SszEncoding.UInt32Length;
        offsets = new int[count];
        var previous = -1;
        for (var i = 0; i < count; i++)
        {
            var offsetPosition = i * SszEncoding.UInt32Length;
            var offset = ReadOffset(bytes, offsetPosition);
            if (offset < firstOffset)
            {
                reason = $"{elementType} list offset {offset} at index {i} points into fixed section.";
                return false;
            }

            if (offset > bytes.Length)
            {
                reason = $"{elementType} list offset {offset} at index {i} exceeds payload length {bytes.Length}.";
                return false;
            }

            if (previous > offset)
            {
                reason = $"{elementType} list offset {offset} at index {i} is not monotonic.";
                return false;
            }

            previous = offset;
            offsets[i] = offset;
        }

        return true;
    }

    private static bool TryDecodeBitlist(ReadOnlySpan<byte> bytes, out bool[] bits, out string reason)
    {
        bits = Array.Empty<bool>();
        reason = string.Empty;

        if (bytes.Length == 0)
        {
            reason = "Bitlist payload is empty.";
            return false;
        }

        var lastByte = bytes[^1];
        if (lastByte == 0)
        {
            reason = "Bitlist delimiter bit is missing.";
            return false;
        }

        var delimiterBitIndex = HighestSetBitIndex(lastByte);
        var bitCount = ((bytes.Length - 1) * 8) + delimiterBitIndex;
        bits = new bool[bitCount];

        for (var i = 0; i < bitCount; i++)
        {
            bits[i] = (bytes[i / 8] & (1 << (i % 8))) != 0;
        }

        return true;
    }

    private static int HighestSetBitIndex(byte value)
    {
        for (var i = 7; i >= 0; i--)
        {
            if ((value & (1 << i)) != 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int ReadOffset(ReadOnlySpan<byte> bytes, int offset)
    {
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, SszEncoding.UInt32Length));
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, SszEncoding.UInt64Length));
    }

    private static Bytes32 ReadBytes32(ReadOnlySpan<byte> bytes, int offset)
    {
        return new Bytes32(bytes.Slice(offset, SszEncoding.Bytes32Length).ToArray());
    }
}
