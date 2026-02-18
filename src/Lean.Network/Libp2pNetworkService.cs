using Lean.Metrics;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Snappier;
using System.Net.Quic;

namespace Lean.Network;

public sealed class Libp2pNetworkService : INetworkService
{
    private const int BlockRootLength = 32;
    private static readonly TimeSpan StatusProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusProbeMinInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PubsubDialTimeout = TimeSpan.FromSeconds(2);
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
    private readonly object _topicsLock = new();
    private readonly object _bootstrapPeersLock = new();
    private readonly object _blocksByRootPeersLock = new();
    private readonly Dictionary<string, ITopic> _topics = new();
    private readonly Dictionary<string, Multiformats.Address.Multiaddress> _bootstrapPeerAddresses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Multiformats.Address.Multiaddress> _blocksByRootPeerAddresses = new(StringComparer.Ordinal);
    private readonly BlocksByRootPeerSelector _blocksByRootPeerSelector = new();
    private readonly IStatusRpcRouter _statusRpcRouter;
    private readonly object _statusProbeLock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastStatusProbeAtUtc = new(StringComparer.Ordinal);
    private readonly object _bootstrapSessionsLock = new();
    private readonly List<ISession> _bootstrapSessions = new();
    private readonly HashSet<string> _connectedBootstrapPeers = new(StringComparer.Ordinal);
    private ILocalPeer? _peer;
    private Connected? _onConnectedHandler;
    private int _bootstrapReconnectTriggeredAfterSubscribe;

    public Libp2pNetworkService(
        ILogger<Libp2pNetworkService> logger,
        IPeerFactory peerFactory,
        PubsubRouter pubsubRouter,
        MDnsDiscoveryProtocol mdnsDiscovery,
        IStatusRpcRouter statusRpcRouter,
        Libp2pConfig config)
    {
        _logger = logger;
        _peerFactory = peerFactory;
        _pubsubRouter = pubsubRouter;
        _mdnsDiscovery = mdnsDiscovery;
        _statusRpcRouter = statusRpcRouter;
        _config = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _bootstrapReconnectTriggeredAfterSubscribe, 0);

        var identity = Libp2pIdentityFactory.Create(_config);
        _logger.LogInformation("libp2p identity initialized with peer id {PeerId}", identity.PeerId);

        var peer = _peerFactory.Create(identity);
        _onConnectedHandler = OnPeerConnectedAsync;
        peer.OnConnected += _onConnectedHandler;
        _peer = peer;
        InitializeBootstrapPeers();

        var rawListenAddresses = _config.ListenAddresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .ToArray();
        var quicRequested = IsQuicRequested(rawListenAddresses);
        if (quicRequested && !QuicConnection.IsSupported)
        {
            throw new InvalidOperationException(BuildQuicUnsupportedMessage());
        }

        var listenAddresses = rawListenAddresses
            .Select(Multiformats.Address.Multiaddress.Decode)
            .ToArray();

        await peer.StartListenAsync(listenAddresses, cancellationToken);
        var advertisedAddresses = peer.ListenAddresses.ToArray();
        if (advertisedAddresses.Length == 0)
        {
            throw new InvalidOperationException(
                "libp2p started without any advertised listen addresses. " +
                "Check listenAddresses and QUIC runtime dependencies.");
        }

