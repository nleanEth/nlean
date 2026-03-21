using System.Collections.Concurrent;
using Lean.Consensus.Types;
using Lean.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lean.Consensus.Sync;

public sealed class SyncService : ISyncService
{
    private const int MaxPendingAttestations = 1024;

    private readonly IBlockProcessor _processor;
    private readonly SyncPeerManager _peerManager;
    private readonly NewBlockCache _cache;
    private readonly IAttestationSink _attestationSink;
    private readonly HeadSync _headSync;
    private readonly BackfillSync _backfillSync;
    private readonly ILogger<SyncService> _logger;

    private readonly ConcurrentQueue<SignedAttestation> _pendingAttestations = new();
    private int _pendingAttestationCount;

    private volatile SyncState _state = SyncState.Idle;

    public SyncService(
        IBlockProcessor processor,
        SyncPeerManager peerManager,
        NewBlockCache cache,
        IAttestationSink attestationSink,
        INetworkRequester network,
        ILogger<BackfillSync>? backfillLogger = null,
        ILogger<HeadSync>? headSyncLogger = null,
        ILogger<SyncService>? logger = null)
    {
        _processor = processor;
        _peerManager = peerManager;
        _cache = cache;
        _attestationSink = attestationSink;
        _logger = logger ?? NullLogger<SyncService>.Instance;
        // Note: _headSync is captured by the lambda and assigned on the next line.
        // This is safe because the callback only executes after construction completes.
        _backfillSync = new BackfillSync(network, processor, peerManager,
            onBlockAccepted: root =>
            {
                _headSync!.CascadeChildren(root);
                RecomputeState();
            },
            shouldDeferBackfill: () => _state == SyncState.Synced,
            logger: backfillLogger);
        _headSync = new HeadSync(processor, cache, _backfillSync, headSyncLogger);
    }

    public SyncState State => _state;

    public void OnPeerConnected(string peerId)
    {
        _peerManager.AddPeer(peerId);
        RecomputeState();
        LeanMetrics.SetSyncPeersConnected(_peerManager.PeerCount);
        _logger.LogDebug("SyncService: peer connected. PeerId: {PeerId}, PeerCount: {PeerCount}, State: {State}",
            peerId, _peerManager.PeerCount, _state);
    }

    public void OnPeerDisconnected(string peerId)
    {
        _peerManager.RemovePeer(peerId);
        RecomputeState();
        LeanMetrics.SetSyncPeersConnected(_peerManager.PeerCount);
        _logger.LogDebug("SyncService: peer disconnected. PeerId: {PeerId}, PeerCount: {PeerCount}, State: {State}",
            peerId, _peerManager.PeerCount, _state);
    }

    public Task OnPeerStatusAsync(string peerId, ulong headSlot, ulong finalizedSlot, Bytes32? headRoot = null)
    {
        _peerManager.UpdatePeerStatus(peerId, headSlot, finalizedSlot, headRoot);
        RecomputeState();
        RetryOrphanBackfills();

        return Task.CompletedTask;
    }

    public void TrySyncFromBestPeer()
    {
        RetryOrphanBackfills();
    }

    public void CascadeAcceptedBlock(Bytes32 blockRoot)
    {
        _headSync.CascadeChildren(blockRoot);
        DrainPendingAttestations();
        RecomputeState();
        LeanMetrics.SetSyncCacheSize(_cache.Count);
        LeanMetrics.SetSyncOrphanCount(_cache.OrphanCount);
    }

    public Task OnGossipBlockAsync(SignedBlockWithAttestation block, Bytes32 blockRoot, string? peerId)
    {
        _headSync.OnGossipBlock(block, blockRoot, peerId);
        DrainPendingAttestations();
        RecomputeState();
        LeanMetrics.SetSyncCacheSize(_cache.Count);
        LeanMetrics.SetSyncOrphanCount(_cache.OrphanCount);
        return Task.CompletedTask;
    }

    public Task OnGossipAttestationAsync(SignedAttestation attestation)
    {
        // Try to apply immediately; if the fork-choice store rejects it
        // (e.g. unknown head/target/source root), buffer it for replay
        // once the missing block arrives.
        if (!_attestationSink.TryAddAttestation(attestation))
        {
            EnqueuePendingAttestation(attestation);
        }
        return Task.CompletedTask;
    }

    public void RecomputeState()
    {
        var oldState = _state;

        if (_peerManager.PeerCount == 0)
        {
            _state = SyncState.Idle;
            LeanMetrics.SetSyncState(0);
        }
        else
        {
            var networkHead = _peerManager.GetNetworkHeadSlot();
            var localHead = _processor.HeadSlot;

            if (localHead + 2 >= networkHead)
            {
                _state = SyncState.Synced;
                LeanMetrics.SetSyncState(2);
            }
            else
            {
                _state = SyncState.Syncing;
                LeanMetrics.SetSyncState(1);
            }
        }

        if (_state != oldState)
        {
            _logger.LogInformation("Sync state changed: {OldState} -> {NewState}, PeerCount: {PeerCount}",
                oldState, _state, _peerManager.PeerCount);
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        _backfillSync.SetShutdownToken(ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct) => await _backfillSync.StopAsync();

    private void RetryOrphanBackfills()
    {
        var bestPeerId = _peerManager.GetBestPeerHead()?.PeerId;
        var orphanParents = _cache.GetOrphanParentsWithHints();
        foreach (var (parent, preferredPeerId) in orphanParents)
        {
            _backfillSync.RequestBackfill(parent, ResolvePreferredPeerId(preferredPeerId, bestPeerId));
        }
    }
    private void EnqueuePendingAttestation(SignedAttestation attestation)
    {
        _pendingAttestations.Enqueue(attestation);
        var count = Interlocked.Increment(ref _pendingAttestationCount);

        // Evict oldest if over capacity.
        while (count > MaxPendingAttestations && _pendingAttestations.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _pendingAttestationCount);
        }
    }

    private void DrainPendingAttestations()
    {
        // Single pass: try to replay all pending attestations.
        // Those that still fail are re-enqueued.
        var remaining = Volatile.Read(ref _pendingAttestationCount);
        if (remaining == 0)
            return;

        var retryCount = 0;
        for (var i = 0; i < remaining; i++)
        {
            if (!_pendingAttestations.TryDequeue(out var att))
                break;

            Interlocked.Decrement(ref _pendingAttestationCount);

            if (!_attestationSink.TryAddAttestation(att))
            {
                EnqueuePendingAttestation(att);
                retryCount++;
            }
        }

        if (retryCount > 0)
        {
            _logger.LogDebug("Drained pending attestations. Replayed: {Replayed}, StillPending: {Pending}",
                remaining - retryCount, retryCount);
        }
    }

    internal static string? ResolvePreferredPeerId(string? hintedPeerId, string? bestPeerId)
    {
        if (!string.IsNullOrWhiteSpace(hintedPeerId))
            return hintedPeerId;

        return string.IsNullOrWhiteSpace(bestPeerId) ? null : bestPeerId;
    }
}
