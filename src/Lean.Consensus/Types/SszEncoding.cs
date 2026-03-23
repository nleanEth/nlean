using System.Buffers.Binary;
using System.Linq;
using Nethermind.Serialization.Ssz;

namespace Lean.Consensus.Types;

public static class SszEncoding
{
    public const int Bytes32Length = 32;
    public const int Bytes52Length = 52;
    public const int UInt64Length = 8;
    public const int UInt32Length = 4;
    public const int FpLength = Fp.ByteLength;
    public const int RandomnessLength = Randomness.Length * FpLength;
    public const int HashDigestVectorLength = HashDigestVector.Length * FpLength;
    public const int ValidatorLength = Bytes52Length + Bytes52Length + UInt64Length;
    public const int NodeListLimit = 1 << 18;
    public const int ValidatorRegistryLimit = 1 << 12;
    public const int CheckpointLength = Bytes32Length + UInt64Length;
    public const int AttestationDataLength = UInt64Length + (CheckpointLength * 3);
    public const int AttestationLength = UInt64Length + AttestationDataLength;
    public const int BlockHeaderLength = (UInt64Length * 2) + (Bytes32Length * 3);

    public static byte[] Encode(AttestationData value)
    {
        var buffer = new byte[AttestationDataLength];
        var offset = 0;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.Slot.Value);
        offset += UInt64Length;
        offset = WriteCheckpoint(buffer, offset, value.Head);
        offset = WriteCheckpoint(buffer, offset, value.Target);
        WriteCheckpoint(buffer, offset, value.Source);
        return buffer;
    }

    public static byte[] Encode(Attestation value)
    {
        var buffer = new byte[AttestationLength];
        var offset = 0;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.ValidatorId);
        offset += UInt64Length;
        EncodeInto(buffer, offset, value.Data);
        return buffer;
    }

    public static byte[] Encode(BlockHeader value)
    {
        var buffer = new byte[BlockHeaderLength];
        var offset = 0;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.Slot.Value);
        offset += UInt64Length;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.ProposerIndex);
        offset += UInt64Length;
        offset = WriteBytes32(buffer, offset, value.ParentRoot);
        offset = WriteBytes32(buffer, offset, value.StateRoot);
        WriteBytes32(buffer, offset, value.BodyRoot);
        return buffer;
    }

    public static byte[] Encode(SignedAttestation value)
    {
        var signatureBytes = Encode(value.Signature);
        // XmssSignature is treated as fixed-size (3112 bytes) — no offset field.
        // Layout: ValidatorId(8) + AttestationData(128) + XmssSignature(inline)
        var fixedSize = UInt64Length + AttestationDataLength;
        var buffer = new byte[fixedSize + signatureBytes.Length];
        var offset = 0;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.ValidatorId);
        offset += UInt64Length;
        EncodeInto(buffer, offset, value.Message);
        signatureBytes.CopyTo(buffer.AsSpan(fixedSize));
        return buffer;
    }

    public static byte[] Encode(BlockBody value)
    {
        var attestationsBytes = Encode(value.Attestations);
        var fixedSize = UInt32Length;
        var buffer = new byte[fixedSize + attestationsBytes.Length];
        WriteOffset(buffer, 0, fixedSize);
        attestationsBytes.CopyTo(buffer.AsSpan(fixedSize));
        return buffer;
    }

    public static byte[] Encode(Block value)
    {
        var bodyBytes = Encode(value.Body);
        var fixedSize = UInt64Length + UInt64Length + Bytes32Length + Bytes32Length + UInt32Length;
        var buffer = new byte[fixedSize + bodyBytes.Length];
        var offset = 0;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.Slot.Value);
        offset += UInt64Length;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.ProposerIndex);
        offset += UInt64Length;
        offset = WriteBytes32(buffer, offset, value.ParentRoot);
        offset = WriteBytes32(buffer, offset, value.StateRoot);
        WriteOffset(buffer, offset, fixedSize);
        bodyBytes.CopyTo(buffer.AsSpan(fixedSize));
        return buffer;
    }

    public static byte[] Encode(BlockWithAttestation value)
    {
        var blockBytes = Encode(value.Block);
        var fixedSize = UInt32Length + AttestationLength;
        var buffer = new byte[fixedSize + blockBytes.Length];
        WriteOffset(buffer, 0, fixedSize);
        var attestationOffset = UInt32Length;
        Ssz.Encode(buffer.AsSpan(attestationOffset, UInt64Length), value.ProposerAttestation.ValidatorId);
        attestationOffset += UInt64Length;
        EncodeInto(buffer, attestationOffset, value.ProposerAttestation.Data);
        blockBytes.CopyTo(buffer.AsSpan(fixedSize));
        return buffer;
    }

    public static byte[] Encode(BlockSignatures value)
    {
        var attestationSignaturesBytes = Encode(value.AttestationSignatures);
        var proposerSignatureBytes = Encode(value.ProposerSignature);
        // ProposerSignature (XmssSignature) is fixed-size (inline), AttestationSignatures is variable (offset).
        // Layout: offset_attestation_sigs(4) + proposer_sig(inline) | attestation_sigs_data
        var fixedSize = UInt32Length + proposerSignatureBytes.Length;
        var buffer = new byte[fixedSize + attestationSignaturesBytes.Length];
        WriteOffset(buffer, 0, fixedSize);
        proposerSignatureBytes.CopyTo(buffer.AsSpan(UInt32Length));
        attestationSignaturesBytes.CopyTo(buffer.AsSpan(fixedSize));
        return buffer;
    }

    public static byte[] Encode(SignedBlockWithAttestation value)
    {
        var messageBytes = Encode(value.Message);
        var signatureBytes = Encode(value.Signature);
        var fixedSize = UInt32Length + UInt32Length;
        var buffer = new byte[fixedSize + messageBytes.Length + signatureBytes.Length];
        WriteOffset(buffer, 0, fixedSize);
        WriteOffset(buffer, UInt32Length, fixedSize + messageBytes.Length);
        messageBytes.CopyTo(buffer.AsSpan(fixedSize));
        signatureBytes.CopyTo(buffer.AsSpan(fixedSize + messageBytes.Length));
        return buffer;
    }

    public static byte[] Encode(XmssSignature value)
    {
        // SSZ Container: path (variable), rho (fixed), hashes (variable)
        // Fixed part: offset_path(4) + rho(28) + offset_hashes(4) = 36
        var pathBytes = Encode(value.Path);
        var hashesBytes = Encode(value.Hashes);

        var fixedSize = UInt32Length + RandomnessLength + UInt32Length;
        var buffer = new byte[fixedSize + pathBytes.Length + hashesBytes.Length];

        var dynamicOffset = fixedSize;
        // offset_path
        WriteOffset(buffer, 0, dynamicOffset);
        // rho (fixed, inline)
        EncodeInto(buffer.AsSpan(), UInt32Length, value.Rho);
        // offset_hashes
        dynamicOffset += pathBytes.Length;
        WriteOffset(buffer, UInt32Length + RandomnessLength, dynamicOffset);
        // path data
        pathBytes.CopyTo(buffer.AsSpan(fixedSize));
        // hashes data
        hashesBytes.CopyTo(buffer.AsSpan(fixedSize + pathBytes.Length));

        return buffer;
    }

    public static byte[] Encode(HashTreeOpening value)
    {
        var siblingsBytes = Encode(value.Siblings);
        var fixedSize = UInt32Length;
        var buffer = new byte[fixedSize + siblingsBytes.Length];
        WriteOffset(buffer, 0, fixedSize);
        siblingsBytes.CopyTo(buffer.AsSpan(fixedSize));
        return buffer;
    }

    public static byte[] Encode(HashDigestList value)
    {
        if (value.Elements.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[value.Elements.Count * HashDigestVectorLength];
        var offset = 0;
        foreach (var element in value.Elements)
        {
            EncodeInto(buffer.AsSpan(), offset, element);
            offset += HashDigestVectorLength;
        }

        return buffer;
    }

    public static byte[] Encode(Fp value)
    {
        var buffer = new byte[FpLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value.Value);
        return buffer;
    }

    public static byte[] Encode(AggregatedSignatureProof value)
    {
        var participantsBytes = EncodeBitlist(value.Participants.Bits);
        var proofBytes = EncodeByteList(value.ProofData);
        var fixedSize = UInt32Length + UInt32Length;
        var buffer = new byte[fixedSize + participantsBytes.Length + proofBytes.Length];
        WriteOffset(buffer, 0, fixedSize);
        WriteOffset(buffer, UInt32Length, fixedSize + participantsBytes.Length);
        participantsBytes.CopyTo(buffer.AsSpan(fixedSize));
        proofBytes.CopyTo(buffer.AsSpan(fixedSize + participantsBytes.Length));
        return buffer;
    }

    public static byte[] Encode(IReadOnlyList<AggregatedSignatureProof> signatures)
    {
        if (signatures.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var elements = new List<byte[]>(signatures.Count);
        foreach (var sig in signatures)
            elements.Add(Encode(sig));
        return EncodeVariableSizeList(elements);
    }

    public static byte[] Encode(IReadOnlyList<AggregatedAttestation> attestations)
    {
        if (attestations.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var elements = new List<byte[]>(attestations.Count);
        foreach (var att in attestations)
            elements.Add(Encode(att));
        return EncodeVariableSizeList(elements);
    }

    public static byte[] Encode(SignedAggregatedAttestation value)
    {
        var proofBytes = Encode(value.Proof);
        var fixedSize = AttestationDataLength + UInt32Length;
        var buffer = new byte[fixedSize + proofBytes.Length];
        EncodeInto(buffer, 0, value.Data);
        WriteOffset(buffer, AttestationDataLength, fixedSize);
        proofBytes.CopyTo(buffer.AsSpan(fixedSize));
        return buffer;
    }

    public static byte[] Encode(AggregatedAttestation value)
    {
        var aggregationBytes = EncodeBitlist(value.AggregationBits.Bits);
        var fixedSize = UInt32Length + AttestationDataLength;
        var buffer = new byte[fixedSize + aggregationBytes.Length];
        WriteOffset(buffer, 0, fixedSize);
        EncodeInto(buffer, UInt32Length, value.Data);
        aggregationBytes.CopyTo(buffer.AsSpan(fixedSize));
        return buffer;
    }

    public static byte[] Encode(Config value)
    {
        var buffer = new byte[UInt64Length];
        Ssz.Encode(buffer, value.GenesisTime);
        return buffer;
    }

    public static byte[] Encode(Validator value)
    {
        var buffer = new byte[ValidatorLength];
        value.AttestationPubkey.AsSpan().CopyTo(buffer.AsSpan(0, Bytes52Length));
        value.ProposalPubkey.AsSpan().CopyTo(buffer.AsSpan(Bytes52Length, Bytes52Length));
        Ssz.Encode(buffer.AsSpan(Bytes52Length + Bytes52Length, UInt64Length), value.Index);
        return buffer;
    }

    public static byte[] Encode(State value)
    {
        var historicalBytes = Encode(value.HistoricalBlockHashes);
        var justifiedBytes = EncodeBitlist(value.JustifiedSlots);
        var validatorsBytes = Encode(value.Validators);
        var justificationRootsBytes = Encode(value.JustificationsRoots);
        var justificationValidatorsBytes = EncodeBitlist(value.JustificationsValidators);

        var fixedSize = UInt64Length + UInt64Length + BlockHeaderLength + CheckpointLength + CheckpointLength + (UInt32Length * 5);
        var buffer = new byte[fixedSize + historicalBytes.Length + justifiedBytes.Length + validatorsBytes.Length + justificationRootsBytes.Length + justificationValidatorsBytes.Length];

        var offset = 0;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.Config.GenesisTime);
        offset += UInt64Length;
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.Slot.Value);
        offset += UInt64Length;
        EncodeInto(buffer.AsSpan(), offset, value.LatestBlockHeader);
        offset += BlockHeaderLength;
        EncodeInto(buffer.AsSpan(), offset, value.LatestJustified);
        offset += CheckpointLength;
        EncodeInto(buffer.AsSpan(), offset, value.LatestFinalized);
        offset += CheckpointLength;

        var dynamicOffset = fixedSize;
        WriteOffset(buffer, offset, dynamicOffset);
        offset += UInt32Length;
        dynamicOffset += historicalBytes.Length;
        WriteOffset(buffer, offset, dynamicOffset);
        offset += UInt32Length;
        dynamicOffset += justifiedBytes.Length;
        WriteOffset(buffer, offset, dynamicOffset);
        offset += UInt32Length;
        dynamicOffset += validatorsBytes.Length;
        WriteOffset(buffer, offset, dynamicOffset);
        offset += UInt32Length;
        dynamicOffset += justificationRootsBytes.Length;
        WriteOffset(buffer, offset, dynamicOffset);

        var writeOffset = fixedSize;
        historicalBytes.CopyTo(buffer.AsSpan(writeOffset, historicalBytes.Length));
        writeOffset += historicalBytes.Length;
        justifiedBytes.CopyTo(buffer.AsSpan(writeOffset, justifiedBytes.Length));
        writeOffset += justifiedBytes.Length;
        validatorsBytes.CopyTo(buffer.AsSpan(writeOffset, validatorsBytes.Length));
        writeOffset += validatorsBytes.Length;
        justificationRootsBytes.CopyTo(buffer.AsSpan(writeOffset, justificationRootsBytes.Length));
        writeOffset += justificationRootsBytes.Length;
        justificationValidatorsBytes.CopyTo(buffer.AsSpan(writeOffset, justificationValidatorsBytes.Length));

        return buffer;
    }

    public static byte[] Encode(IReadOnlyList<Bytes32> values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[values.Count * Bytes32Length];
        var offset = 0;
        foreach (var value in values)
        {
            EncodeInto(buffer.AsSpan(), offset, value);
            offset += Bytes32Length;
        }

        return buffer;
    }

    public static byte[] Encode(IReadOnlyList<Validator> values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[values.Count * ValidatorLength];
        var offset = 0;
        foreach (var value in values)
        {
            EncodeInto(buffer.AsSpan(), offset, value);
            offset += ValidatorLength;
        }

        return buffer;
    }

    public static byte[] EncodeBitlist(IReadOnlyList<bool> bits)
    {
        if (bits.Count == 0)
        {
            return new byte[] { 0x01 };
        }

        var byteLen = (bits.Count + 7) / 8;
        var buffer = new byte[byteLen];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                buffer[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        if (bits.Count % 8 == 0)
        {
            var withDelimiter = new byte[buffer.Length + 1];
            buffer.CopyTo(withDelimiter, 0);
            withDelimiter[^1] = 0x01;
            return withDelimiter;
        }

        buffer[bits.Count / 8] |= (byte)(1 << (bits.Count % 8));
        return buffer;
    }

    public static byte[] EncodeByteList(byte[] value)
    {
        return value.Length == 0 ? Array.Empty<byte>() : value;
    }

    private static int WriteBytes32(byte[] buffer, int offset, Bytes32 value)
    {
        Ssz.Encode(buffer.AsSpan(offset, Bytes32Length), value.AsSpan());
        return offset + Bytes32Length;
    }

    private static int WriteCheckpoint(byte[] buffer, int offset, Checkpoint value)
    {
        offset = WriteBytes32(buffer, offset, value.Root);
        Ssz.Encode(buffer.AsSpan(offset, UInt64Length), value.Slot.Value);
        return offset + UInt64Length;
    }

    private static void EncodeInto(byte[] buffer, int offset, AttestationData value)
    {
        var span = buffer.AsSpan(offset, AttestationDataLength);
        var cursor = 0;
        Ssz.Encode(span.Slice(cursor, UInt64Length), value.Slot.Value);
        cursor += UInt64Length;
        cursor = WriteCheckpoint(span, cursor, value.Head);
        cursor = WriteCheckpoint(span, cursor, value.Target);
        WriteCheckpoint(span, cursor, value.Source);
    }

    private static void EncodeInto(Span<byte> buffer, int offset, Bytes32 value)
    {
        Ssz.Encode(buffer.Slice(offset, Bytes32Length), value.AsSpan());
    }

    private static void EncodeInto(Span<byte> buffer, int offset, Checkpoint value)
    {
        WriteCheckpoint(buffer, offset, value);
    }

    private static void EncodeInto(Span<byte> buffer, int offset, BlockHeader value)
    {
        var cursor = offset;
        Ssz.Encode(buffer.Slice(cursor, UInt64Length), value.Slot.Value);
        cursor += UInt64Length;
        Ssz.Encode(buffer.Slice(cursor, UInt64Length), value.ProposerIndex);
        cursor += UInt64Length;
        Ssz.Encode(buffer.Slice(cursor, Bytes32Length), value.ParentRoot.AsSpan());
        cursor += Bytes32Length;
        Ssz.Encode(buffer.Slice(cursor, Bytes32Length), value.StateRoot.AsSpan());
        cursor += Bytes32Length;
        Ssz.Encode(buffer.Slice(cursor, Bytes32Length), value.BodyRoot.AsSpan());
    }

    private static void EncodeInto(Span<byte> buffer, int offset, Fp value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, FpLength), value.Value);
    }

    private static void EncodeInto(Span<byte> buffer, int offset, Randomness value)
    {
        var cursor = offset;
        foreach (var element in value.Elements)
        {
            EncodeInto(buffer, cursor, element);
            cursor += FpLength;
        }
    }

    private static void EncodeInto(Span<byte> buffer, int offset, HashDigestVector value)
    {
        var cursor = offset;
        foreach (var element in value.Elements)
        {
            EncodeInto(buffer, cursor, element);
            cursor += FpLength;
        }
    }

    private static void EncodeInto(Span<byte> buffer, int offset, Validator value)
    {
        value.AttestationPubkey.AsSpan().CopyTo(buffer.Slice(offset, Bytes52Length));
        value.ProposalPubkey.AsSpan().CopyTo(buffer.Slice(offset + Bytes52Length, Bytes52Length));
        Ssz.Encode(buffer.Slice(offset + Bytes52Length + Bytes52Length, UInt64Length), value.Index);
    }

    private static int WriteCheckpoint(Span<byte> buffer, int offset, Checkpoint value)
    {
        Ssz.Encode(buffer.Slice(offset, Bytes32Length), value.Root.AsSpan());
        offset += Bytes32Length;
        Ssz.Encode(buffer.Slice(offset, UInt64Length), value.Slot.Value);
        return offset + UInt64Length;
    }

    private static void WriteOffset(byte[] buffer, int offset, int value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, UInt32Length), (uint)value);
    }

    private static byte[] EncodeVariableSizeList(IReadOnlyList<byte[]> elements)
    {
        if (elements.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var fixedSize = UInt32Length * elements.Count;
        var totalVarSize = 0;
        foreach (var element in elements)
            totalVarSize += element.Length;
        var total = fixedSize + totalVarSize;
        var buffer = new byte[total];

        var offset = fixedSize;
        for (var i = 0; i < elements.Count; i++)
        {
            WriteOffset(buffer, i * UInt32Length, offset);
            offset += elements[i].Length;
        }

        var writeOffset = fixedSize;
        foreach (var element in elements)
        {
            element.CopyTo(buffer.AsSpan(writeOffset, element.Length));
            writeOffset += element.Length;
        }

        return buffer;
    }
}
