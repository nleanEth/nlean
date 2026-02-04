using Lean.Ssz;

namespace Lean.Consensus.Types;

public sealed record AttestationData(Slot Slot, Checkpoint Head, Checkpoint Target, Checkpoint Source)
{
    public byte[] HashTreeRoot()
    {
        return Ssz.HashTreeRootContainer(
            Ssz.HashTreeRootUInt64(Slot.Value),
            Head.HashTreeRoot(),
            Target.HashTreeRoot(),
            Source.HashTreeRoot());
    }
}

public sealed record Attestation(ulong ValidatorId, AttestationData Data)
{
    public byte[] HashTreeRoot()
    {
        return Ssz.HashTreeRootContainer(
            Ssz.HashTreeRootUInt64(ValidatorId),
            Data.HashTreeRoot());
    }
}

public sealed record AggregatedAttestation(AggregationBits AggregationBits, AttestationData Data)
{
    public byte[] HashTreeRoot()
    {
        return Ssz.HashTreeRootContainer(
            AggregationBits.HashTreeRoot(),
            Data.HashTreeRoot());
    }
}
