namespace Lean.Consensus.Types;

public sealed record AggregatedSignatureProof(AggregationBits Participants, byte[] ProofData)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            Participants.HashTreeRoot(),
            SszInterop.HashBytes(ProofData));
    }
}
