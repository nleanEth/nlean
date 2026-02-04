using System.Collections;
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

    public static byte[] HashBitlist(bool[] bits)
    {
        var bitArray = new BitArray(bits);
        Merkle.Merkleize(out UInt256 root, bitArray, (ulong)bits.Length);
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
}
