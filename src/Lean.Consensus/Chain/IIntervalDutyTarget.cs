namespace Lean.Consensus.Chain;

public interface IIntervalDutyTarget
{
    Task OnIntervalAsync(ulong slot, int intervalInSlot);
}
