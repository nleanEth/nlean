using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Lean.Network;

public sealed class Libp2pNetworkService : INetworkService
{
    private readonly ILogger<Libp2pNetworkService> _logger;
    private readonly IPeerFactory _peerFactory;
    private readonly PubsubRouter _pubsubRouter;
    private readonly MDnsDiscoveryProtocol _mdnsDiscovery;
    private readonly Libp2pConfig _config;
    private readonly Dictionary<string, ITopic> _topics = new();
    private ILocalPeer? _peer;

    public Libp2pNetworkService(
        ILogger<Libp2pNetworkService> logger,
        IPeerFactory peerFactory,
        PubsubRouter pubsubRouter,
        MDnsDiscoveryProtocol mdnsDiscovery,
        Libp2pConfig config)
    {
        _logger = logger;
        _peerFactory = peerFactory;
        _pubsubRouter = pubsubRouter;
        _mdnsDiscovery = mdnsDiscovery;
        _config = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var peer = _peerFactory.Create(new Identity());
        _peer = peer;

        var listenAddresses = _config.ListenAddresses
            .Select(Multiformats.Address.Multiaddress.Decode)
            .ToArray();

        await peer.StartListenAsync(listenAddresses, cancellationToken);

        if (_config.EnableMdns)
        {
            _ = _mdnsDiscovery.StartDiscoveryAsync(peer.ListenAddresses, token: cancellationToken);
        }

        if (_config.EnablePubsub)
        {
            await _pubsubRouter.StartAsync(peer, token: cancellationToken);
        }

        await ConnectBootstrapPeersAsync(peer, cancellationToken);

        _logger.LogInformation("libp2p listening on {Addresses}", string.Join(", ", peer.ListenAddresses));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _topics.Clear();

        if (_pubsubRouter is IDisposable pubsubDisposable)
        {
            pubsubDisposable.Dispose();
        }

        if (_mdnsDiscovery is IDisposable mdnsDisposable)
        {
            mdnsDisposable.Dispose();
        }

        if (_peer is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_peer is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _peer = null;
    }

    public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!_config.EnablePubsub)
        {
            return Task.CompletedTask;
        }

        var topicHandle = GetOrCreateTopic(topic);
        topicHandle.Publish(payload.ToArray());
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default)
    {
        if (!_config.EnablePubsub)
        {
            return Task.CompletedTask;
        }

        var topicHandle = GetOrCreateTopic(topic);
        topicHandle.OnMessage += handler;
        return Task.CompletedTask;
    }

    private ITopic GetOrCreateTopic(string topic)
    {
        if (_topics.TryGetValue(topic, out var existing))
        {
            return existing;
        }

        var created = _pubsubRouter.GetTopic(topic);
        _topics[topic] = created;
        return created;
    }

    private async Task ConnectBootstrapPeersAsync(ILocalPeer peer, CancellationToken cancellationToken)
    {
        foreach (var bootstrapPeer in _config.BootstrapPeers)
        {
            Multiformats.Address.Multiaddress address;
            try
            {
                address = Multiformats.Address.Multiaddress.Decode(bootstrapPeer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping invalid bootstrap peer address: {Address}", bootstrapPeer);
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = await peer.DialAsync(address);
                _logger.LogInformation("Connected to bootstrap peer {Address}", bootstrapPeer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to bootstrap peer {Address}", bootstrapPeer);
            }
        }
    }
}
