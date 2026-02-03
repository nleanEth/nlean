using System.Security.Cryptography;

namespace Lean.Ssz;

public static class Ssz
{
    public static byte[] HashTreeRoot(ReadOnlySpan<byte> data)
    {
        // Placeholder hash; replace with SSZ merkleization.
        return SHA256.HashData(data);
    }
}
