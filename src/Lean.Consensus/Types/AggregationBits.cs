using System.Linq;
using System.Diagnostics.CodeAnalysis;

namespace Lean.Consensus.Types;

public sealed class AggregationBits
{
    private readonly bool[] _bits;

    public AggregationBits(IEnumerable<bool> bits)
    {
        _bits = bits.ToArray();
    }

    public int Length => _bits.Length;

    public IReadOnlyList<bool> Bits => _bits;

    public static AggregationBits FromValidatorIndices(IEnumerable<ulong> indices)
    {
        var ids = indices.Distinct().Select(id => (long)id).ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Aggregated attestation must reference at least one validator.");
        }

        var maxId = ids.Max();
        if (maxId < 0)
        {
            throw new ArgumentException("Validator index must be non-negative.");
        }

        var bits = new bool[maxId + 1];
        foreach (var id in ids)
        {
            bits[id] = true;
        }

        return new AggregationBits(bits);
    }

    public IReadOnlyList<ulong> ToValidatorIndices()
    {
        if (!TryToValidatorIndices(out var indices))
        {
            throw new InvalidOperationException("Aggregated attestation must reference at least one validator.");
        }

        return indices;
    }

    public bool TryToValidatorIndices([NotNullWhen(true)] out IReadOnlyList<ulong>? indices)
    {
        var values = new List<ulong>();
        for (var i = 0; i < _bits.Length; i++)
        {
            if (_bits[i])
            {
                values.Add((ulong)i);
            }
        }

        if (values.Count == 0)
        {
            indices = null;
            return false;
        }

        indices = values;
        return true;
    }

    public byte[] HashTreeRoot()
    {
        return SszInterop.HashBitlist(_bits, SszEncoding.ValidatorRegistryLimit);
    }
}
