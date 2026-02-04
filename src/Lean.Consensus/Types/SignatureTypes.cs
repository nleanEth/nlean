namespace Lean.Consensus.Types;

public sealed record AggregatedSignatureProof(AggregationBits Participants, byte[] ProofData)
{
}
