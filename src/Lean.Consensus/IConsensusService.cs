namespace Lean.Consensus;

public interface IConsensusService
{
    ulong CurrentSlot { get; }
    ulong HeadSlot { get; }
    byte[] HeadRoot { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
