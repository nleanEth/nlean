using System.Linq;

namespace Lean.Consensus.Types;

public sealed class HashDigestVector
{
    public const int Length = 8;

    private readonly Fp[] _elements;

    public HashDigestVector(IEnumerable<Fp> elements)
    {
        _elements = elements.ToArray();
        if (_elements.Length != Length)
        {
            throw new ArgumentException($"HashDigestVector must be exactly {Length} elements.");
        }
    }

    public IReadOnlyList<Fp> Elements => _elements;

    public static HashDigestVector Zero() => new(Enumerable.Repeat(Fp.Zero, Length));
}

public sealed class HashDigestList
{
    private readonly List<HashDigestVector> _elements;

    public HashDigestList(IEnumerable<HashDigestVector> elements)
    {
        _elements = elements.ToList();
    }

    public IReadOnlyList<HashDigestVector> Elements => _elements;

    public static HashDigestList Empty() => new(Array.Empty<HashDigestVector>());
}

public sealed class Randomness
{
    public const int Length = 7;

    private readonly Fp[] _elements;

    public Randomness(IEnumerable<Fp> elements)
    {
        _elements = elements.ToArray();
        if (_elements.Length != Length)
        {
            throw new ArgumentException($"Randomness must be exactly {Length} elements.");
        }
    }

    public IReadOnlyList<Fp> Elements => _elements;

    public static Randomness Zero() => new(Enumerable.Repeat(Fp.Zero, Length));
}

public sealed class HashTreeOpening
{
    public HashTreeOpening(HashDigestList siblings)
    {
        Siblings = siblings;
    }

    public HashDigestList Siblings { get; }

    public byte[] EncodeBytes() => SszEncoding.Encode(this);
}

/// <summary>
/// XMSS signature represented as SSZ-encoded fixed-length bytes (3112 bytes).
/// </summary>
public sealed class XmssSignature
{
    public const int Length = 3112;

    private readonly byte[] _bytes;

    /// <summary>
    /// Create an XMSS signature from SSZ-encoded bytes.
    /// The byte length must be exactly 3112 (leansig serialized signature length).
    /// </summary>
    public XmssSignature(byte[] bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"XmssSignature must be exactly {Length} bytes.");
        }

        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// Create an XMSS signature from SSZ-encoded bytes.
    /// </summary>
    public static XmssSignature FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"XmssSignature must be exactly {Length} bytes.");
        }

        return new XmssSignature(bytes.ToArray());
    }

    /// <summary>
    /// Returns a zero-filled XMSS signature (all zeros, length 3112 bytes).
    /// </summary>
    public static XmssSignature Empty() => new(new byte[Length]);

    public byte[] EncodeBytes() => _bytes.ToArray();

    public byte[] HashTreeRoot() => SszInterop.HashBytesVector(_bytes);
}
