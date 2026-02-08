namespace Lean.Consensus;

public sealed class ConsensusConfig
{
    public int SecondsPerSlot { get; set; } = 12;
    public bool EnableGossipProcessing { get; set; } = true;
    public int MaxOrphanBlocks { get; set; } = 2048;
    public ulong SlotsPerEpoch { get; set; } = 32;
    public ulong MaxValidatorCount { get; set; } = 1_048_576;
}
