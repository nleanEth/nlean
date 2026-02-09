using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Lean.Metrics;

namespace Lean.Network;

public sealed class Libp2pNetworkService : INetworkService
{
    private const int BlockRootLength = 32;
    private const string BlocksByRootHit = "hit";
    private const string BlocksByRootMiss = "miss";
    private const string BlocksByRootAttemptSuccess = "success";
    private const string BlocksByRootAttemptDialFailure = "dial_failure";
    private const string BlocksByRootAttemptRpcFailure = "rpc_failure";
    private const string BlocksByRootAttemptEmptyResponse = "empty_response";
    private const string BlocksByRootFailurePeerNotStarted = "peer_not_started";
    private const string BlocksByRootFailureInvalidRootLength = "invalid_root_length";
    private const string BlocksByRootFailureNoCandidates = "no_candidates";
    private const string BlocksByRootFailureInvalidBootstrapAddress = "invalid_bootstrap_address";
    private const string BlocksByRootFailureDialError = "dial_error";
    private const string BlocksByRootFailureRpcError = "rpc_error";
    private const string BlocksByRootFailureEmptyResponse = "empty_response";

    private readonly ILogger<Libp2pNetworkService> _logger;
    private readonly IPeerFactory _peerFactory;
    private readonly PubsubRouter _pubsubRouter;
    private readonly MDnsDiscoveryProtocol _mdnsDiscovery;
    private readonly Libp2pConfig _config;
    private readonly Dictionary<string, ITopic> _topics = new();
    private readonly Dictionary<string, Multiformats.Address.Multiaddress> _bootstrapPeerAddresses = new(StringComparer.Ordinal);
    private readonly BlocksByRootPeerSelector _blocksByRootPeerSelector = new();
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
        InitializeBootstrapPeers();

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

        await ConnectBootstrapPeersAsync(cancellationToken);

        _logger.LogInformation("libp2p listening on {Addresses}", string.Join(", ", peer.ListenAddresses));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _topics.Clear();
        _bootstrapPeerAddresses.Clear();

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

    public async Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken = default)
    {
        if (_peer is null)
        {
            RecordBlocksByRootFailure(BlocksByRootFailurePeerNotStarted);
            RecordBlocksByRootRequest(BlocksByRootMiss);
            return null;
        }

        if (blockRoot.Length != BlockRootLength)
        {
            RecordBlocksByRootFailure(BlocksByRootFailureInvalidRootLength);
            RecordBlocksByRootRequest(BlocksByRootMiss);
            return null;
        }

        var request = new BytesValue { Value = ByteString.CopyFrom(blockRoot.Span) };
        var peerKeys = _blocksByRootPeerSelector.GetRequestOrder();
        if (peerKeys.Count == 0)
        {
            RecordBlocksByRootFailure(BlocksByRootFailureNoCandidates);
            RecordBlocksByRootRequest(BlocksByRootMiss);
            return null;
        }

        var attempted = false;
        foreach (var peerKey in peerKeys)
        {
            if (!_bootstrapPeerAddresses.TryGetValue(peerKey, out var address))
            {
                continue;
            }

            attempted = true;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ISession? session = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                session = await _peer.DialAsync(address, cancellationToken);
                _blocksByRootPeerSelector.MarkConnected(peerKey);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Failure, stopwatch.Elapsed);
                RecordBlocksByRootAttempt(BlocksByRootAttemptDialFailure, stopwatch.Elapsed);
                RecordBlocksByRootFailure(BlocksByRootFailureDialError);
                _logger.LogDebug(ex, "Failed dialing bootstrap peer {Address} for blocks-by-root request.", peerKey);
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await session.DialAsync<RequestResponseProtocol<BytesValue, BytesValue>, BytesValue, BytesValue>(
                    request,
                    cancellationToken);
                var payload = response.Value.ToByteArray();
                if (payload.Length > 0)
                {
                    stopwatch.Stop();
                    _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Success, stopwatch.Elapsed);
                    RecordBlocksByRootAttempt(BlocksByRootAttemptSuccess, stopwatch.Elapsed);
                    RecordBlocksByRootRequest(BlocksByRootHit);
                    return payload;
                }

                stopwatch.Stop();
                _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.EmptyResponse, stopwatch.Elapsed);
                RecordBlocksByRootAttempt(BlocksByRootAttemptEmptyResponse, stopwatch.Elapsed);
                RecordBlocksByRootFailure(BlocksByRootFailureEmptyResponse);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Failure, stopwatch.Elapsed);
                RecordBlocksByRootAttempt(BlocksByRootAttemptRpcFailure, stopwatch.Elapsed);
                RecordBlocksByRootFailure(BlocksByRootFailureRpcError);
                _logger.LogDebug(ex, "Failed blocks-by-root request against bootstrap peer {Address}.", peerKey);
            }
            finally
            {
                if (session is not null)
                {
                    try
                    {
                        await session.DisconnectAsync();
                    }
                    catch
                    {
                        // Ignore disconnect errors.
                    }
                }
            }
        }

        if (!attempted)
        {
            RecordBlocksByRootFailure(BlocksByRootFailureNoCandidates);
        }

        RecordBlocksByRootRequest(BlocksByRootMiss);
        return null;
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

    private void InitializeBootstrapPeers()
    {
        _bootstrapPeerAddresses.Clear();

        foreach (var bootstrapPeer in _config.BootstrapPeers)
        {
            if (string.IsNullOrWhiteSpace(bootstrapPeer))
            {
                continue;
            }

            try
            {
                _bootstrapPeerAddresses[bootstrapPeer] = Multiformats.Address.Multiaddress.Decode(bootstrapPeer);
            }
            catch (Exception ex)
            {
                RecordBlocksByRootFailure(BlocksByRootFailureInvalidBootstrapAddress);
                _logger.LogWarning(ex, "Skipping invalid bootstrap peer address: {Address}", bootstrapPeer);
            }
        }

        _blocksByRootPeerSelector.RegisterCandidates(_bootstrapPeerAddresses.Keys);
    }

    private async Task ConnectBootstrapPeersAsync(CancellationToken cancellationToken)
    {
        if (_peer is null)
        {
            return;
        }

        foreach (var (peerKey, address) in _bootstrapPeerAddresses)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await _peer.DialAsync(address, cancellationToken);
                _blocksByRootPeerSelector.MarkConnected(peerKey);
                await session.DisconnectAsync();
                _logger.LogInformation("Connected to bootstrap peer {Address}", peerKey);
            }
            catch (Exception ex)
            {
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                _logger.LogWarning(ex, "Failed to connect to bootstrap peer {Address}", peerKey);
            }
        }
    }

    private static void RecordBlocksByRootRequest(string result)
    {
        LeanMetrics.SyncBlocksByRootRequestsTotal.WithLabels(result).Inc();
    }

    private static void RecordBlocksByRootFailure(string reason)
    {
        LeanMetrics.SyncBlocksByRootFailuresTotal.WithLabels(reason).Inc();
    }

    private static void RecordBlocksByRootAttempt(string result, TimeSpan latency)
    {
        LeanMetrics.SyncBlocksByRootAttemptsTotal.WithLabels(result).Inc();
        LeanMetrics.SyncBlocksByRootAttemptLatencySeconds.WithLabels(result).Observe(Math.Max(0d, latency.TotalSeconds));
    }
}
