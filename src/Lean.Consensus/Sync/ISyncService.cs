using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public interface ISyncService
{
    SyncState State { get; }
    ulong GetNetworkHeadSlot();
    Task OnGossipBlockAsync(SignedBlock block, Bytes32 blockRoot, string? peerId);
    Task OnGossipAttestationAsync(SignedAttestation attestation);
    Task OnPeerStatusAsync(string peerId, ulong headSlot, ulong finalizedSlot, Bytes32? headRoot = null);
    void TrySyncFromBestPeer();
    void CascadeAcceptedBlock(Bytes32 blockRoot);
    void OnPeerConnected(string peerId);
    void OnPeerDisconnected(string peerId);

    /// <summary>
    /// Request a specific block by root via BlocksByRoot. Used by fork-choice
    /// when state-transition advances store.latest_justified to a root we don't
    /// have locally — the 2/3 supermajority is already formed on that block,
    /// so we need to pull it in to clear "Unknown source root" rejections.
    /// </summary>
    void RequestBlockByRoot(Bytes32 blockRoot);

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
