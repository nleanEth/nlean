namespace Lean.Consensus.Types;

public sealed record AttestationData(Slot Slot, Checkpoint Head, Checkpoint Target, Checkpoint Source)
{
}

public sealed record Attestation(ulong ValidatorId, AttestationData Data)
{
}

public sealed record AggregatedAttestation(AggregationBits AggregationBits, AttestationData Data)
{
}
