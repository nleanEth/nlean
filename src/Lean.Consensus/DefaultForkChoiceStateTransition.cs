using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class DefaultForkChoiceStateTransition : IForkChoiceStateTransition
{
    public bool TryTransition(
        ForkChoiceNodeState parentState,
        SignedBlockWithAttestation signedBlock,
        out ForkChoiceNodeState postState,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(parentState);
        ArgumentNullException.ThrowIfNull(signedBlock);

        var block = signedBlock.Message.Block;
        var proposerAttestation = signedBlock.Message.ProposerAttestation;

        var validatorCount = parentState.ValidatorCount;
        validatorCount = Math.Max(validatorCount, block.ProposerIndex + 1);
        validatorCount = Math.Max(validatorCount, proposerAttestation.ValidatorId + 1);

        foreach (var aggregated in block.Body.Attestations)
        {
            foreach (var validatorId in aggregated.AggregationBits.ToValidatorIndices())
            {
                validatorCount = Math.Max(validatorCount, validatorId + 1);
            }
        }

        var latestJustified = parentState.LatestJustified;
        var latestFinalized = parentState.LatestFinalized;

        ConsiderCheckpoint(proposerAttestation.Data, ref latestJustified, ref latestFinalized);
        foreach (var aggregated in block.Body.Attestations)
        {
            ConsiderCheckpoint(aggregated.Data, ref latestJustified, ref latestFinalized);
        }

        if (latestFinalized.Slot.Value > latestJustified.Slot.Value)
        {
            postState = default!;
            reason = "Finalized checkpoint slot cannot exceed justified checkpoint slot.";
            return false;
        }

        if (latestJustified.Slot.Value > block.Slot.Value)
        {
            postState = default!;
            reason = $"Justified checkpoint slot {latestJustified.Slot.Value} cannot exceed block slot {block.Slot.Value}.";
            return false;
        }

        if (latestFinalized.Slot.Value > block.Slot.Value)
        {
            postState = default!;
            reason = $"Finalized checkpoint slot {latestFinalized.Slot.Value} cannot exceed block slot {block.Slot.Value}.";
            return false;
        }

        postState = new ForkChoiceNodeState(latestJustified, latestFinalized, validatorCount);
        reason = string.Empty;
        return true;
    }

    private static void ConsiderCheckpoint(
        AttestationData data,
        ref Checkpoint latestJustified,
        ref Checkpoint latestFinalized)
    {
        if (data.Target.Slot.Value > latestJustified.Slot.Value)
        {
            latestJustified = data.Target;
        }

        if (data.Source.Slot.Value > latestFinalized.Slot.Value && data.Source.Slot.Value <= latestJustified.Slot.Value)
        {
            latestFinalized = data.Source;
        }
    }
}
