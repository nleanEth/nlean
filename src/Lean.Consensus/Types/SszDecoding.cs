using System.Buffers.Binary;

namespace Lean.Consensus.Types;

public static class SszDecoding
{
    /// <summary>
    /// Decode XmssSignature from SSZ Container bytes.
    /// Layout: [offset_path(4) | rho(28) | offset_hashes(4) | path_data | hashes_data]
    /// </summary>
    public static XmssSignature DecodeXmssSignature(ReadOnlySpan<byte> data)
    {
        var cursor = 0;

        // Read offset_path
        var offsetPath = ReadOffset(data, ref cursor);
        // Read rho (fixed, 7 * 4 = 28 bytes)
        var rho = DecodeRandomness(data.Slice(cursor, SszEncoding.RandomnessLength));
        cursor += SszEncoding.RandomnessLength;
        // Read offset_hashes
        var offsetHashes = ReadOffset(data, ref cursor);

        // Decode path data (from offsetPath to offsetHashes)
        var pathData = data.Slice(offsetPath, offsetHashes - offsetPath);
        var path = DecodeHashTreeOpening(pathData);

        // Decode hashes data (from offsetHashes to end)
        var hashesData = data.Slice(offsetHashes);
        var hashes = DecodeHashDigestList(hashesData);

        return new XmssSignature(path, rho, hashes);
    }

    public static HashTreeOpening DecodeHashTreeOpening(ReadOnlySpan<byte> data)
    {
        var cursor = 0;
        var offsetSiblings = ReadOffset(data, ref cursor);
        var siblingsData = data.Slice(offsetSiblings);
        var siblings = DecodeHashDigestList(siblingsData);
        return new HashTreeOpening(siblings);
    }

    public static HashDigestList DecodeHashDigestList(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return HashDigestList.Empty();

        var elementSize = SszEncoding.HashDigestVectorLength; // 8 * 4 = 32
        var count = data.Length / elementSize;
        var elements = new List<HashDigestVector>(count);

        for (var i = 0; i < count; i++)
        {
            var elementData = data.Slice(i * elementSize, elementSize);
            elements.Add(DecodeHashDigestVector(elementData));
        }

        return new HashDigestList(elements);
    }

    public static HashDigestVector DecodeHashDigestVector(ReadOnlySpan<byte> data)
    {
        var fps = new Fp[HashDigestVector.Length];
        for (var i = 0; i < HashDigestVector.Length; i++)
        {
            fps[i] = new Fp(BinaryPrimitives.ReadUInt32LittleEndian(
                data.Slice(i * Fp.ByteLength, Fp.ByteLength)));
        }

        return new HashDigestVector(fps);
    }

    public static Randomness DecodeRandomness(ReadOnlySpan<byte> data)
    {
        var fps = new Fp[Randomness.Length];
        for (var i = 0; i < Randomness.Length; i++)
        {
            fps[i] = new Fp(BinaryPrimitives.ReadUInt32LittleEndian(
                data.Slice(i * Fp.ByteLength, Fp.ByteLength)));
        }

        return new Randomness(fps);
    }

