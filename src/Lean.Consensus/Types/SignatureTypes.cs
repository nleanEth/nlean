using Lean.Ssz;

namespace Lean.Consensus.Types;

public sealed record AggregatedSignatureProof(AggregationBits Participants, byte[] ProofData)
{
    public byte[] HashTreeRoot()
    {
        return Ssz.HashTreeRootContainer(
            Participants.HashTreeRoot(),
            Ssz.HashTreeRootBytes(ProofData));
    }
}
