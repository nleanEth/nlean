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
    private static readonly TimeSpan BlocksByRootPerPeerTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusProbeMinInterval = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan PubsubDialTimeout = TimeSpan.FromSeconds(5);
    private const string ConnectionDirectionInbound = "inbound";
    private const string ConnectionDirectionOutbound = "outbound";
    private const string ConnectionResultSuccess = "success";
    private const string ConnectionResultTimeout = "timeout";
    private const string ConnectionResultError = "error";
    private const string DisconnectionReasonLocalClose = "local_close";
    private const string DisconnectionReasonError = "error";
    private static readonly TimeSpan BootstrapReconnectInterval = TimeSpan.FromSeconds(10);

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
    private readonly object _connectedPeersLock = new();
    private readonly HashSet<string> _connectedPeers = new(StringComparer.Ordinal);
    private readonly object _bootstrapSessionsLock = new();
    private readonly List<ISession> _bootstrapSessions = new();
    private readonly HashSet<string> _connectedBootstrapPeers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pubsubEstablishedPeers = new(StringComparer.Ordinal);
    private readonly object _peerSessionsLock = new();
    private readonly Dictionary<string, ISession> _peerSessions = new(StringComparer.Ordinal);
    private ILocalPeer? _peer;
    private Connected? _onConnectedHandler;
    private int _bootstrapReconnectTriggeredAfterSubscribe;
    private CancellationTokenSource? _bootstrapReconnectLoopCts;

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
        lock (_connectedPeersLock)
        {
            _connectedPeers.Clear();
            LeanMetrics.SetConnectedPeers(0);
        }

        lock (_bootstrapSessionsLock)
        {
            _pubsubEstablishedPeers.Clear();
        }

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

        _logger.LogInformation("libp2p listening on {Addresses}", string.Join(", ", advertisedAddresses));
    }

    public async Task ConnectToPeersAsync(CancellationToken cancellationToken = default)
    {
        await ConnectBootstrapPeersAsync(cancellationToken);
        StartBootstrapReconnectLoop();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        StopBootstrapReconnectLoop();
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

        lock (_bootstrapSessionsLock)
        {
            _pubsubEstablishedPeers.Clear();
        }

        lock (_blocksByRootPeersLock)
        {
            _blocksByRootPeerAddresses.Clear();
        }
        lock (_statusProbeLock)
        {
            _lastStatusProbeAtUtc.Clear();
        }
        lock (_connectedPeersLock)
        {
            _connectedPeers.Clear();
            LeanMetrics.SetConnectedPeers(0);
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
                lock (_peerSessionsLock) { _peerSessions.Clear(); }
                return;
            }

            sessions = new List<ISession>(_bootstrapSessions);
            _bootstrapSessions.Clear();
        }
        ClearConnectedBootstrapPeers();
        lock (_peerSessionsLock) { _peerSessions.Clear(); }

        foreach (var session in sessions)
        {
            var remotePeer = session.RemoteAddress.ToString();
            try
            {
                await session.DisconnectAsync();
                RecordPeerDisconnected(remotePeer, ConnectionDirectionOutbound, DisconnectionReasonLocalClose);
            }
            catch (Exception ex)
            {
                RecordPeerDisconnected(remotePeer, ConnectionDirectionOutbound, DisconnectionReasonError);
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
            _logger.LogWarning("blocks-by-root skip: _peer is null. Root={Root}", Convert.ToHexString(blockRoot.Span));
            return null;
        }

        if (blockRoot.Length != BlockRootLength)
        {
            _logger.LogWarning("blocks-by-root skip: invalid root length {Length}. Root={Root}", blockRoot.Length, Convert.ToHexString(blockRoot.Span));
            return null;
        }

        var candidateAddresses = SnapshotBlocksByRootPeerAddresses();

        // Exclude self from candidates to avoid wasting a request slot on a dial to ourselves.
        var localPeerId = ExtractPeerId(_peer.ListenAddresses.FirstOrDefault()?.ToString() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(localPeerId))
        {
            var selfKeys = candidateAddresses.Keys
                .Where(k => string.Equals(ExtractPeerId(k), localPeerId, StringComparison.Ordinal))
                .ToList();
            foreach (var selfKey in selfKeys)
            {
                candidateAddresses.Remove(selfKey);
            }
        }

        _logger.LogInformation(
            "blocks-by-root starting. Root={Root}, PreferredPeer={PreferredPeer}, CandidateCount={CandidateCount}",
            Convert.ToHexString(blockRoot.Span),
            preferredPeerKey ?? "none",
            candidateAddresses.Count);
        if (candidateAddresses.Count == 0)
        {
            _logger.LogInformation(
                "blocks-by-root miss. Root={Root}, PreferredPeer={PreferredPeer}, Reason=no-candidates.",
                Convert.ToHexString(blockRoot.Span),
                preferredPeerKey ?? "none");
            return null;
        }

        var peerKeys = BuildRequestOrder(candidateAddresses, preferredPeerKey);
        if (peerKeys.Count == 0)
        {
            _logger.LogInformation(
                "blocks-by-root miss. Root={Root}, PreferredPeer={PreferredPeer}, Reason=no-request-order.",
                Convert.ToHexString(blockRoot.Span),
                preferredPeerKey ?? "none");
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
            using var peerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            peerTimeoutCts.CancelAfter(BlocksByRootPerPeerTimeout);
            ISession? session = null;
            var isBootstrapSession = false;
            try
            {
                peerTimeoutCts.Token.ThrowIfCancellationRequested();

                // Try reusing an existing bootstrap session first to avoid fresh dial overhead.
                session = TryFindBootstrapSession(peerKey);
                if (session is not null)
                {
                    isBootstrapSession = true;
                    _logger.LogDebug("blocks-by-root reusing bootstrap session. Peer={Peer}", peerKey);
                }
                else
                {
                    session = await _peer.DialAsync(address, peerTimeoutCts.Token);
                    _blocksByRootPeerSelector.MarkConnected(peerKey);
                    RecordPeerConnected(peerKey, ConnectionDirectionOutbound);
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                dialFailures++;
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Failure, stopwatch.Elapsed);
                RecordPeerConnectionFailure(ConnectionDirectionOutbound, ConnectionResultTimeout);
                _logger.LogDebug(ex, "Timed out dialing peer {Address} for blocks-by-root request. Elapsed: {Elapsed}", peerKey, stopwatch.Elapsed);
                continue;
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
                RecordPeerConnectionFailure(ConnectionDirectionOutbound, MapConnectionFailureResult(ex));
                _logger.LogDebug(ex, "Failed dialing peer {Address} for blocks-by-root request. Elapsed: {Elapsed}", peerKey, stopwatch.Elapsed);
                continue;
            }

            try
            {
                peerTimeoutCts.Token.ThrowIfCancellationRequested();
                var payload = await session.DialAsync<LeanBlocksByRootProtocol, byte[], byte[]?>(
                    blockRoot.Span.ToArray(),
                    peerTimeoutCts.Token);
                if (payload is { Length: > 0 })
                {
                    stopwatch.Stop();
                    _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Success, stopwatch.Elapsed);
                    _logger.LogInformation(
                        "blocks-by-root hit. Root={Root}, PreferredPeer={PreferredPeer}, Peer={Peer}, PayloadBytes={PayloadBytes}, Attempts={Attempts}, Reused={Reused}.",
                        Convert.ToHexString(blockRoot.Span),
                        preferredPeerKey ?? "none",
                        peerKey,
                        payload.Length,
                        dialFailures + rpcFailures + emptyResponses + 1,
                        isBootstrapSession);
                    return payload;
                }

                stopwatch.Stop();
                emptyResponses++;
                _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.EmptyResponse, stopwatch.Elapsed);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                rpcFailures++;
                _blocksByRootPeerSelector.RecordAttempt(peerKey, BlocksByRootPeerAttemptResult.Failure, stopwatch.Elapsed);
                _logger.LogDebug(ex, "Timed out blocks-by-root request against peer {Address}. Elapsed: {Elapsed}", peerKey, stopwatch.Elapsed);
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
                _logger.LogDebug(ex, "Failed blocks-by-root request against peer {Address}. Elapsed: {Elapsed}", peerKey, stopwatch.Elapsed);
            }
            finally
            {
                // Only disconnect sessions we dialed ourselves; never tear down shared bootstrap sessions.
                if (session is not null && !isBootstrapSession)
                {
                    var remotePeer = session.RemoteAddress.ToString();
                    try
                    {
                        using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await session.DisconnectAsync().WaitAsync(disconnectCts.Token);
                        RecordPeerDisconnected(remotePeer, ConnectionDirectionOutbound, DisconnectionReasonLocalClose);
                    }
                    catch
                    {
                        RecordPeerDisconnected(remotePeer, ConnectionDirectionOutbound, DisconnectionReasonError);
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
                    // Open pubsub streams on existing bootstrap sessions so they join the gossip mesh.
                    // Avoids tearing down working sessions — fresh dials may fail with QUIC timeouts.
                    await EnsureBootstrapPubsubSessionsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Deferred bootstrap pubsub warmup after gossip subscription failed.");
                }
            },
            CancellationToken.None);
    }

    private async Task EnsureBootstrapPubsubSessionsAsync()
    {
        List<ISession> sessions;
        lock (_bootstrapSessionsLock)
        {
            if (_bootstrapSessions.Count == 0)
            {
                return;
            }

            sessions = new List<ISession>(_bootstrapSessions);
        }

        foreach (var session in sessions)
        {
            var peerKey = session.RemoteAddress?.ToString();
            if (peerKey is not null)
            {
                lock (_bootstrapSessionsLock)
                {
                    if (_pubsubEstablishedPeers.Contains(peerKey))
                    {
                        continue;
                    }
                }
            }

            try
            {
                await EnsurePubsubSessionAsync(session, CancellationToken.None);
            }
            catch
            {
                // Best-effort stream warmup for existing bootstrap sessions.
            }
        }
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
        var localPeerId = _peer.Identity.PeerId.ToString();
        foreach (var (peerKey, address) in bootstrapPeers)
        {
            if (peerKey.Contains($"/p2p/{localPeerId}", StringComparison.Ordinal))
            {
                _logger.LogDebug("Skipping self-connection to {Address}", peerKey);
                continue;
            }

            if (!TryReserveBootstrapPeerConnection(peerKey))
            {
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await _peer.DialAsync(address, cancellationToken);
                try
                {
                    await EnsurePubsubSessionAsync(session, CancellationToken.None);
                }
                catch (Exception pubsubEx)
                {
                    _logger.LogWarning(pubsubEx,
                        "Pubsub stream warmup failed for bootstrap peer {Address}; reconnect loop will retry.",
                        peerKey);
                }
                RegisterBlocksByRootCandidate(session.RemoteAddress.ToString(), session.RemoteAddress);
                _blocksByRootPeerSelector.MarkConnected(peerKey);
                RecordPeerConnected(peerKey, ConnectionDirectionOutbound);
                TrackPeerSession(session);
                lock (_bootstrapSessionsLock)
                {
                    _bootstrapSessions.Add(session);
                }
                _logger.LogInformation("Connected to bootstrap peer {Address}", peerKey);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                ReleaseBootstrapPeerConnection(peerKey);
                RecordPeerConnectionFailure(ConnectionDirectionOutbound, MapConnectionFailureResult(ex));
                _logger.LogWarning(ex, "Timed out connecting to bootstrap peer {Address}", peerKey);
            }
            catch (Exception ex)
            {
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                ReleaseBootstrapPeerConnection(peerKey);
                RecordPeerConnectionFailure(ConnectionDirectionOutbound, MapConnectionFailureResult(ex));
                _logger.LogWarning(ex, "Failed to connect to bootstrap peer {Address}", peerKey);
            }
        }
    }

    private void StartBootstrapReconnectLoop()
    {
        if (!HasBootstrapPeers())
        {
            return;
        }

        _bootstrapReconnectLoopCts = new CancellationTokenSource();
        var token = _bootstrapReconnectLoopCts.Token;
        _ = Task.Factory.StartNew(
            () => BootstrapReconnectLoopAsync(token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void StopBootstrapReconnectLoop()
    {
        try
        {
            _bootstrapReconnectLoopCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation errors.
        }
        finally
        {
            _bootstrapReconnectLoopCts?.Dispose();
            _bootstrapReconnectLoopCts = null;
        }
    }

    private async Task BootstrapReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(BootstrapReconnectInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ReconnectDisconnectedBootstrapPeersAsync(cancellationToken);

                // Re-establish pubsub streams on connected peers that may have lost their
                // gossipsub session (e.g. stream failure, timeout during initial warmup).
                await EnsureBootstrapPubsubSessionsAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Bootstrap reconnect loop iteration failed.");
            }
        }
    }

    private async Task ReconnectDisconnectedBootstrapPeersAsync(CancellationToken cancellationToken)
    {
        if (_peer is null)
        {
            return;
        }

        var bootstrapPeers = SnapshotBootstrapPeers();
        var localPeerId = _peer.Identity.PeerId.ToString();
        foreach (var (peerKey, address) in bootstrapPeers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (peerKey.Contains($"/p2p/{localPeerId}", StringComparison.Ordinal))
            {
                continue;
            }

            bool alreadyConnected;
            lock (_bootstrapPeersLock)
            {
                alreadyConnected = _connectedBootstrapPeers.Contains(peerKey);
            }

            if (alreadyConnected)
            {
                continue;
            }

            if (!TryReserveBootstrapPeerConnection(peerKey))
            {
                continue;
            }

            try
            {
                var session = await _peer.DialAsync(address, cancellationToken);
                try
                {
                    await EnsurePubsubSessionAsync(session, cancellationToken);
                }
                catch
                {
                    // Best-effort pubsub stream warmup.
                }
                RegisterBlocksByRootCandidate(session.RemoteAddress.ToString(), session.RemoteAddress);
                _blocksByRootPeerSelector.MarkConnected(peerKey);
                RecordPeerConnected(peerKey, ConnectionDirectionOutbound);
                TrackPeerSession(session);
                lock (_bootstrapSessionsLock)
                {
                    _bootstrapSessions.Add(session);
                }
                _logger.LogInformation("Reconnected to bootstrap peer {Address}", peerKey);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                ReleaseBootstrapPeerConnection(peerKey);
                _logger.LogDebug("Timed out reconnecting to bootstrap peer {Address}", peerKey);
            }
            catch (OperationCanceledException)
            {
                ReleaseBootstrapPeerConnection(peerKey);
                throw;
            }
            catch (Exception ex)
            {
                _blocksByRootPeerSelector.MarkDisconnected(peerKey);
                ReleaseBootstrapPeerConnection(peerKey);
                _logger.LogDebug(ex, "Failed to reconnect to bootstrap peer {Address}", peerKey);
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

        lock (_bootstrapSessionsLock)
        {
            _pubsubEstablishedPeers.Remove(peerKey);
        }
    }

    private void ClearConnectedBootstrapPeers()
    {
        lock (_bootstrapPeersLock)
        {
            _connectedBootstrapPeers.Clear();
        }

        lock (_bootstrapSessionsLock)
        {
            _pubsubEstablishedPeers.Clear();
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
            var peerKey = session.RemoteAddress?.ToString();
            if (peerKey is not null)
            {
                lock (_bootstrapSessionsLock)
                {
                    _pubsubEstablishedPeers.Add(peerKey);
                }
            }
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
        RecordPeerConnected(peerKey, ConnectionDirectionInbound);
        TrackPeerSession(session);

        // Don't probe status immediately — the QUIC connection is established but
        // protocol negotiation (Identify / multistream-select) hasn't completed yet.
        // The periodic TriggerPeerStatusProbe (fired every slot) will pick up this
        // peer via TryFindBootstrapSession → _peerSessions once the session is ready.
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
            // Try reusing an existing bootstrap session before opening a new connection.
            var session = TryFindBootstrapSession(peerKey);
            if (session is not null)
            {
                await TryProbePeerStatusAsync(session, peerKey, disconnectAfterProbe: false);
                return;
            }

            using var dialTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dialTimeoutCts.CancelAfter(StatusProbeTimeout);
            session = await _peer.DialAsync(address, dialTimeoutCts.Token);
            RecordPeerConnected(peerKey, ConnectionDirectionOutbound);
            await TryProbePeerStatusAsync(session, peerKey, disconnectAfterProbe: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordPeerConnectionFailure(ConnectionDirectionOutbound, ConnectionResultTimeout);
            _logger.LogDebug("Timed out proactive status probe dial for peer {Peer}.", peerKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordPeerConnectionFailure(ConnectionDirectionOutbound, MapConnectionFailureResult(ex));
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
            var statusDialTask = session.DialAsync<LeanStatusProtocol, LeanStatusMessage, LeanStatusMessage>(
                localStatus,
                timeoutCts.Token);
            var statusDelayTask = Task.Delay(StatusProbeTimeout, timeoutCts.Token);
            var statusCompleted = await Task.WhenAny(statusDialTask, statusDelayTask);
            if (statusCompleted == statusDelayTask)
                throw new OperationCanceledException("Status probe dial timed out via fallback");
            var remoteStatus = await statusDialTask;
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
                var remotePeer = session.RemoteAddress.ToString();
                try
                {
                    await session.DisconnectAsync();
                    RecordPeerDisconnected(remotePeer, ConnectionDirectionOutbound, DisconnectionReasonLocalClose);
                }
                catch
                {
                    RecordPeerDisconnected(remotePeer, ConnectionDirectionOutbound, DisconnectionReasonError);
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

    private ISession? TryFindBootstrapSession(string peerKey)
    {
        var targetPeerId = ExtractPeerId(peerKey);
        if (string.IsNullOrWhiteSpace(targetPeerId))
        {
            return null;
        }

        // Check tracked peer sessions (populated from both bootstrap and inbound connections).
        lock (_peerSessionsLock)
        {
            if (_peerSessions.TryGetValue(targetPeerId, out var peerSession))
            {
                return peerSession;
            }
        }

        // Fall back to scanning bootstrap sessions by peer ID.
        lock (_bootstrapSessionsLock)
        {
            foreach (var session in _bootstrapSessions)
            {
                var remoteAddr = session.RemoteAddress?.ToString();
                if (remoteAddr is null)
                {
                    continue;
                }

                var sessionPeerId = ExtractPeerId(remoteAddr);
                if (string.Equals(sessionPeerId, targetPeerId, StringComparison.Ordinal))
                {
                    return session;
                }
            }
        }

        return null;
    }

    private void TrackPeerSession(ISession session)
    {
        var remoteAddr = session.RemoteAddress?.ToString();
        if (remoteAddr is null)
        {
            return;
        }

        var peerId = ExtractPeerId(remoteAddr);
        if (string.IsNullOrWhiteSpace(peerId))
        {
            return;
        }

        lock (_peerSessionsLock)
        {
            _peerSessions[peerId] = session;
        }
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

    private void RecordPeerConnected(string peerKey, string direction)
    {
        if (!TryNormalizePeerKey(peerKey, out var normalizedPeerKey))
        {
            return;
        }

        var added = false;
        lock (_connectedPeersLock)
        {
            added = _connectedPeers.Add(normalizedPeerKey);
            LeanMetrics.SetConnectedPeers(_connectedPeers.Count);
        }

        if (added)
        {
            LeanMetrics.PeerConnectionEventsTotal.WithLabels(direction, ConnectionResultSuccess).Inc();
        }
    }

    private static void RecordPeerConnectionFailure(string direction, string result)
    {
        LeanMetrics.PeerConnectionEventsTotal.WithLabels(direction, result).Inc();
    }

    private void RecordPeerDisconnected(string peerKey, string direction, string reason)
    {
        if (TryNormalizePeerKey(peerKey, out var normalizedPeerKey))
        {
            lock (_connectedPeersLock)
            {
                _connectedPeers.Remove(normalizedPeerKey);
                LeanMetrics.SetConnectedPeers(_connectedPeers.Count);
            }
        }

        LeanMetrics.PeerDisconnectionEventsTotal.WithLabels(direction, reason).Inc();
    }

    private static string MapConnectionFailureResult(Exception exception)
    {
        return exception is OperationCanceledException ? ConnectionResultTimeout : ConnectionResultError;
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
