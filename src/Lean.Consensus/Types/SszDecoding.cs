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
}
