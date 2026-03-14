using Lean.Consensus.Types;

namespace Lean.Consensus.ForkChoice;

public struct ProtoAttestation
{
    public int Index;
    public ulong Slot;
    public AttestationData? Data;
}

public struct AttestationTracker
{
    public int? AppliedIndex;
    public ProtoAttestation? LatestKnown;
    public ProtoAttestation? LatestNew;
}
