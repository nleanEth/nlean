using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class Devnet2ForkChoiceStateTransition : IForkChoiceStateTransition
{
    private readonly ulong _slotsPerEpoch;
    private readonly ulong _maxValidatorCount;

    public Devnet2ForkChoiceStateTransition(ConsensusConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _slotsPerEpoch = Math.Max(1, config.SlotsPerEpoch);
        _maxValidatorCount = Math.Max(1, config.MaxValidatorCount);
    }

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

        if (validatorCount > _maxValidatorCount)
        {
            postState = default!;
            reason = $"Validator count {validatorCount} exceeds configured limit {_maxValidatorCount}.";
            return false;
        }

        var latestJustified = parentState.LatestJustified;
        var latestFinalized = parentState.LatestFinalized;

        if (!TryConsiderCheckpoint(proposerAttestation.Data, ref latestJustified, ref latestFinalized, out reason))
        {
            postState = default!;
            return false;
        }

        foreach (var aggregated in block.Body.Attestations)
        {
            if (!TryConsiderCheckpoint(aggregated.Data, ref latestJustified, ref latestFinalized, out reason))
            {
                postState = default!;
                return false;
            }
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

    private bool TryConsiderCheckpoint(
        AttestationData data,
        ref Checkpoint latestJustified,
        ref Checkpoint latestFinalized,
        out string reason)
    {
        if (data.Source.Slot.Value > data.Target.Slot.Value)
        {
            reason = "Source checkpoint slot exceeds target checkpoint slot.";
            return false;
        }

        if (GetEpoch(data.Source.Slot.Value) > GetEpoch(data.Target.Slot.Value))
        {
            reason = "Source checkpoint epoch exceeds target checkpoint epoch.";
            return false;
        }

        if (data.Target.Slot.Value > latestJustified.Slot.Value)
        {
            latestJustified = data.Target;
        }

        if (data.Source.Slot.Value > latestFinalized.Slot.Value && data.Source.Slot.Value <= latestJustified.Slot.Value)
        {
            latestFinalized = data.Source;
        }

        reason = string.Empty;
        return true;
    }

    private ulong GetEpoch(ulong slot)
    {
        return slot / _slotsPerEpoch;
    }
}
