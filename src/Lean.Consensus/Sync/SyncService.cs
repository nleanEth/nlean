using Lean.Consensus.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lean.Consensus.Sync;

public sealed class SyncService : ISyncService
{
    private readonly IBlockProcessor _processor;
    private readonly SyncPeerManager _peerManager;
    private readonly NewBlockCache _cache;
    private readonly IAttestationSink _attestationSink;
    private readonly HeadSync _headSync;
    private readonly BackfillSync _backfillSync;
    private readonly ILogger<SyncService> _logger;

    private SyncState _state = SyncState.Idle;

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
            logger: backfillLogger);
        _headSync = new HeadSync(processor, cache, _backfillSync, headSyncLogger);
    }

    public SyncState State => _state;

    public void OnPeerConnected(string peerId)
    {
        _peerManager.AddPeer(peerId);
        RecomputeState();
        _logger.LogInformation("SyncService: peer connected. PeerId: {PeerId}, PeerCount: {PeerCount}, State: {State}",
            peerId, _peerManager.PeerCount, _state);
    }

    public void OnPeerDisconnected(string peerId)
    {
        _peerManager.RemovePeer(peerId);
        RecomputeState();
        _logger.LogInformation("SyncService: peer disconnected. PeerId: {PeerId}, PeerCount: {PeerCount}, State: {State}",
            peerId, _peerManager.PeerCount, _state);
    }

    public Task OnPeerStatusAsync(string peerId, ulong headSlot, ulong finalizedSlot, Bytes32? headRoot = null)
    {
        _peerManager.UpdatePeerStatus(peerId, headSlot, finalizedSlot, headRoot);
        RecomputeState();
        RetryOrphanBackfills();

        // Proactive sync: if we're behind the network and the peer gave us their
        // head root, trigger a backfill so we don't have to wait for gossip blocks.
        if (headRoot is not null && _state == SyncState.Syncing)
        {
            TriggerProactiveBackfill(headRoot.Value, headSlot, peerId);
        }

        return Task.CompletedTask;
    }

    public void TrySyncFromBestPeer()
    {
        if (_state != SyncState.Syncing)
            return;

        // Only trigger periodic backfill when meaningfully behind the network.
        // During normal operation, transient Syncing states (e.g. one-slot lag)
        // resolve via gossip without needing backfill.
        var networkFinalized = _peerManager.GetNetworkFinalizedSlot();
        var localHead = _processor.HeadSlot;
        if (localHead >= networkFinalized)
            return;

        var best = _peerManager.GetBestPeerHead();
        if (best is null)
            return;

        TriggerProactiveBackfill(best.Value.Root, best.Value.Slot, "periodic");
    }

    public void CascadeAcceptedBlock(Bytes32 blockRoot)
    {
        _headSync.CascadeChildren(blockRoot);
        RecomputeState();
    }

    public Task OnGossipBlockAsync(SignedBlockWithAttestation block, Bytes32 blockRoot, string? peerId)
    {
        _headSync.OnGossipBlock(block, blockRoot, peerId);
        RecomputeState();
        return Task.CompletedTask;
    }

    public Task OnGossipAttestationAsync(SignedAttestation attestation)
    {
        // Always process attestations regardless of sync state.
        // Fork-choice needs attestation data for justification/finalization
        // even before peer status exchanges transition out of Idle.
        _attestationSink.AddAttestation(attestation);
        return Task.CompletedTask;
    }

    public void RecomputeState()
    {
        if (_peerManager.PeerCount == 0)
        {
            _state = SyncState.Idle;
            return;
        }

        var networkFinalized = _peerManager.GetNetworkFinalizedSlot();
        var localHead = _processor.HeadSlot;

        // Sync is complete when the local head is at or past the network's
        // finalized slot.  Orphan blocks in the cache are resolved via
        // backfill in the background and must NOT keep the node stuck in
        // Syncing — otherwise validator duties are suppressed and the
        // network loses quorum.
        if (localHead >= networkFinalized)
            _state = SyncState.Synced;
        else
            _state = SyncState.Syncing;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _backfillSync.SetShutdownToken(ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct) => await _backfillSync.StopAsync();

    private void RetryOrphanBackfills()
    {
        var orphanParents = _cache.GetOrphanParents();
        foreach (var parent in orphanParents)
        {
            _backfillSync.RequestBackfill(parent);
        }
    }

    private void TriggerProactiveBackfill(Bytes32 headRoot, ulong peerHeadSlot, string source)
    {
        var localHead = _processor.HeadSlot;
        if (localHead < peerHeadSlot && !_processor.IsBlockKnown(headRoot))
        {
            _logger.LogInformation(
                "Proactive sync: triggering backfill from peer head root. Source: {Source}, PeerHead: {PeerHeadSlot}, LocalHead: {LocalHead}, HeadRoot: {HeadRoot}",
                source, peerHeadSlot, localHead, headRoot);
            _backfillSync.RequestBackfill(headRoot);
        }
    }
}
