namespace Lean.Consensus;

public sealed class ConsensusConfig
{
    public int SecondsPerSlot { get; set; } = 12;
    public bool EnableGossipProcessing { get; set; } = true;
}
