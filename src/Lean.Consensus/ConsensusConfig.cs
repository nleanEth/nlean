namespace Lean.Consensus;

public sealed class ConsensusConfig
{
    public int SecondsPerSlot { get; set; } = 4;
    public ulong GenesisTimeUnix { get; set; }
    public bool EnableGossipProcessing { get; set; } = true;
    public int MaxOrphanBlocks { get; set; } = 2048;
    public ulong InitialValidatorCount { get; set; } = 1;
    public IReadOnlyList<string> GenesisValidatorPublicKeys { get; set; } = Array.Empty<string>();
    public int AttestationCommitteeCount { get; set; } = 1;
    public bool IsAggregator { get; set; } = false;
    public IReadOnlySet<ulong> LocalValidatorIds { get; set; } = new HashSet<ulong> { 0 };
}
