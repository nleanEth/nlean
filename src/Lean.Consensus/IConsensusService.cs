using Lean.Consensus.Types;

namespace Lean.Consensus;

public interface IConsensusService
{
    ulong CurrentSlot { get; }
    ulong HeadSlot { get; }
    ulong JustifiedSlot { get; }
    ulong FinalizedSlot { get; }
    byte[] HeadRoot { get; }
    byte[] GetProposalHeadRoot();
    AttestationData CreateAttestationData(ulong slot);
    bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out string reason);
    bool TryApplyLocalBlock(SignedBlockWithAttestation signedBlock, out string reason);
    bool TryApplyLocalAttestation(SignedAttestation signedAttestation, out string reason);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
