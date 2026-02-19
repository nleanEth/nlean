using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed record ForkChoiceNodeState(
    Checkpoint LatestJustified,
    Checkpoint LatestFinalized,
    ulong ValidatorCount,
    IReadOnlyDictionary<string, ForkChoiceJustificationVote>? JustificationVotes = null,
    IReadOnlyList<ulong>? JustifiedSlots = null);

public sealed record ForkChoiceJustificationVote(
    ulong TargetSlot,
    IReadOnlyList<ulong> ValidatorIds);
