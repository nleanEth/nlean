namespace Lean.Consensus.Types;

public sealed record AttestationData(Slot Slot, Checkpoint Head, Checkpoint Target, Checkpoint Source)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            SszInterop.HashUInt64(Slot.Value),
            Head.HashTreeRoot(),
            Target.HashTreeRoot(),
            Source.HashTreeRoot());
    }
}

public sealed record Attestation(ValidatorIndex ValidatorId, AttestationData Data)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            SszInterop.HashUInt64(ValidatorId),
            Data.HashTreeRoot());
    }
}

public sealed record SignedAttestation(ValidatorIndex ValidatorId, AttestationData Message, XmssSignature Signature);

public sealed record AggregatedAttestation(AggregationBits AggregationBits, AttestationData Data)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            AggregationBits.HashTreeRoot(),
            Data.HashTreeRoot());
    }
}

public sealed record SignedAggregatedAttestation(AttestationData Data, AggregatedSignatureProof Proof)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            Data.HashTreeRoot(),
            Proof.HashTreeRoot());
    }
}
