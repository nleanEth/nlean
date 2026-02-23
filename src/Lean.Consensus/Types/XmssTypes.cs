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
    /// Legacy length constant — kept temporarily for backward compatibility.
    /// </summary>
    public const int Length = 3112;

    // Legacy raw bytes — used when constructed from opaque byte[], will be removed after Task 1.3.
    private readonly byte[]? _legacyBytes;

    /// <summary>
    /// Construct a structured XmssSignature from typed fields.
    /// </summary>
    public XmssSignature(HashTreeOpening path, Randomness rho, HashDigestList hashes)
    {
        Path = path;
        Rho = rho;
        Hashes = hashes;
    }

    /// <summary>
    /// Legacy constructor from opaque bytes — kept temporarily for backward compatibility.
    /// Will be removed once SszDecoding.DecodeXmssSignature is implemented (Task 1.3).
    /// </summary>
    public XmssSignature(byte[] bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"XmssSignature must be exactly {Length} bytes.");
        }

        _legacyBytes = bytes.ToArray();
        // Initialize fields to empty defaults; structured access not available in legacy mode.
        Path = new HashTreeOpening(HashDigestList.Empty());
        Rho = Randomness.Zero();
        Hashes = HashDigestList.Empty();
    }

    public HashTreeOpening Path { get; }
    public Randomness Rho { get; }
    public HashDigestList Hashes { get; }

    /// <summary>
    /// Backward-compatible Bytes property — returns raw bytes if constructed from legacy constructor,
    /// otherwise returns SSZ-encoded bytes.
    /// </summary>
    public ReadOnlySpan<byte> Bytes => _legacyBytes ?? EncodeBytes();

    /// <summary>
    /// Create an XMSS signature from SSZ-encoded bytes.
    /// Currently uses legacy byte[] constructor; will be replaced by SszDecoding in Task 1.3.
    /// </summary>
    public static XmssSignature FromBytes(ReadOnlySpan<byte> bytes)
    {
        // TODO: Task 1.3 will replace this with SszDecoding.DecodeXmssSignature
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"XmssSignature must be exactly {Length} bytes.");
        }

        return new XmssSignature(bytes.ToArray());
    }

    /// <summary>
    /// Create an XMSS signature from wire bytes.
    /// Lean peers encode signatures as SSZ containers.
    /// </summary>
    public static XmssSignature FromWireBytes(ReadOnlySpan<byte> bytes)
    {
        return FromBytes(bytes);
    }

    /// <summary>
    /// Returns an empty XMSS signature with empty siblings, zero rho, and empty hashes.
    /// </summary>
    public static XmssSignature Empty() => new(
        new HashTreeOpening(HashDigestList.Empty()),
        Randomness.Zero(),
        HashDigestList.Empty());

    /// <summary>
    /// SSZ-encode this signature. If constructed from legacy bytes, returns those bytes.
    /// Otherwise encodes as a container with variable-length fields.
    /// Note: Container encoding will be finalized in Task 1.2.
    /// </summary>
    public byte[] EncodeBytes()
    {
        if (_legacyBytes is not null)
        {
            return _legacyBytes.ToArray();
        }

        var pathBytes = SszEncoding.Encode(Path);
        var rhoBytes = SszEncoding.Encode(Rho);
        var hashesBytes = SszEncoding.Encode(Hashes);

        // Container with: offset(path) | rho (fixed) | offset(hashes) | path_data | hashes_data
        var fixedSize = SszEncoding.UInt32Length + SszEncoding.RandomnessLength + SszEncoding.UInt32Length;
        var buffer = new byte[fixedSize + pathBytes.Length + hashesBytes.Length];

        var offset = 0;
        // offset for path (variable-size)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(offset, SszEncoding.UInt32Length), (uint)fixedSize);
        offset += SszEncoding.UInt32Length;

        // rho (fixed-size)
        rhoBytes.CopyTo(buffer.AsSpan(offset, SszEncoding.RandomnessLength));
        offset += SszEncoding.RandomnessLength;

        // offset for hashes (variable-size)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(offset, SszEncoding.UInt32Length), (uint)(fixedSize + pathBytes.Length));
        offset += SszEncoding.UInt32Length;

        // path data
        pathBytes.CopyTo(buffer.AsSpan(fixedSize));

        // hashes data
        hashesBytes.CopyTo(buffer.AsSpan(fixedSize + pathBytes.Length));

        return buffer;
    }

    /// <summary>
    /// SSZ HashTreeRoot: Container with 3 fields (path, rho, hashes).
    /// </summary>
    public byte[] HashTreeRoot() => SszInterop.HashContainer(
        Path.HashTreeRoot(),
        Rho.HashTreeRoot(),
        Hashes.HashTreeRoot());
}
