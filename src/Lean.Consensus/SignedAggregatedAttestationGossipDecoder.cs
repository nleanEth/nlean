using System.Buffers.Binary;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public enum AggregatedAttestationGossipDecodeFailure
{
    None = 0,
    EmptyPayload = 1,
    InvalidSsz = 2
}

public sealed record AggregatedAttestationGossipDecodeResult
{
    private AggregatedAttestationGossipDecodeResult(
        bool isSuccess,
        SignedAggregatedAttestation? attestation,
        AggregatedAttestationGossipDecodeFailure failure,
        string reason)
    {
        IsSuccess = isSuccess;
        Attestation = attestation;
        Failure = failure;
        Reason = reason;
    }

    public bool IsSuccess { get; }

    public SignedAggregatedAttestation? Attestation { get; }

    public AggregatedAttestationGossipDecodeFailure Failure { get; }

    public string Reason { get; }

    public static AggregatedAttestationGossipDecodeResult Success(SignedAggregatedAttestation attestation)
    {
        return new AggregatedAttestationGossipDecodeResult(
            isSuccess: true,
            attestation: attestation,
            failure: AggregatedAttestationGossipDecodeFailure.None,
            reason: "Payload decoded and validated.");
    }

    public static AggregatedAttestationGossipDecodeResult Fail(AggregatedAttestationGossipDecodeFailure failure, string reason)
    {
        return new AggregatedAttestationGossipDecodeResult(
            isSuccess: false,
            attestation: null,
            failure: failure,
            reason: reason);
    }
}

public sealed class SignedAggregatedAttestationGossipDecoder
{
    // Fixed part: AttestationData(104) + offset_proof(4) = 108
    private const int SignedAggregatedAttestationFixedLength = SszEncoding.AttestationDataLength + SszEncoding.UInt32Length;

    public AggregatedAttestationGossipDecodeResult DecodeAndValidate(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return DecodeAndValidate(payload.AsSpan());
    }

    public AggregatedAttestationGossipDecodeResult DecodeAndValidate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return AggregatedAttestationGossipDecodeResult.Fail(
                AggregatedAttestationGossipDecodeFailure.EmptyPayload,
                "Gossip payload must be non-empty.");
        }

        if (!TryDecode(payload, out var attestation, out var reason))
        {
            return AggregatedAttestationGossipDecodeResult.Fail(
                AggregatedAttestationGossipDecodeFailure.InvalidSsz,
                reason);
        }

        return AggregatedAttestationGossipDecodeResult.Success(attestation);
    }

    private static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out SignedAggregatedAttestation value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (payload.Length < SignedAggregatedAttestationFixedLength)
        {
            reason = $"SignedAggregatedAttestation payload is too short: {payload.Length} bytes (minimum {SignedAggregatedAttestationFixedLength}).";
            return false;
        }

        // Decode AttestationData (fixed 104 bytes)
        var dataBytes = payload[..SszEncoding.AttestationDataLength];
        if (!TryDecodeAttestationData(dataBytes, out var data, out reason))
        {
            reason = $"Invalid attestation data: {reason}";
            return false;
        }

        // Read proof offset
        var proofOffset = ReadOffset(payload, SszEncoding.AttestationDataLength);
        if (proofOffset != SignedAggregatedAttestationFixedLength)
        {
            reason = $"SignedAggregatedAttestation proof offset must be {SignedAggregatedAttestationFixedLength}, got {proofOffset}.";
            return false;
        }

        if (proofOffset > payload.Length)
        {
            reason = $"SignedAggregatedAttestation proof offset {proofOffset} exceeds payload length {payload.Length}.";
            return false;
        }

        // Decode AggregatedSignatureProof
        var proofBytes = payload[proofOffset..];
        if (!TryDecodeAggregatedSignatureProof(proofBytes, out var proof, out reason))
        {
            reason = $"Invalid aggregated signature proof: {reason}";
            return false;
        }

        value = new SignedAggregatedAttestation(data, proof);
        return true;
    }

    private static bool TryDecodeAggregatedSignatureProof(
        ReadOnlySpan<byte> payload,
        out AggregatedSignatureProof value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        // AggregatedSignatureProof layout: [offset_participants(4) | offset_proof_data(4) | participants_bitlist | proof_data_bytes]
        const int proofFixedLength = SszEncoding.UInt32Length + SszEncoding.UInt32Length;
        if (payload.Length < proofFixedLength)
        {
            reason = $"AggregatedSignatureProof payload is too short: {payload.Length} bytes (minimum {proofFixedLength}).";
            return false;
        }

        var offsetParticipants = ReadOffset(payload, 0);
        var offsetProofData = ReadOffset(payload, SszEncoding.UInt32Length);

        if (offsetParticipants != proofFixedLength)
        {
            reason = $"AggregatedSignatureProof participants offset must be {proofFixedLength}, got {offsetParticipants}.";
            return false;
        }

        if (offsetProofData < offsetParticipants || offsetProofData > payload.Length)
        {
            reason = $"AggregatedSignatureProof proof_data offset {offsetProofData} is out of range.";
            return false;
        }

        // Decode participants bitlist
        var participantsBytes = payload.Slice(offsetParticipants, offsetProofData - offsetParticipants);
        var participants = DecodeBitlist(participantsBytes);

        // Decode proof data
        var proofData = payload.Slice(offsetProofData).ToArray();

        value = new AggregatedSignatureProof(participants, proofData);
        return true;
    }

    private static AggregationBits DecodeBitlist(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return new AggregationBits(Array.Empty<bool>());
        }

        // Find the delimiter bit (highest set bit in the last byte)
        var lastByte = bytes[^1];
        if (lastByte == 0)
        {
            // No delimiter bit found — treat as empty
            return new AggregationBits(Array.Empty<bool>());
        }

        // Find position of highest set bit in last byte
        var highBit = 7;
        while (highBit >= 0 && ((lastByte >> highBit) & 1) == 0)
        {
            highBit--;
        }

        // Total number of data bits = (bytes.Length - 1) * 8 + highBit
        var totalBits = ((bytes.Length - 1) * 8) + highBit;
        var bits = new bool[totalBits];
        for (var i = 0; i < totalBits; i++)
        {
            bits[i] = ((bytes[i / 8] >> (i % 8)) & 1) == 1;
        }

        return new AggregationBits(bits);
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

    private static int ReadOffset(ReadOnlySpan<byte> bytes, int offset)
    {
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, SszEncoding.UInt32Length));
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, SszEncoding.UInt64Length));
    }
}
