using System;
using System.Security.Cryptography;
using System.Linq;
using Lean.Ssz;

namespace Lean.Consensus.Types;

public readonly struct Bytes32 : IEquatable<Bytes32>
{
    private readonly byte[] _bytes;

    public Bytes32(byte[] bytes)
    {
        if (bytes.Length != Ssz.BytesPerChunk)
        {
            throw new ArgumentException("Bytes32 must be exactly 32 bytes.");
        }

        _bytes = bytes.ToArray();
    }

    public static Bytes32 Zero() => new(new byte[Ssz.BytesPerChunk]);

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public byte[] HashTreeRoot() => Ssz.HashTreeRootBytes32(_bytes);

    public bool Equals(Bytes32 other) => _bytes.SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is Bytes32 other && Equals(other);

    public override int GetHashCode()
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(_bytes, hash);
        return BitConverter.ToInt32(hash[..4]);
    }
}
