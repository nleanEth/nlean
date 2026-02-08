using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed record ForkChoiceNodeState(
    Checkpoint LatestJustified,
    Checkpoint LatestFinalized,
    ulong ValidatorCount);