        if (_config.EnableMdns)
        {
            try
            {
                _ = _mdnsDiscovery.StartDiscoveryAsync(advertisedAddresses, token: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start mDNS discovery; continuing without mDNS.");
            }
        }

        if (_config.EnablePubsub)
        {
            await _pubsubRouter.StartAsync(peer, token: cancellationToken);
        }

        await ConnectBootstrapPeersAsync(cancellationToken);

        _logger.LogInformation("libp2p listening on {Addresses}", string.Join(", ", advertisedAddresses));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisconnectBootstrapSessionsAsync();
        Interlocked.Exchange(ref _bootstrapReconnectTriggeredAfterSubscribe, 0);

        lock (_topicsLock)
        {
            _topics.Clear();
        }

        lock (_bootstrapPeersLock)
        {
            _bootstrapPeerAddresses.Clear();
            _connectedBootstrapPeers.Clear();
        }

        lock (_blocksByRootPeersLock)
        {
            _blocksByRootPeerAddresses.Clear();
        }
        lock (_statusProbeLock)
        {
            _lastStatusProbeAtUtc.Clear();
        }

        if (_peer is not null && _onConnectedHandler is not null)
        {
            _peer.OnConnected -= _onConnectedHandler;
            _onConnectedHandler = null;
        }

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

    private async Task DisconnectBootstrapSessionsAsync()
    {
        List<ISession> sessions;
        lock (_bootstrapSessionsLock)
        {
            if (_bootstrapSessions.Count == 0)
            {
                ClearConnectedBootstrapPeers();
                return;
            }

            sessions = new List<ISession>(_bootstrapSessions);
            _bootstrapSessions.Clear();
        }
        ClearConnectedBootstrapPeers();

        foreach (var session in sessions)
        {
            try
            {
                await session.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to disconnect bootstrap session cleanly.");
            }
        }
    }

    public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!_config.EnablePubsub)
        {
            return Task.CompletedTask;
        }

