using System;
using System.Security.Cryptography;
using System.Linq;
namespace Lean.Consensus.Types;

public readonly struct Bytes32 : IEquatable<Bytes32>
{
    private readonly byte[] _bytes;

    public Bytes32(byte[] bytes)
    {
        if (bytes.Length != 32)
        {
            throw new ArgumentException("Bytes32 must be exactly 32 bytes.");
        }

        _bytes = bytes.ToArray();
    }

    public static Bytes32 Zero() => new(new byte[32]);

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public bool Equals(Bytes32 other) => _bytes.SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is Bytes32 other && Equals(other);

    public override int GetHashCode()
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(_bytes, hash);
        return BitConverter.ToInt32(hash[..4]);
    }
}
