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
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