    private static int ReadOffset(ReadOnlySpan<byte> data, ref int cursor)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor, 4));
        cursor += 4;
        return (int)value;
    }

    public static State DecodeState(ReadOnlySpan<byte> data)
    {
        var cursor = 0;

        var genesisTime = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor, SszEncoding.UInt64Length));
        cursor += SszEncoding.UInt64Length;

        var slot = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor, SszEncoding.UInt64Length));
        cursor += SszEncoding.UInt64Length;

        var latestBlockHeader = DecodeBlockHeader(data.Slice(cursor, SszEncoding.BlockHeaderLength));
        cursor += SszEncoding.BlockHeaderLength;

        var latestJustified = DecodeCheckpoint(data.Slice(cursor, SszEncoding.CheckpointLength));
        cursor += SszEncoding.CheckpointLength;

        var latestFinalized = DecodeCheckpoint(data.Slice(cursor, SszEncoding.CheckpointLength));
        cursor += SszEncoding.CheckpointLength;

        var offsetHistorical = ReadOffset(data, ref cursor);
        var offsetJustifiedSlots = ReadOffset(data, ref cursor);
        var offsetValidators = ReadOffset(data, ref cursor);
        var offsetJustificationsRoots = ReadOffset(data, ref cursor);
        var offsetJustificationsValidators = ReadOffset(data, ref cursor);

        var historicalBlockHashes = DecodeBytes32List(
            data.Slice(offsetHistorical, offsetJustifiedSlots - offsetHistorical));

        var justifiedSlots = DecodeBitlist(
            data.Slice(offsetJustifiedSlots, offsetValidators - offsetJustifiedSlots));

        var validators = DecodeValidatorList(
            data.Slice(offsetValidators, offsetJustificationsRoots - offsetValidators));

        var justificationsRoots = DecodeBytes32List(
            data.Slice(offsetJustificationsRoots, offsetJustificationsValidators - offsetJustificationsRoots));

        var justificationsValidators = DecodeBitlist(
            data.Slice(offsetJustificationsValidators));

        return new State(
            new Config(genesisTime),
            new Slot(slot),
            latestBlockHeader,
            latestJustified,
            latestFinalized,
            historicalBlockHashes,
            justifiedSlots,
            validators,
            justificationsRoots,
            justificationsValidators);
    }

    private static BlockHeader DecodeBlockHeader(ReadOnlySpan<byte> data)
    {
        var cursor = 0;
        var headerSlot = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor, SszEncoding.UInt64Length));
        cursor += SszEncoding.UInt64Length;
        var proposerIndex = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor, SszEncoding.UInt64Length));
        cursor += SszEncoding.UInt64Length;
        var parentRoot = new Bytes32(data.Slice(cursor, SszEncoding.Bytes32Length).ToArray());
        cursor += SszEncoding.Bytes32Length;
        var stateRoot = new Bytes32(data.Slice(cursor, SszEncoding.Bytes32Length).ToArray());
        cursor += SszEncoding.Bytes32Length;
        var bodyRoot = new Bytes32(data.Slice(cursor, SszEncoding.Bytes32Length).ToArray());

        return new BlockHeader(new Slot(headerSlot), proposerIndex, parentRoot, stateRoot, bodyRoot);
    }

    private static Checkpoint DecodeCheckpoint(ReadOnlySpan<byte> data)
    {
        var root = new Bytes32(data.Slice(0, SszEncoding.Bytes32Length).ToArray());
        var checkpointSlot = BinaryPrimitives.ReadUInt64LittleEndian(
            data.Slice(SszEncoding.Bytes32Length, SszEncoding.UInt64Length));
        return new Checkpoint(root, new Slot(checkpointSlot));
    }

    private static List<Bytes32> DecodeBytes32List(ReadOnlySpan<byte> data)
    {
        var count = data.Length / SszEncoding.Bytes32Length;
        var result = new List<Bytes32>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(new Bytes32(data.Slice(i * SszEncoding.Bytes32Length, SszEncoding.Bytes32Length).ToArray()));
        }

        return result;
    }

    private static List<Validator> DecodeValidatorList(ReadOnlySpan<byte> data)
    {
        var count = data.Length / SszEncoding.ValidatorLength;
        var result = new List<Validator>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = i * SszEncoding.ValidatorLength;
            var pubkey = new Bytes52(data.Slice(offset, SszEncoding.Bytes52Length).ToArray());
            var index = BinaryPrimitives.ReadUInt64LittleEndian(
                data.Slice(offset + SszEncoding.Bytes52Length, SszEncoding.UInt64Length));
            result.Add(new Validator(pubkey, index));
        }

        return result;
    }

    public static bool[] DecodeBitlist(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return Array.Empty<bool>();

        // Find the delimiter bit (highest set bit in the last byte).
        var lastByte = data[^1];
        if (lastByte == 0)
            return Array.Empty<bool>();

        var lastByteBits = 0;
        var tmp = lastByte;
        while (tmp > 0)
        {
            lastByteBits++;
            tmp >>= 1;
        }

        // The delimiter is the highest set bit; actual bits are below it.
        var totalBits = (data.Length - 1) * 8 + (lastByteBits - 1);
        var bits = new bool[totalBits];
        for (var i = 0; i < totalBits; i++)
        {
            bits[i] = (data[i / 8] & (1 << (i % 8))) != 0;
        }

        return bits;
    }
}
