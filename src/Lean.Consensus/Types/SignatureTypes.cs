namespace Lean.Consensus.Types;

public sealed record AggregatedSignatureProof(AggregationBits Participants, byte[] ProofData)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            Participants.HashTreeRoot(),
            SszInterop.HashBytes(ProofData));
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
