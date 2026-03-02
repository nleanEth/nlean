namespace Lean.Network;

public interface INetworkService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default);
    Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken = default);
    Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, string preferredPeerKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches multiple blocks by root using a single QUIC session per peer.
    /// Avoids the per-block dial+disconnect overhead of RequestBlockByRootAsync.
    /// </summary>
    Task<List<(byte[] Root, byte[] Payload)>> RequestBlocksByRootBatchAsync(
        List<byte[]> roots, string? preferredPeerKey, CancellationToken cancellationToken = default);
    Task ProbePeerStatusesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects to bootstrap peers. Must be called after StartAsync and after topics have
    /// been subscribed, so the gossipsub hello includes all topic subscriptions.
    /// </summary>
    Task ConnectToPeersAsync(CancellationToken cancellationToken = default);
}
