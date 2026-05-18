using Lean.Consensus.Types;

namespace Lean.Consensus;

public interface IConsensusService
{
    ulong CurrentSlot { get; }
    ulong HeadSlot { get; }
    ulong JustifiedSlot { get; }
    ulong FinalizedSlot { get; }

    /// <summary>
    /// False while the store's justified checkpoint is still the un-earned boot
    /// seed (genesis or a checkpoint-sync anchor). The proposer divergence guard
    /// is bypassed while this is false.
    /// </summary>
    bool JustifiedAdvancedSinceBoot { get; }
    bool HasUnknownBlockRootsInFlight { get; }
    byte[] HeadRoot { get; }
    (byte[] ParentRoot, AttestationData BaseAttestationData) GetProposalContext(ulong slot);
    AttestationData CreateAttestationData(ulong slot);
    bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out string reason);
    bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out Checkpoint postJustified, out string reason);
    bool TryComputeBlockStateRoot(
        Block candidateBlock,
        out Bytes32 stateRoot,
        out Checkpoint postJustified,
        out IReadOnlyList<bool> postJustifiedSlots,
        out ulong postFinalizedSlot,
        out string reason);
    bool TryApplyLocalBlock(SignedBlock signedBlock, out string reason);
    bool TryApplyLocalAttestation(SignedAttestation signedAttestation, out string reason);
    bool TryApplyLocalAggregatedAttestation(SignedAggregatedAttestation signed, out string reason);
    bool ApplyLocalAggregationResult(SignedAggregatedAttestation signed, out string reason);
    /// <summary>
    /// Selects aggregated attestation payloads eligible for inclusion in a block
    /// at <paramref name="slot"/> built on <paramref name="parentRoot"/>, faithful
    /// to leanSpec build_block (PR #716). An attestation is eligible when its head
    /// is a known block, its source/target roots match the parent chain view, its
    /// source SLOT is justified on that chain (not necessarily equal to the chain's
    /// latest justified checkpoint), and its target slot is not already justified
    /// (genesis self-votes excepted).
    /// </summary>
    /// <param name="currentJustifiedSlots">
    /// The evolving justified-slot bitfield for the fixed-point loop. Pass null on
    /// the first iteration to derive it from the parent state; pass the post-state
    /// bitfield on later iterations once justification has advanced.
    /// </param>
    /// <param name="currentFinalizedSlot">
    /// The evolving finalized slot, paired with <paramref name="currentJustifiedSlots"/>.
    /// </param>
    (IReadOnlyList<AggregatedAttestation> Attestations, IReadOnlyList<AggregatedSignatureProof> Proofs) GetKnownAggregatedPayloadsForBlock(
        ulong slot,
        Bytes32 parentRoot,
        IReadOnlyList<bool>? currentJustifiedSlots,
        ulong? currentFinalizedSlot);
    List<(AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)> CollectAttestationsForAggregation();
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
