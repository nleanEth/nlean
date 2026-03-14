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

    /// <summary>
    /// SSZ HashTreeRoot: Vector[Fp, 8] — merkleize concatenated Fp values (no mix_in_length).
    /// </summary>
    public byte[] HashTreeRoot()
    {
        var buffer = new byte[Length * Fp.ByteLength];
        var offset = 0;
        foreach (var fp in _elements)
        {
            SszEncoding.Encode(fp).CopyTo(buffer.AsSpan(offset, Fp.ByteLength));
            offset += Fp.ByteLength;
        }

        return SszInterop.HashBytesVector(buffer);
    }
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

    /// <summary>
    /// SSZ HashTreeRoot: List[Vector[Fp, 8], NODE_LIST_LIMIT] — merkleize element roots with limit, mix_in_length.
    /// </summary>
    public byte[] HashTreeRoot()
    {
        var roots = _elements.Select(e => e.HashTreeRoot()).ToList();
        return SszInterop.HashList(roots, (ulong)SszEncoding.NodeListLimit);
    }
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

    /// <summary>
    /// SSZ HashTreeRoot: Vector[Fp, 7] — merkleize concatenated Fp values (no mix_in_length).
    /// </summary>
    public byte[] HashTreeRoot()
    {
        var buffer = new byte[Length * Fp.ByteLength];
        var offset = 0;
        foreach (var fp in _elements)
        {
            SszEncoding.Encode(fp).CopyTo(buffer.AsSpan(offset, Fp.ByteLength));
            offset += Fp.ByteLength;
        }

        return SszInterop.HashBytesVector(buffer);
    }
}

public sealed class HashTreeOpening
{
    public HashTreeOpening(HashDigestList siblings)
    {
        Siblings = siblings;
    }

    public HashDigestList Siblings { get; }

    public byte[] EncodeBytes() => SszEncoding.Encode(this);

    /// <summary>
    /// SSZ HashTreeRoot: Container with 1 field (siblings).
    /// </summary>
    public byte[] HashTreeRoot() => SszInterop.HashContainer(Siblings.HashTreeRoot());
}

/// <summary>
/// XMSS signature represented as an SSZ Container with structured fields:
/// path (HashTreeOpening), rho (Randomness), hashes (HashDigestList).
/// </summary>
public sealed class XmssSignature
{
    /// <summary>
    /// XMSS signature byte length produced by leanSig.
    /// </summary>
    public const int Length = 3112;

    /// <summary>
    /// Construct a structured XmssSignature from typed fields.
    /// </summary>
    public XmssSignature(HashTreeOpening path, Randomness rho, HashDigestList hashes)
    {
        Path = path;
        Rho = rho;
        Hashes = hashes;
    }

    public HashTreeOpening Path { get; }
    public Randomness Rho { get; }
    public HashDigestList Hashes { get; }

    public ReadOnlySpan<byte> Bytes => EncodeBytes();

    /// <summary>
    /// Create an XMSS signature from SSZ-encoded bytes (Container format).
    /// </summary>
    public static XmssSignature FromBytes(ReadOnlySpan<byte> bytes)
    {
        return SszDecoding.DecodeXmssSignature(bytes);
    }

    /// <summary>
    /// Returns an empty XMSS signature with empty siblings, zero rho, and empty hashes.
    /// </summary>
    public static XmssSignature Empty() => new(
        new HashTreeOpening(HashDigestList.Empty()),
        Randomness.Zero(),
        HashDigestList.Empty());

    /// <summary>
    /// SSZ-encode this signature as a container.
    /// </summary>
    public byte[] EncodeBytes()
    {
        return SszEncoding.Encode(this);
    }

    /// <summary>
    /// SSZ HashTreeRoot: Container with 3 fields (path, rho, hashes).
    /// </summary>
    public byte[] HashTreeRoot() => SszInterop.HashContainer(
        Path.HashTreeRoot(),
        Rho.HashTreeRoot(),
        Hashes.HashTreeRoot());
}
