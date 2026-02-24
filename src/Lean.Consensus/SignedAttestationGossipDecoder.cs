using System.Buffers.Binary;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class SignedAttestationGossipDecoder
{
    // Fixed part: ValidatorId(8) + AttestationData(104) + offset_signature(4) = 116
    private const int SignedAttestationFixedLength = SszEncoding.UInt64Length + SszEncoding.AttestationDataLength + SszEncoding.UInt32Length;

    public AttestationGossipDecodeResult DecodeAndValidate(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return DecodeAndValidate(payload.AsSpan());
    }

    public AttestationGossipDecodeResult DecodeAndValidate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return AttestationGossipDecodeResult.Fail(
                AttestationGossipDecodeFailure.EmptyPayload,
                "Gossip payload must be non-empty.");
        }

        if (!TryDecodeSignedAttestation(payload, out var attestation, out var reason))
        {
            return AttestationGossipDecodeResult.Fail(
                AttestationGossipDecodeFailure.InvalidSsz,
                reason);
        }

        return AttestationGossipDecodeResult.Success(attestation);
    }

    private static bool TryDecodeSignedAttestation(
        ReadOnlySpan<byte> payload,
        out SignedAttestation value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        if (payload.Length < SignedAttestationFixedLength)
        {
            reason = $"SignedAttestation payload is too short: {payload.Length} bytes (minimum {SignedAttestationFixedLength}).";
            return false;
        }

        var validatorId = BinaryPrimitives.ReadUInt64LittleEndian(payload[..SszEncoding.UInt64Length]);
        var dataBytes = payload.Slice(SszEncoding.UInt64Length, SszEncoding.AttestationDataLength);
        if (!TryDecodeAttestationData(dataBytes, out var data, out reason))
        {
            reason = $"Invalid attestation data: {reason}";
            return false;
        }

        var signatureOffset = ReadOffset(payload, SszEncoding.UInt64Length + SszEncoding.AttestationDataLength);
        if (signatureOffset != SignedAttestationFixedLength)
        {
            reason = $"SignedAttestation signature offset must be {SignedAttestationFixedLength}, got {signatureOffset}.";
            return false;
        }

        if (signatureOffset > payload.Length)
        {
            reason = $"SignedAttestation signature offset {signatureOffset} exceeds payload length {payload.Length}.";
            return false;
        }

        var signatureBytes = payload[signatureOffset..];
        try
        {
            var signature = SszDecoding.DecodeXmssSignature(signatureBytes);
            value = new SignedAttestation(validatorId, data, signature);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Invalid XMSS signature bytes: {ex.Message}";
            return false;
        }
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
