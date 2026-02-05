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

public sealed record Attestation(ulong ValidatorId, AttestationData Data)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            SszInterop.HashUInt64(ValidatorId),
            Data.HashTreeRoot());
    }
}

public sealed record SignedAttestation(ulong ValidatorId, AttestationData Message, XmssSignature Signature);

public sealed record AggregatedAttestation(AggregationBits AggregationBits, AttestationData Data)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            AggregationBits.HashTreeRoot(),
            Data.HashTreeRoot());
    }
}
