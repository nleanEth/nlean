using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Lean.Consensus.Types;

internal static class SszInterop
{
    public static byte[] HashUInt64(ulong value)
    {
        Merkle.Merkleize(out UInt256 root, value);
        return ToBytes(root);
    }

    public static byte[] HashBytes32(ReadOnlySpan<byte> value)
    {
        Merkle.Merkleize(out UInt256 root, value);
        return ToBytes(root);
    }

    public static byte[] HashBytes(ReadOnlySpan<byte> value)
    {
        Merkle.Merkleize(out UInt256 root, value);
        Merkle.MixIn(ref root, value.Length);
        return ToBytes(root);
    }

    public static byte[] HashBytesVector(ReadOnlySpan<byte> value)
    {
        Merkle.Merkleize(out UInt256 root, value);
        return ToBytes(root);
    }

    public static byte[] HashBitlist(bool[] bits, ulong maxLength)
    {
        var serialized = EncodeBitlist(bits);
        var chunkCount = checked((uint)((maxLength + 255UL) / 256UL));
        Merkle.MerkleizeBits(out UInt256 root, serialized, chunkCount);
        return ToBytes(root);
    }

    public static byte[] HashContainer(params byte[][] fieldRoots)
    {
        var roots = new UInt256[fieldRoots.Length];
        for (var i = 0; i < fieldRoots.Length; i++)
        {
            Merkle.Merkleize(out roots[i], fieldRoots[i]);
        }

        Merkle.Merkleize(out UInt256 root, roots);
        return ToBytes(root);
    }

    public static byte[] HashList(IReadOnlyList<byte[]> elementRoots, ulong maxLength)
    {
        var roots = new UInt256[elementRoots.Count];
        for (var i = 0; i < elementRoots.Count; i++)
        {
            Merkle.Merkleize(out roots[i], elementRoots[i]);
        }

        Merkle.Merkleize(out UInt256 root, roots, maxLength);
        Merkle.MixIn(ref root, elementRoots.Count);
        return ToBytes(root);
    }

    private static byte[] ToBytes(UInt256 value)
    {
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).ToArray();
    }

    private static byte[] EncodeBitlist(IReadOnlyList<bool> bits)
    {
        if (bits.Count == 0)
        {
            return new byte[] { 0x01 };
        }

        var byteLength = (bits.Count + 7) / 8;
        var buffer = new byte[byteLength];
        for (var index = 0; index < bits.Count; index++)
        {
            if (bits[index])
            {
                buffer[index / 8] |= (byte)(1 << (index % 8));
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
}
