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
        var indices = new List<ulong>();
        for (var i = 0; i < _bits.Length; i++)
        {
            if (_bits[i])
            {
                indices.Add((ulong)i);
            }
        }

        if (indices.Count == 0)
        {
            throw new InvalidOperationException("Aggregated attestation must reference at least one validator.");
        }

        return indices;
    }
}
