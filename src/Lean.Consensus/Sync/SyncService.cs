using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public sealed class SyncService : ISyncService
{
    private readonly IBlockProcessor _processor;
    private readonly SyncPeerManager _peerManager;
    private readonly NewBlockCache _cache;
    private readonly IAttestationSink _attestationSink;
    private readonly HeadSync _headSync;
    private readonly BackfillSync _backfillSync;

    private SyncState _state = SyncState.Idle;

    public SyncService(
        IBlockProcessor processor,
        SyncPeerManager peerManager,
        NewBlockCache cache,
        IAttestationSink attestationSink,
        INetworkRequester network)
    {
        _processor = processor;
        _peerManager = peerManager;
        _cache = cache;
        _attestationSink = attestationSink;
        // Note: _headSync is captured by the lambda and assigned on the next line.
        // This is safe because the callback only executes after construction completes.
        _backfillSync = new BackfillSync(network, processor, peerManager,
            onBlockAccepted: root => _headSync!.CascadeChildren(root));
        _headSync = new HeadSync(processor, cache, _backfillSync);
    }

    public SyncState State => _state;

    public void OnPeerConnected(string peerId)
    {
        _peerManager.AddPeer(peerId);
        RecomputeState();
    }

    public void OnPeerDisconnected(string peerId)
    {
        _peerManager.RemovePeer(peerId);
        RecomputeState();
    }

    public Task OnPeerStatusAsync(string peerId, ulong headSlot, ulong finalizedSlot)
    {
        _peerManager.UpdatePeerStatus(peerId, headSlot, finalizedSlot);
        RecomputeState();
        return Task.CompletedTask;
    }

    public Task OnGossipBlockAsync(SignedBlockWithAttestation block, Bytes32 blockRoot, string? peerId)
    {
        _headSync.OnGossipBlock(block, blockRoot, peerId);
        RecomputeState();
        return Task.CompletedTask;
    }

    public Task OnGossipAttestationAsync(SignedAttestation attestation)
    {
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
        var hasOrphans = _cache.OrphanCount > 0;

        if (localHead >= networkFinalized && !hasOrphans)
            _state = SyncState.Synced;
        else
            _state = SyncState.Syncing;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _backfillSync.SetShutdownToken(ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
