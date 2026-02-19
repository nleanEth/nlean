namespace Lean.Network;

public interface INetworkService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default);
    Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken = default);
    Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, string preferredPeerKey, CancellationToken cancellationToken = default);
    Task ProbePeerStatusesAsync(CancellationToken cancellationToken = default);
}
