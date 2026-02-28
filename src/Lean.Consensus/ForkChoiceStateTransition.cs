using Lean.Consensus.Types;
using System.Collections.Generic;
using System.Linq;

namespace Lean.Consensus;

public sealed class ForkChoiceStateTransition : IForkChoiceStateTransition
{
    private readonly ulong _maxValidatorCount;

    public ForkChoiceStateTransition(ConsensusConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
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
            if (!aggregated.AggregationBits.TryToValidatorIndices(out var participants))
            {
                postState = default!;
                reason = "Aggregated attestation must reference at least one validator.";
                return false;
            }

            foreach (var validatorId in participants)
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
        var justificationVotes = CloneVotes(parentState.JustificationVotes);
        var justifiedSlots = CloneJustifiedSlots(parentState.JustifiedSlots, latestFinalized, latestJustified);
        var originalFinalizedSlot = latestFinalized.Slot.Value;

        foreach (var aggregated in block.Body.Attestations)
        {
            if (!aggregated.AggregationBits.TryToValidatorIndices(out var participants))
            {
                postState = default!;
                reason = "Aggregated attestation must reference at least one validator.";
                return false;
            }

            if (!TryConsiderCheckpoint(
                    aggregated.Data,
                    participants,
                    validatorCount,
                    ref latestJustified,
                    ref latestFinalized,
                    originalFinalizedSlot,
                    justificationVotes,
                    justifiedSlots,
                    out reason))
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

        postState = new ForkChoiceNodeState(
            latestJustified,
            latestFinalized,
            validatorCount,
            FreezeVotes(justificationVotes),
            FreezeJustifiedSlots(justifiedSlots));
        reason = string.Empty;
        return true;
    }

    private bool TryConsiderCheckpoint(
        AttestationData data,
        IReadOnlyList<ulong> validatorIds,
        ulong validatorCount,
        ref Checkpoint latestJustified,
        ref Checkpoint latestFinalized,
        ulong originalFinalizedSlot,
        Dictionary<string, VoteAccumulator> justificationVotes,
        HashSet<ulong> justifiedSlots,
        out string reason)
    {
        if (data.Source.Slot.Value > data.Target.Slot.Value)
        {
            reason = "Source checkpoint slot exceeds target checkpoint slot.";
            return false;
        }

        // Lean consensus only considers attestations where target is newer than source.
        if (data.Target.Slot.Value <= data.Source.Slot.Value)
        {
            reason = string.Empty;
            return true;
        }

        // Source must already be justified.
        // Matches leanSpec state.py:528 — is_slot_justified(finalized_slot, source.slot).
        if (!IsSlotJustified(data.Source.Slot.Value, latestFinalized.Slot.Value, latestJustified.Slot.Value, justifiedSlots))
        {
            reason = string.Empty;
            return true;
        }

        if (IsSlotJustified(data.Target.Slot.Value, latestFinalized.Slot.Value, latestJustified.Slot.Value, justifiedSlots))
        {
            reason = string.Empty;
            return true;
        }

        if (!IsJustifiableSlot(originalFinalizedSlot, data.Target.Slot.Value))
        {
            reason = string.Empty;
            return true;
        }

        var targetKey = ToKey(data.Target.Root);
        if (!justificationVotes.TryGetValue(targetKey, out var vote))
        {
            vote = new VoteAccumulator(data.Target.Slot.Value);
            justificationVotes[targetKey] = vote;
        }
        else if (vote.TargetSlot != data.Target.Slot.Value)
        {
            reason = "Attestation target root maps to inconsistent target slots.";
            return false;
        }

        foreach (var validatorId in validatorIds)
        {
            vote.ValidatorIds.Add(validatorId);
        }

        if (HasTwoThirdsMajority(vote.ValidatorIds.Count, validatorCount))
        {
            // Record that this target slot is now justified.
            justifiedSlots.Add(data.Target.Slot.Value);
            justificationVotes.Remove(targetKey);

            // Set latest justified to the most recently justified target,
            // matching leanSpec's unconditional assignment.
            latestJustified = data.Target;

            var canFinalize = true;
            for (var slot = data.Source.Slot.Value + 1; slot < data.Target.Slot.Value; slot++)
            {
                if (IsJustifiableSlot(originalFinalizedSlot, slot) &&
                    !IsSlotJustified(slot, latestFinalized.Slot.Value, latestJustified.Slot.Value, justifiedSlots))
                {
                    canFinalize = false;
                    break;
                }
            }

            if (canFinalize && data.Source.Slot.Value > latestFinalized.Slot.Value)
            {
                latestFinalized = data.Source;
                PruneAfterFinalization(justificationVotes, justifiedSlots, latestFinalized.Slot.Value);
            }
        }

        reason = string.Empty;
        return true;
    }

    private static Dictionary<string, VoteAccumulator> CloneVotes(
        IReadOnlyDictionary<string, ForkChoiceJustificationVote>? source)
    {
        var votes = new Dictionary<string, VoteAccumulator>(StringComparer.Ordinal);
        if (source is null)
        {
            return votes;
        }

        foreach (var (root, vote) in source)
        {
            votes[root] = new VoteAccumulator(vote.TargetSlot, vote.ValidatorIds);
        }

        return votes;
    }

    private static HashSet<ulong> CloneJustifiedSlots(
        IReadOnlyList<ulong>? source,
        Checkpoint latestFinalized,
        Checkpoint latestJustified)
    {
        var slots = source is null
            ? new HashSet<ulong>()
            : new HashSet<ulong>(source);

        slots.RemoveWhere(slot => slot <= latestFinalized.Slot.Value);
        if (latestJustified.Slot.Value > latestFinalized.Slot.Value)
        {
            slots.Add(latestJustified.Slot.Value);
        }

        return slots;
    }

    private static IReadOnlyDictionary<string, ForkChoiceJustificationVote>? FreezeVotes(
        Dictionary<string, VoteAccumulator> votes)
    {
        if (votes.Count == 0)
        {
            return null;
        }

        var frozen = new Dictionary<string, ForkChoiceJustificationVote>(votes.Count, StringComparer.Ordinal);
        foreach (var (root, vote) in votes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var participants = vote.ValidatorIds.OrderBy(id => id).ToArray();
            frozen[root] = new ForkChoiceJustificationVote(vote.TargetSlot, participants);
        }

        return frozen;
    }

    private static IReadOnlyList<ulong>? FreezeJustifiedSlots(HashSet<ulong> justifiedSlots)
    {
        if (justifiedSlots.Count == 0)
        {
            return null;
        }

        return justifiedSlots.OrderBy(slot => slot).ToArray();
    }

    private static bool IsSlotJustified(
        ulong slot,
        ulong latestFinalizedSlot,
        ulong latestJustifiedSlot,
        HashSet<ulong> justifiedSlots)
    {
        if (slot <= latestFinalizedSlot)
        {
            return true;
        }

        if (slot == latestJustifiedSlot)
        {
            return true;
        }

        return justifiedSlots.Contains(slot);
    }

    private static bool IsJustifiableSlot(ulong latestFinalizedSlot, ulong candidateSlot)
    {
        if (candidateSlot < latestFinalizedSlot)
        {
            return false;
        }

        return new Slot(candidateSlot).IsJustifiableAfter(new Slot(latestFinalizedSlot));
    }

    private static bool HasTwoThirdsMajority(int voteCount, ulong validatorCount)
    {
        if (validatorCount == 0)
        {
            return false;
        }

        return checked(3UL * (ulong)voteCount) >= checked(2UL * validatorCount);
    }

    private static void PruneAfterFinalization(
        Dictionary<string, VoteAccumulator> justificationVotes,
        HashSet<ulong> justifiedSlots,
        ulong latestFinalizedSlot)
    {
        justifiedSlots.RemoveWhere(slot => slot <= latestFinalizedSlot);

        if (justificationVotes.Count == 0)
        {
            return;
        }

        var obsoleteRoots = justificationVotes
            .Where(pair => pair.Value.TargetSlot <= latestFinalizedSlot)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var root in obsoleteRoots)
        {
            justificationVotes.Remove(root);
        }
    }

    private static string ToKey(Bytes32 root)
    {
        return Convert.ToHexString(root.AsSpan());
    }

    private sealed class VoteAccumulator
    {
        public VoteAccumulator(ulong targetSlot, IEnumerable<ulong>? validatorIds = null)
        {
            TargetSlot = targetSlot;
            ValidatorIds = validatorIds is null
                ? new HashSet<ulong>()
                : new HashSet<ulong>(validatorIds);
        }

        public ulong TargetSlot { get; }

        public HashSet<ulong> ValidatorIds { get; }
    }
}
