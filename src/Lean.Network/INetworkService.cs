namespace Lean.Network;

public interface INetworkService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default);
}