        var topicHandle = GetOrCreateTopic(topic);
        var encodedPayload = EncodeGossipPayload(payload);
        topicHandle.Publish(encodedPayload);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default)
    {
        if (!_config.EnablePubsub)
        {
            return Task.CompletedTask;
        }

        var topicHandle = GetOrCreateTopic(topic, subscribe: true);
        topicHandle.OnMessage += payload =>
        {
            byte[] decodedPayload;
            try
            {
                decodedPayload = DecodeGossipPayload(payload);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Dropped gossip payload on topic {Topic}: invalid snappy payload.",
                    topic);
                return;
            }

            handler(decodedPayload);
        };

        _logger.LogInformation(
            "Registered gossip handler for topic {Topic}. IsSubscribed: {IsSubscribed}",
            topic,
            topicHandle.IsSubscribed);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken = default)
    {
        return await RequestBlockByRootCoreAsync(blockRoot, preferredPeerKey: null, cancellationToken);
    }

    public async Task<byte[]?> RequestBlockByRootAsync(
        ReadOnlyMemory<byte> blockRoot,
        string preferredPeerKey,
        CancellationToken cancellationToken = default)
    {
        return await RequestBlockByRootCoreAsync(blockRoot, string.IsNullOrWhiteSpace(preferredPeerKey) ? null : preferredPeerKey.Trim(), cancellationToken);
    }

    public async Task ProbePeerStatusesAsync(CancellationToken cancellationToken = default)
    {
        if (_peer is null)
        {
            return;
        }

        var candidateAddresses = SnapshotBlocksByRootPeerAddresses();
        foreach (var (peerKey, address) in candidateAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryProbePeerStatusByAddressAsync(peerKey, address, cancellationToken);
        }
    }

    private async Task<byte[]?> RequestBlockByRootCoreAsync(
        ReadOnlyMemory<byte> blockRoot,
        string? preferredPeerKey,
        CancellationToken cancellationToken)
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

        var candidateAddresses = SnapshotBlocksByRootPeerAddresses();
        if (candidateAddresses.Count == 0)
        {
            _logger.LogInformation(
                "blocks-by-root miss. Root={Root}, PreferredPeer={PreferredPeer}, Reason=no-candidates.",
                Convert.ToHexString(blockRoot.Span),
                preferredPeerKey ?? "none");
            RecordBlocksByRootFailure(BlocksByRootFailureNoCandidates);
            RecordBlocksByRootRequest(BlocksByRootMiss);
            return null;
        }

        var peerKeys = BuildRequestOrder(candidateAddresses, preferredPeerKey);
        if (peerKeys.Count == 0)
        {
            _logger.LogInformation(
                "blocks-by-root miss. Root={Root}, PreferredPeer={PreferredPeer}, Reason=no-request-order.",
                Convert.ToHexString(blockRoot.Span),
                preferredPeerKey ?? "none");
            RecordBlocksByRootFailure(BlocksByRootFailureNoCandidates);
            RecordBlocksByRootRequest(BlocksByRootMiss);
            return null;
        }

        var attempted = false;
        var dialFailures = 0;
        var rpcFailures = 0;
        var emptyResponses = 0;
        string? lastAttemptedPeerKey = null;
        foreach (var peerKey in peerKeys)
        {
            if (!candidateAddresses.TryGetValue(peerKey, out var address))
            {
                continue;
            }

            attempted = true;
            lastAttemptedPeerKey = peerKey;
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
                dialFailures++;
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Failure, stopwatch.Elapsed);
                RecordBlocksByRootAttempt(BlocksByRootAttemptDialFailure, stopwatch.Elapsed);
                RecordBlocksByRootFailure(BlocksByRootFailureDialError);
                _logger.LogDebug(ex, "Failed dialing peer {Address} for blocks-by-root request.", peerKey);
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = await session.DialAsync<LeanBlocksByRootProtocol, byte[], byte[]?>(
                    blockRoot.Span.ToArray(),
                    cancellationToken);
                if (payload is { Length: > 0 })
                {
                    stopwatch.Stop();
                    _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Success, stopwatch.Elapsed);
                    RecordBlocksByRootAttempt(BlocksByRootAttemptSuccess, stopwatch.Elapsed);
                    RecordBlocksByRootRequest(BlocksByRootHit);
                    _logger.LogInformation(
                        "blocks-by-root hit. Root={Root}, PreferredPeer={PreferredPeer}, Peer={Peer}, PayloadBytes={PayloadBytes}, Attempts={Attempts}.",
                        Convert.ToHexString(blockRoot.Span),
                        preferredPeerKey ?? "none",
                        peerKey,
                        payload.Length,
                        dialFailures + rpcFailures + emptyResponses + 1);
                    return payload;
                }

                stopwatch.Stop();
                emptyResponses++;
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
                rpcFailures++;
                _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Failure, stopwatch.Elapsed);
                RecordBlocksByRootAttempt(BlocksByRootAttemptRpcFailure, stopwatch.Elapsed);
                RecordBlocksByRootFailure(BlocksByRootFailureRpcError);
                _logger.LogDebug(ex, "Failed blocks-by-root request against peer {Address}.", peerKey);
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
            _logger.LogInformation(
                "blocks-by-root miss. Root={Root}, PreferredPeer={PreferredPeer}, Reason=not-attempted.",
                Convert.ToHexString(blockRoot.Span),
                preferredPeerKey ?? "none");
            RecordBlocksByRootFailure(BlocksByRootFailureNoCandidates);
        }
        else
        {
            _logger.LogInformation(
                "blocks-by-root miss. Root={Root}, PreferredPeer={PreferredPeer}, LastPeer={LastPeer}, DialFailures={DialFailures}, RpcFailures={RpcFailures}, EmptyResponses={EmptyResponses}, CandidateCount={CandidateCount}.",
                Convert.ToHexString(blockRoot.Span),
                preferredPeerKey ?? "none",
                lastAttemptedPeerKey ?? "none",
                dialFailures,
                rpcFailures,
                emptyResponses,
                candidateAddresses.Count);
        }

        RecordBlocksByRootRequest(BlocksByRootMiss);
        return null;
    }

    private ITopic GetOrCreateTopic(string topic, bool subscribe = false)
    {
        ITopic? topicHandle;
        lock (_topicsLock)
        {
            if (!_topics.TryGetValue(topic, out topicHandle))
            {
                topicHandle = _pubsubRouter.GetTopic(topic, subscribe);
                _topics[topic] = topicHandle;
            }
        }

        ArgumentNullException.ThrowIfNull(topicHandle);
        EnsureTopicSubscription(topic, topicHandle, subscribe);
        return topicHandle;
    }

    private void EnsureTopicSubscription(string topic, ITopic topicHandle, bool subscribe)
    {
        if (!subscribe)
        {
            return;
        }

        // Router-level subscribe keeps routing state in sync; topic-level subscribe is retained as a fallback.
        _pubsubRouter.Subscribe(topic);
        if (!topicHandle.IsSubscribed)
        {
            topicHandle.Subscribe();
        }

        _logger.LogInformation(
            "Ensured libp2p gossip subscription for topic {Topic}. IsSubscribed: {IsSubscribed}",
            topic,
            topicHandle.IsSubscribed);
        TriggerBootstrapReconnectAfterSubscription();
    }

    private void TriggerBootstrapReconnectAfterSubscription()
    {
        if (_peer is null || !HasBootstrapPeers())
        {
            return;
        }

        if (Interlocked.Exchange(ref _bootstrapReconnectTriggeredAfterSubscribe, 1) == 1)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    // Force one reconnect after topic subscriptions are in place.
                    // This avoids being stuck on pre-subscription sessions that never join gossip mesh.
                    await DisconnectBootstrapSessionsAsync();
                    await ConnectBootstrapPeersAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Deferred bootstrap reconnect after gossip subscription failed.");
                }
            },
            CancellationToken.None);
    }

    private void InitializeBootstrapPeers()
    {
        lock (_bootstrapPeersLock)
        {
            _bootstrapPeerAddresses.Clear();
            _connectedBootstrapPeers.Clear();
        }

        lock (_blocksByRootPeersLock)
        {
            _blocksByRootPeerAddresses.Clear();
        }

        foreach (var bootstrapPeer in _config.BootstrapPeers)
        {
            if (string.IsNullOrWhiteSpace(bootstrapPeer))
            {
                continue;
            }

            try
            {
                var address = Multiformats.Address.Multiaddress.Decode(bootstrapPeer);
                lock (_bootstrapPeersLock)
                {
                    _bootstrapPeerAddresses[bootstrapPeer] = address;
                }

                RegisterBlocksByRootCandidate(bootstrapPeer, address);
            }
            catch (Exception ex)
            {
                RecordBlocksByRootFailure(BlocksByRootFailureInvalidBootstrapAddress);
                _logger.LogWarning(ex, "Skipping invalid bootstrap peer address: {Address}", bootstrapPeer);
            }
        }
    }

    private async Task ConnectBootstrapPeersAsync(CancellationToken cancellationToken)
    {
        if (_peer is null)
        {
            return;
        }

        var bootstrapPeers = SnapshotBootstrapPeers();
        foreach (var (peerKey, address) in bootstrapPeers)
        {
            if (!TryReserveBootstrapPeerConnection(peerKey))
            {
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await _peer.DialAsync(address, cancellationToken);
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await EnsurePubsubSessionAsync(session, CancellationToken.None);
                        }
                        catch
                        {
                            // Best-effort pubsub stream warmup on bootstrap sessions.
                        }
                    },
                    CancellationToken.None);
                RegisterBlocksByRootCandidate(session.RemoteAddress.ToString(), session.RemoteAddress);
                _blocksByRootPeerSelector.MarkConnected(peerKey);
                lock (_bootstrapSessionsLock)
                {
                    _bootstrapSessions.Add(session);
                }
                _logger.LogInformation("Connected to bootstrap peer {Address}", peerKey);
            }
            catch (Exception ex)
            {
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                ReleaseBootstrapPeerConnection(peerKey);
                _logger.LogWarning(ex, "Failed to connect to bootstrap peer {Address}", peerKey);
            }
        }
    }

    private bool HasBootstrapPeers()
    {
        lock (_bootstrapPeersLock)
        {
            return _bootstrapPeerAddresses.Count > 0;
        }
    }

    private List<KeyValuePair<string, Multiformats.Address.Multiaddress>> SnapshotBootstrapPeers()
    {
        lock (_bootstrapPeersLock)
        {
            return _bootstrapPeerAddresses.ToList();
        }
    }

    private bool TryReserveBootstrapPeerConnection(string peerKey)
    {
        lock (_bootstrapPeersLock)
        {
            return _connectedBootstrapPeers.Add(peerKey);
        }
    }

    private void ReleaseBootstrapPeerConnection(string peerKey)
    {
        lock (_bootstrapPeersLock)
        {
            _connectedBootstrapPeers.Remove(peerKey);
        }
    }

    private void ClearConnectedBootstrapPeers()
    {
        lock (_bootstrapPeersLock)
        {
            _connectedBootstrapPeers.Clear();
        }
    }

    private async Task EnsurePubsubSessionAsync(ISession session, CancellationToken cancellationToken)
    {
        if (!_config.EnablePubsub)
        {
            return;
        }

        if (await TryDialAsync<GossipsubProtocolV12>("gossipsub v1.2")
            || await TryDialAsync<GossipsubProtocolV11>("gossipsub v1.1")
            || await TryDialAsync<GossipsubProtocol>("gossipsub v1.0")
            || await TryDialAsync<FloodsubProtocol>("floodsub"))
        {
            return;
        }

        async Task<bool> TryDialAsync<TProtocol>(string protocolName) where TProtocol : class, ISessionProtocol
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(PubsubDialTimeout);
                await session.DialAsync<TProtocol>(timeoutCts.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "Timed out opening {Protocol} stream for peer {Peer}.",
                    protocolName,
                    session.RemoteAddress);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Failed to open {Protocol} stream for peer {Peer}.",
                    protocolName,
                    session.RemoteAddress);
                return false;
            }
        }
    }

    private Task OnPeerConnectedAsync(ISession session)
    {
        if (session is null)
        {
            return Task.CompletedTask;
        }

        var remoteAddress = session.RemoteAddress;
        var peerKey = remoteAddress.ToString();
        RegisterBlocksByRootCandidate(peerKey, remoteAddress);
        _blocksByRootPeerSelector.MarkConnected(peerKey);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await TryProbePeerStatusAsync(session, peerKey, disconnectAfterProbe: false);
                }
                catch
                {
                    // Best-effort proactive probe; ignore task-level failures.
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task TryProbePeerStatusByAddressAsync(
        string peerKey,
        Multiformats.Address.Multiaddress address,
        CancellationToken cancellationToken)
    {
        if (_peer is null)
        {
            return;
        }

        try
        {
            using var dialTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dialTimeoutCts.CancelAfter(StatusProbeTimeout);
            var session = await _peer.DialAsync(address, dialTimeoutCts.Token);
            await TryProbePeerStatusAsync(session, peerKey, disconnectAfterProbe: false, forceProbe: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Timed out proactive status probe dial for peer {Peer}.", peerKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed proactive status probe dial for peer {Peer}.", peerKey);
        }
    }

    private async Task TryProbePeerStatusAsync(
        ISession session,
        string peerKey,
        bool disconnectAfterProbe,
        bool forceProbe = false)
    {
        if (!TryReserveStatusProbe(peerKey, forceProbe))
        {
            return;
        }

        using var timeoutCts = new CancellationTokenSource(StatusProbeTimeout);
        LeanStatusMessage localStatus;
        try
        {
            localStatus = await _statusRpcRouter.ResolveAsync(timeoutCts.Token);
        }
        catch
        {
            localStatus = LeanStatusMessage.Zero();
        }

        try
        {
            var remoteStatus = await session.DialAsync<LeanStatusProtocol, LeanStatusMessage, LeanStatusMessage>(
                localStatus,
                timeoutCts.Token);
            await _statusRpcRouter.HandlePeerStatusAsync(remoteStatus, peerKey, timeoutCts.Token);
            _logger.LogInformation(
                "Proactive status probe succeeded. Peer: {Peer}, FinalizedSlot: {FinalizedSlot}, HeadSlot: {HeadSlot}",
                peerKey,
                remoteStatus.FinalizedSlot,
                remoteStatus.HeadSlot);
        }
        catch (OperationCanceledException)
        {
            // Timeout/cancel path for best-effort probe.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Proactive status probe failed for peer {Peer}.", peerKey);
        }
        finally
        {
            if (disconnectAfterProbe)
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

    private bool TryReserveStatusProbe(string peerKey, bool forceProbe = false)
    {
        if (!TryNormalizePeerKey(peerKey, out var normalizedPeerKey))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_statusProbeLock)
        {
            if (!forceProbe &&
                _lastStatusProbeAtUtc.TryGetValue(normalizedPeerKey, out var lastProbeAt) &&
                now - lastProbeAt < StatusProbeMinInterval)
            {
                return false;
            }

            _lastStatusProbeAtUtc[normalizedPeerKey] = now;
        }

        return true;
    }

    private void RegisterBlocksByRootCandidate(string peerKey, Multiformats.Address.Multiaddress address)
    {
        if (string.IsNullOrWhiteSpace(peerKey))
        {
            return;
        }

        var normalizedKey = peerKey.Trim();
        lock (_blocksByRootPeersLock)
        {
            _blocksByRootPeerAddresses[normalizedKey] = address;
        }

        _blocksByRootPeerSelector.RegisterCandidates(new[] { normalizedKey });
    }

    private Dictionary<string, Multiformats.Address.Multiaddress> SnapshotBlocksByRootPeerAddresses()
    {
        lock (_blocksByRootPeersLock)
        {
            return new Dictionary<string, Multiformats.Address.Multiaddress>(
                _blocksByRootPeerAddresses,
                StringComparer.Ordinal);
        }
    }

    private IReadOnlyList<string> BuildRequestOrder(
        IReadOnlyDictionary<string, Multiformats.Address.Multiaddress> candidateAddresses,
        string? preferredPeerKey)
    {
        var requestOrder = _blocksByRootPeerSelector.GetRequestOrder().ToList();
        if (requestOrder.Count == 0)
        {
            requestOrder.AddRange(candidateAddresses.Keys);
        }

        if (!TryResolvePreferredCandidateKey(candidateAddresses, preferredPeerKey, out var preferredCandidateKey))
        {
            return requestOrder;
        }

        requestOrder.RemoveAll(key => string.Equals(key, preferredCandidateKey, StringComparison.Ordinal));
        requestOrder.Insert(0, preferredCandidateKey);
        return requestOrder;
    }

    private static bool TryResolvePreferredCandidateKey(
        IReadOnlyDictionary<string, Multiformats.Address.Multiaddress> candidateAddresses,
        string? preferredPeerKey,
        out string preferredCandidateKey)
    {
        preferredCandidateKey = string.Empty;
        if (!TryNormalizePeerKey(preferredPeerKey, out var normalizedPreferredPeerKey))
        {
            return false;
        }

        if (candidateAddresses.ContainsKey(normalizedPreferredPeerKey))
        {
            preferredCandidateKey = normalizedPreferredPeerKey;
            return true;
        }

        var preferredPeerId = ExtractPeerId(normalizedPreferredPeerKey);
        if (string.IsNullOrWhiteSpace(preferredPeerId))
        {
            return false;
        }

        foreach (var candidateKey in candidateAddresses.Keys)
        {
            if (string.Equals(ExtractPeerId(candidateKey), preferredPeerId, StringComparison.Ordinal))
            {
                preferredCandidateKey = candidateKey;
                return true;
            }
        }

        return false;
    }

    private static string? ExtractPeerId(string peerKey)
    {
        const string marker = "/p2p/";
        var markerIndex = peerKey.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            var trimmed = peerKey.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        var peerIdStart = markerIndex + marker.Length;
        if (peerIdStart >= peerKey.Length)
        {
            return null;
        }

        return peerKey[peerIdStart..];
    }

    private static bool TryNormalizePeerKey(string? peerKey, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(peerKey))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = peerKey.Trim();
        return true;
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

    private static byte[] EncodeGossipPayload(ReadOnlyMemory<byte> payload)
    {
        // Lean gossip topics are /ssz_snappy, so application payloads must be
        // snappy-compressed before publishing on gossipsub.
        return Snappy.CompressToArray(payload.ToArray());
    }

    private static byte[] DecodeGossipPayload(byte[] payload)
    {
        try
        {
            return Snappy.DecompressToArray(payload);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Invalid snappy gossip payload.", ex);
        }
    }

    private static bool IsQuicRequested(IEnumerable<string> listenAddresses)
    {
        return listenAddresses.Any(address => address.Contains("/quic", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildQuicUnsupportedMessage()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "QUIC is enabled but System.Net.Quic is not supported on this macOS runtime. " +
                   "Install libmsquic and ensure it is visible to the process, for example: " +
                   "export DYLD_LIBRARY_PATH=/opt/homebrew/lib:$DYLD_LIBRARY_PATH";
        }

        return "QUIC is enabled but System.Net.Quic is not supported on this runtime. " +
               "Install libmsquic (version 2+) and retry.";
    }
}
