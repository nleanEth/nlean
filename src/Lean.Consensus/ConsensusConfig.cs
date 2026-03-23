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
    public IReadOnlySet<ulong> LocalValidatorIds { get; set; } = new HashSet<ulong> { 0 };
}
