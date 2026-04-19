namespace Lean.Consensus;

public sealed class ConsensusConfig
{
    public int SecondsPerSlot { get; set; } = 4;
    public ulong GenesisTimeUnix { get; set; }
    public bool EnableGossipProcessing { get; set; } = true;
    public int MaxOrphanBlocks { get; set; } = 2048;
    public int MaxConcurrentRecoveries { get; set; } = 2;
    public int MaxRecoveryDepth { get; set; } = 512;
    public int MaxRecoveryAttemptsPerRoot { get; set; } = 3;
    public ulong SlotsPerEpoch { get; set; } = 32;
    public ulong InitialValidatorCount { get; set; } = 1;
    public int AttestationTargetLookbackSlots { get; set; } = 3;
    public ulong MaxValidatorCount { get; set; } = 1_048_576;
    public IReadOnlyList<(string AttestationPubkey, string ProposalPubkey)> GenesisValidatorKeys { get; set; }
        = Array.Empty<(string, string)>();
    public int AttestationCommitteeCount { get; set; } = 1;
    public bool IsAggregator { get; set; } = false;
    public IReadOnlyList<int> AggregateSubnetIds { get; set; } = Array.Empty<int>();
    public IReadOnlySet<ulong> LocalValidatorIds { get; set; } = new HashSet<ulong> { 0 };

    // Amortise ProtoArray.Prune cost AND provide a grace window for in-flight
    // attestations whose source / target / head references a block that is
    // about to fall outside the finalized boundary. When `finalizedIndex` in
    // ProtoArray is below this threshold, UpdateStoreCheckpoints skips the
    // rebuild and leaves the pre-finalized ancestors in place. lighthouse
    // uses 256 for mainnet; 3SF-mini's faster finalization cadence makes a
    // smaller value (≈1 eth epoch) preferable — race window is at most a
    // few slots of gossip delay, so 64 is comfortable without wasting memory.
    public int PruneNodeThreshold { get; set; } = 64;
}
