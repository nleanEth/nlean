using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Lean.Ssz;

public static class Ssz
{
    public const int BytesPerChunk = 32;

    private static readonly byte[] ZeroChunk = new byte[BytesPerChunk];

    public static byte[] HashTreeRootUInt64(ulong value)
    {
        var chunk = new byte[BytesPerChunk];
        BinaryPrimitives.WriteUInt64LittleEndian(chunk, value);
        return chunk;
    }

    public static byte[] HashTreeRootBytes32(ReadOnlySpan<byte> value)
    {
        if (value.Length != BytesPerChunk)
        {
            throw new ArgumentException("Bytes32 must be exactly 32 bytes.");
        }

        return value.ToArray();
    }

    public static byte[] HashTreeRootBytes(ReadOnlySpan<byte> value)
    {
        var chunks = Chunkify(value);
        var root = Merkleize(chunks);
        return MixInLength(root, (ulong)value.Length);
    }

    public static byte[] HashTreeRootBitlist(ReadOnlySpan<bool> bits)
    {
        var bytes = PackBitlist(bits);
        var chunks = Chunkify(bytes);
        var root = Merkleize(chunks);
        return MixInLength(root, (ulong)bits.Length);
    }

    public static byte[] HashTreeRootList(IReadOnlyList<byte[]> elementRoots, int elementCount)
    {
        var root = Merkleize(elementRoots);
        return MixInLength(root, (ulong)elementCount);
    }

    public static byte[] HashTreeRootContainer(params byte[][] fieldRoots)
    {
        return Merkleize(fieldRoots);
    }

    public static byte[] HashPair(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<byte> buffer = stackalloc byte[BytesPerChunk * 2];
        left.CopyTo(buffer);
        right.CopyTo(buffer.Slice(BytesPerChunk));
        return SHA256.HashData(buffer);
    }

    private static byte[] Merkleize(IReadOnlyList<byte[]> chunks)
    {
        if (chunks.Count == 0)
        {
            return ZeroChunk.ToArray();
        }

        var layer = new List<byte[]>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.Length != BytesPerChunk)
            {
                throw new ArgumentException("SSZ chunk must be 32 bytes.");
            }
            layer.Add(chunk);
        }

        var powerOfTwo = 1;
        while (powerOfTwo < layer.Count)
        {
            powerOfTwo <<= 1;
        }

        while (layer.Count < powerOfTwo)
        {
            layer.Add(ZeroChunk.ToArray());
        }

        while (layer.Count > 1)
        {
            var next = new List<byte[]>(layer.Count / 2);
            for (var i = 0; i < layer.Count; i += 2)
            {
                next.Add(HashPair(layer[i], layer[i + 1]));
            }
            layer = next;
        }

        return layer[0];
    }

    private static byte[] MixInLength(byte[] root, ulong length)
    {
        var lengthChunk = new byte[BytesPerChunk];
        BinaryPrimitives.WriteUInt64LittleEndian(lengthChunk, length);
        return HashPair(root, lengthChunk);
    }

    private static byte[] PackBitlist(ReadOnlySpan<bool> bits)
    {
        var totalBits = bits.Length + 1;
        var byteLen = (totalBits + 7) / 8;
        var bytes = new byte[byteLen];

        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        var delimiterIndex = bits.Length;
        bytes[delimiterIndex / 8] |= (byte)(1 << (delimiterIndex % 8));
        return bytes;
    }

    private static List<byte[]> Chunkify(ReadOnlySpan<byte> value)
    {
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < value.Length; offset += BytesPerChunk)
        {
            var size = Math.Min(BytesPerChunk, value.Length - offset);
            var chunk = new byte[BytesPerChunk];
            value.Slice(offset, size).CopyTo(chunk);
            chunks.Add(chunk);
        }

        if (chunks.Count == 0)
        {
            chunks.Add(ZeroChunk.ToArray());
        }

        return chunks;
    }
}
