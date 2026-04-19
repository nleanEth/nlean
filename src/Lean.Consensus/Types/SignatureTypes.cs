namespace Lean.Consensus.Types;

public sealed record AggregatedSignatureProof(AggregationBits Participants, byte[] ProofData)
{
    // leanSpec models proof_data as ByteListMiB (limit = 1 MiB); the SSZ
    // merkleisation must pad to that chunk limit, not just to the actual
    // byte length. Using HashBytes here produced a root that happened to
    // have the right SSZ bytes but the wrong hash_tree_root.
    private const ulong ProofDataByteLimit = 1UL << 20; // 1 MiB, matches ByteListMiB

    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            Participants.HashTreeRoot(),
            SszInterop.HashByteList(ProofData, ProofDataByteLimit));
    }

    public bool Equals(AggregatedSignatureProof? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Participants.Equals(other.Participants) &&
               ProofData.AsSpan().SequenceEqual(other.ProofData.AsSpan());
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Participants);
        foreach (var b in ProofData.AsSpan()[..Math.Min(16, ProofData.Length)])
            hash.Add(b);
        return hash.ToHashCode();
    }
}
