namespace Lean.Consensus;

public interface IConsensusService
{
    ulong CurrentSlot { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
