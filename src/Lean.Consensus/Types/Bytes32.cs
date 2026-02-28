using System;
using System.Buffers.Binary;
using System.Linq;
namespace Lean.Consensus.Types;

public readonly struct Bytes32 : IEquatable<Bytes32>
{
    private static readonly byte[] ZeroBytes = new byte[32];
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

    public ReadOnlySpan<byte> AsSpan() => _bytes ?? ZeroBytes;

    public byte[] HashTreeRoot() => SszInterop.HashBytes32(_bytes);

    public bool Equals(Bytes32 other) => AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals(object? obj) => obj is Bytes32 other && Equals(other);

    public override int GetHashCode()
    {
        var bytes = AsSpan();
        return HashCode.Combine(
            BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4)));
    }
}
