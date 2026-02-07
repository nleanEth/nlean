namespace Lean.Consensus.Types;

public sealed record Config(ulong GenesisTime);

public sealed record Validator(Bytes52 Pubkey, ulong Index);

public sealed record State(
    Config Config,
    Slot Slot,
    BlockHeader LatestBlockHeader,
    Checkpoint LatestJustified,
    Checkpoint LatestFinalized,
    IReadOnlyList<Bytes32> HistoricalBlockHashes,
    IReadOnlyList<bool> JustifiedSlots,
    IReadOnlyList<Validator> Validators,
    IReadOnlyList<Bytes32> JustificationsRoots,
    IReadOnlyList<bool> JustificationsValidators);
