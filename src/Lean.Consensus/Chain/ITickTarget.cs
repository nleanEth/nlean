namespace Lean.Consensus.Chain;

public interface ITickTarget
{
    void OnTick(ulong slot, int intervalInSlot);
}
