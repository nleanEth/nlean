namespace Lean.Consensus;

public sealed class ConsensusConfig
{
    public int SecondsPerSlot { get; set; } = 4;
    public ulong GenesisTimeUnix { get; set; }
    public bool EnableGossipProcessing { get; set; } = true;
    public int MaxOrphanBlocks { get; set; } = 2048;
    public ulong SlotsPerEpoch { get; set; } = 32;
    public ulong InitialValidatorCount { get; set; } = 1;
    public int AttestationTargetLookbackSlots { get; set; } = 3;
    public ulong MaxValidatorCount { get; set; } = 1_048_576;
    public IReadOnlyList<string> GenesisValidatorPublicKeys { get; set; } = Array.Empty<string>();
}
