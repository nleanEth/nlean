using System.Security.Cryptography;

namespace Lean.Consensus.Types;

public readonly struct Bytes52 : IEquatable<Bytes52>
{
    private readonly byte[] _bytes;

    public Bytes52(byte[] bytes)
    {
        if (bytes.Length != 52)
        {
            throw new ArgumentException("Bytes52 must be exactly 52 bytes.");
        }

        _bytes = bytes.ToArray();
    }

    public static Bytes52 Zero() => new(new byte[52]);

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public bool Equals(Bytes52 other) => _bytes.SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is Bytes52 other && Equals(other);

    public override int GetHashCode()
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(_bytes, hash);
        return BitConverter.ToInt32(hash[..4]);
    }
}
