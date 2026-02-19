using System.Buffers.Binary;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class SignedAttestationGossipDecoder
{
    private const int SignedAttestationLength = SszEncoding.UInt64Length + SszEncoding.AttestationDataLength + XmssSignature.Length;

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

        if (payload.Length == SignedAttestationLength &&
            TryDecodeSignedAttestationFixed(payload, out value, out reason))
        {
            return true;
        }

        reason =
            $"SignedAttestation must be exactly {SignedAttestationLength} bytes, got {payload.Length}.";
        return false;
    }

    private static bool TryDecodeSignedAttestationFixed(
        ReadOnlySpan<byte> payload,
        out SignedAttestation value,
        out string reason)
    {
        value = null!;
        reason = string.Empty;

        var validatorId = BinaryPrimitives.ReadUInt64LittleEndian(payload[..SszEncoding.UInt64Length]);
        var dataBytes = payload.Slice(SszEncoding.UInt64Length, SszEncoding.AttestationDataLength);
        if (!TryDecodeAttestationData(dataBytes, out var data, out reason))
        {
            reason = $"Invalid attestation data: {reason}";
            return false;
        }

        try
        {
            var signature = XmssSignature.FromBytes(payload[^XmssSignature.Length..]);
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

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, SszEncoding.UInt64Length));
    }
}
