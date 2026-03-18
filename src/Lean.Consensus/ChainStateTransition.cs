using System.Linq;
using Lean.Consensus.Types;
using Lean.Metrics;
using System.Diagnostics;

namespace Lean.Consensus;

internal sealed class ChainStateTransition
{
    private const ulong JustificationsValidatorsLimit = 1UL << 30;

    public ChainStateTransition(ConsensusConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    private readonly ConsensusConfig _config;

    public State CreateGenesisState(ulong initialValidatorCount)
    {
        var validators = BuildGenesisValidators(initialValidatorCount);
        var emptyBodyRoot = new Bytes32(new BlockBody(Array.Empty<AggregatedAttestation>()).HashTreeRoot());
        var genesisHeader = new BlockHeader(
            new Slot(0),
            0,
            Bytes32.Zero(),
            Bytes32.Zero(),
            emptyBodyRoot);

        var genesisCheckpoint = Checkpoint.Default();
        var state = new State(
            new Config(_config.GenesisTimeUnix),
            new Slot(0),
            genesisHeader,
            genesisCheckpoint,
            genesisCheckpoint,
            Array.Empty<Bytes32>(),
            Array.Empty<bool>(),
            validators,
            Array.Empty<Bytes32>(),
            Array.Empty<bool>());

        // Compute state root with zeroed header state_root, then fill it in.
        var stateRoot = new Bytes32(state.HashTreeRoot());
        return state with { LatestBlockHeader = genesisHeader with { StateRoot = stateRoot } };
    }

    public bool TryComputeStateRoot(
        State parentState,
        Block candidateBlock,
        out Bytes32 stateRoot,
        out State postState,
        out string reason)
    {
        if (!TryComputePostState(parentState, candidateBlock, out postState, out reason))
        {
            stateRoot = Bytes32.Zero();
            return false;
        }

        stateRoot = new Bytes32(postState.HashTreeRoot());
        return true;
    }

    private bool TryComputePostState(
        State parentState,
        Block block,
        out State postState,
        out string reason)
    {
        var stateSlot = parentState.Slot.Value;
        var latestBlockHeader = parentState.LatestBlockHeader;
        var latestJustified = parentState.LatestJustified;
        var latestFinalized = parentState.LatestFinalized;
        var historicalBlockHashes = parentState.HistoricalBlockHashes.ToList();
        var justifiedSlots = parentState.JustifiedSlots.ToList();
        var validators = parentState.Validators;
        var justificationsRoots = parentState.JustificationsRoots.ToList();
        var justificationsValidators = parentState.JustificationsValidators.ToList();
        var transitionStopwatch = Stopwatch.StartNew();
        var slotsProcessingStopwatch = Stopwatch.StartNew();
        var blockProcessingStopwatch = new Stopwatch();
        var attestationsProcessingStopwatch = new Stopwatch();
        ulong slotsProcessed = 0;
        ulong attestationsProcessed = 0;
        var finalizedSlotBeforeTransition = latestFinalized.Slot.Value;
        var transitionSucceeded = false;
        var finalizedAdvanced = false;

        try
        {

            if (block.Slot.Value <= stateSlot)
            {
                postState = default!;
                reason = $"Block slot {block.Slot.Value} must be greater than state slot {stateSlot}.";
                return false;
            }

            while (stateSlot < block.Slot.Value)
            {
                if (latestBlockHeader.StateRoot.Equals(Bytes32.Zero()))
                {
                    var preState = new State(
                        parentState.Config,
                        new Slot(stateSlot),
                        latestBlockHeader,
                        latestJustified,
                        latestFinalized,
                        historicalBlockHashes,
                        justifiedSlots,
                        validators,
                        justificationsRoots,
                        justificationsValidators);
                    latestBlockHeader = latestBlockHeader with { StateRoot = new Bytes32(preState.HashTreeRoot()) };
                }

                stateSlot++;
                slotsProcessed++;
            }

            slotsProcessingStopwatch.Stop();
            blockProcessingStopwatch.Start();

            if (block.Slot.Value != stateSlot)
            {
                postState = default!;
                reason = $"Block slot {block.Slot.Value} does not match state slot {stateSlot}.";
                return false;
            }

            if (block.Slot.Value <= latestBlockHeader.Slot.Value)
            {
                postState = default!;
                reason = $"Block slot {block.Slot.Value} is not greater than latest block header slot {latestBlockHeader.Slot.Value}.";
                return false;
            }

            if (validators.Count == 0)
            {
                postState = default!;
                reason = "No validators are configured for state transition.";
                return false;
            }

            // Verify the block proposer matches round-robin selection.
            if (!block.ProposerIndex.IsProposerFor(block.Slot.Value, validators.Count))
            {
                postState = default!;
                reason = $"Block proposer {block.ProposerIndex} is not the expected proposer for slot {block.Slot.Value} (expected {block.Slot.Value % (ulong)validators.Count}).";
                return false;
            }

            // Verify block.parent_root matches hash_tree_root(latest_block_header).
            var expectedParentRoot = new Bytes32(latestBlockHeader.HashTreeRoot());
            if (!block.ParentRoot.Equals(expectedParentRoot))
            {
                postState = default!;
                reason = $"Block parent root mismatch at slot {block.Slot.Value}: block has {block.ParentRoot}, expected {expectedParentRoot}.";
                return false;
            }

            if (latestBlockHeader.Slot.Value == 0)
            {
                latestJustified = latestJustified with { Root = block.ParentRoot };
                latestFinalized = latestFinalized with { Root = block.ParentRoot };
            }

            historicalBlockHashes.Add(block.ParentRoot);

            var emptySlots = checked((int)(block.Slot.Value - latestBlockHeader.Slot.Value - 1));
            for (var i = 0; i < emptySlots; i++)
            {
                historicalBlockHashes.Add(Bytes32.Zero());
            }

            if (block.Slot.Value > 0)
            {
                var targetIndex = new Slot(block.Slot.Value - 1).JustifiedIndexAfter(latestFinalized.Slot);
                if (targetIndex.HasValue)
                {
                    EnsureBooleanCapacity(justifiedSlots, targetIndex.Value + 1);
                }
            }

            latestBlockHeader = new BlockHeader(
                block.Slot,
                block.ProposerIndex,
                block.ParentRoot,
                Bytes32.Zero(),
                new Bytes32(block.Body.HashTreeRoot()));

            // NOTE: Multiple aggregated attestations with the same AttestationData are
            // allowed. Different validator subsets may attest to the same checkpoint and
            // arrive as separate aggregates. The vote accumulation uses bool[] per
            // validator, so overlapping attestations are harmless (no double-counting).

            if (justificationsRoots.Any(root => root.Equals(Bytes32.Zero())))
            {
                postState = default!;
                reason = "Zero hash is not allowed in justifications roots.";
                return false;
            }

            if (!TryBuildJustificationsMap(
                    justificationsRoots,
                    justificationsValidators,
                    validators.Count,
                    out var justificationsMap,
                    out reason))
            {
                postState = default!;
                return false;
            }

            var rootToSlot = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var startSlot = latestFinalized.Slot.Value + 1;
            for (var slot = startSlot; slot < (ulong)historicalBlockHashes.Count; slot++)
            {
                rootToSlot[ToKey(historicalBlockHashes[(int)slot])] = slot;
            }

            attestationsProcessingStopwatch.Start();
            foreach (var aggregated in block.Body.Attestations)
            {
                attestationsProcessed++;
                var sourceSlot = aggregated.Data.Source.Slot.Value;
                var targetSlot = aggregated.Data.Target.Slot.Value;

                if (aggregated.Data.Source.Root.Equals(Bytes32.Zero()) || aggregated.Data.Target.Root.Equals(Bytes32.Zero()))
                {
                    continue;
                }

                if (!TryIsSlotJustified(sourceSlot, latestFinalized.Slot.Value, justifiedSlots, out var sourceJustified, out reason))
                {
                    postState = default!;
                    return false;
                }

                if (!sourceJustified)
                {
                    continue;
                }

                if (!TryIsSlotJustified(targetSlot, latestFinalized.Slot.Value, justifiedSlots, out var targetAlreadyJustified, out reason))
                {
                    postState = default!;
                    return false;
                }

                if (targetAlreadyJustified)
                {
                    continue;
                }

                if (sourceSlot >= (ulong)historicalBlockHashes.Count || targetSlot >= (ulong)historicalBlockHashes.Count)
                {
                    // Block import remains valid and
                    // this attestation is ignored when it references unavailable history.
                    continue;
                }

                if (!aggregated.Data.Source.Root.Equals(historicalBlockHashes[(int)sourceSlot]))
                {
                    continue;
                }

                if (!aggregated.Data.Target.Root.Equals(historicalBlockHashes[(int)targetSlot]))
                {
                    continue;
                }

                if (targetSlot <= sourceSlot)
                {
                    continue;
                }

                if (!new Slot(targetSlot).IsJustifiableAfter(latestFinalized.Slot))
                {
                    continue;
                }

                var targetKey = ToKey(aggregated.Data.Target.Root);
                if (!justificationsMap.TryGetValue(targetKey, out var votes))
                {
                    votes = new JustificationVotes(
                        aggregated.Data.Target.Root,
                        new bool[validators.Count]);
                    justificationsMap[targetKey] = votes;
                }

                for (var validatorIndex = 0; validatorIndex < aggregated.AggregationBits.Bits.Count; validatorIndex++)
                {
                    if (!aggregated.AggregationBits.Bits[validatorIndex])
                    {
                        continue;
                    }

                    if (validatorIndex >= validators.Count)
                    {
                        postState = default!;
                        reason = $"Validator index {validatorIndex} is out of range for validator set size {validators.Count}.";
                        return false;
                    }

                    votes.Votes[validatorIndex] = true;
                }

                var voteCount = 0;
                for (var vi = 0; vi < votes.Votes.Length; vi++)
                    if (votes.Votes[vi]) voteCount++;
                if (checked(3 * voteCount) < checked(2 * validators.Count))
                {
                    continue;
                }

                latestJustified = aggregated.Data.Target;
                var justifiedIndex = aggregated.Data.Target.Slot.JustifiedIndexAfter(latestFinalized.Slot);
                if (justifiedIndex.HasValue)
                {
                    EnsureBooleanCapacity(justifiedSlots, justifiedIndex.Value + 1);
                    justifiedSlots[justifiedIndex.Value] = true;
                }

                justificationsMap.Remove(targetKey);

                // leanSpec finalization rule: finalize source only when NO justifiable
                // slots exist between source and target.  Any justifiable gap blocks
                // finalization regardless of whether those slots are already justified.
                var canFinalize = true;
                for (var slot = sourceSlot + 1; slot < targetSlot; slot++)
                {
                    if (new Slot(slot).IsJustifiableAfter(latestFinalized.Slot))
                    {
                        canFinalize = false;
                        break;
                    }
                }

                if (!canFinalize)
                {
                    continue;
                }

                var previousFinalizedSlot = latestFinalized.Slot.Value;
                latestFinalized = aggregated.Data.Source;
                var delta = latestFinalized.Slot.Value - previousFinalizedSlot;
                if (delta == 0)
                {
                    continue;
                }

                ShiftLeft(justifiedSlots, delta);

                var obsoleteTargets = new List<string>();
                foreach (var (key, value) in justificationsMap)
                {
                    // leanSpec: root_to_slots.get(root, []) — missing root yields [],
                    // any(...) is False, so the entry is pruned. Match that behavior.
                    if (!rootToSlot.TryGetValue(key, out var slot) || slot <= latestFinalized.Slot.Value)
                    {
                        obsoleteTargets.Add(key);
                    }
                }

                foreach (var key in obsoleteTargets)
                {
                    justificationsMap.Remove(key);
                }
            }
            attestationsProcessingStopwatch.Stop();

            justificationsRoots = new List<Bytes32>(justificationsMap.Count);
            justificationsValidators = new List<bool>(justificationsMap.Count * validators.Count);
            foreach (var key in justificationsMap.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                var value = justificationsMap[key];
                if (value.Votes.Length != validators.Count)
                {
                    postState = default!;
                    reason = "Justification vote vector length does not match validator count.";
                    return false;
                }

                justificationsRoots.Add(value.Root);
                justificationsValidators.AddRange(value.Votes);
            }

            if ((ulong)justificationsValidators.Count > JustificationsValidatorsLimit)
            {
                postState = default!;
                reason = "Justifications validators bitlist exceeds protocol limit.";
                return false;
            }

            postState = new State(
                parentState.Config,
                new Slot(stateSlot),
                latestBlockHeader,
                latestJustified,
                latestFinalized,
                historicalBlockHashes,
                justifiedSlots,
                validators,
                justificationsRoots,
                justificationsValidators);

            finalizedAdvanced = latestFinalized.Slot.Value > finalizedSlotBeforeTransition;
            transitionSucceeded = true;
            reason = string.Empty;
            return true;
        }
        finally
        {
            if (slotsProcessingStopwatch.IsRunning)
            {
                slotsProcessingStopwatch.Stop();
            }

            if (attestationsProcessingStopwatch.IsRunning)
            {
                attestationsProcessingStopwatch.Stop();
            }

            if (blockProcessingStopwatch.IsRunning)
            {
                blockProcessingStopwatch.Stop();
            }

            transitionStopwatch.Stop();

            LeanMetrics.RecordStateTransition(
                transitionStopwatch.Elapsed,
                slotsProcessed,
                slotsProcessingStopwatch.Elapsed,
                blockProcessingStopwatch.Elapsed,
                attestationsProcessed,
                attestationsProcessingStopwatch.Elapsed);

            if (transitionSucceeded)
            {
                LeanMetrics.SetJustifiedSlot(latestJustified.Slot.Value);
                LeanMetrics.SetFinalizedSlot(latestFinalized.Slot.Value);
                if (finalizedAdvanced)
                {
                    LeanMetrics.RecordFinalizationResult(true);
                }
            }
            else
            {
                LeanMetrics.RecordFinalizationResult(false);
            }
        }
    }

    private IReadOnlyList<Validator> BuildGenesisValidators(ulong initialValidatorCount)
    {
        var validators = new List<Validator>();
        for (var index = 0; index < _config.GenesisValidatorPublicKeys.Count; index++)
        {
            var keyHex = _config.GenesisValidatorPublicKeys[index];
            if (!TryParseHexBytes(keyHex, out var bytes) || bytes.Length != SszEncoding.Bytes52Length)
            {
                bytes = new byte[SszEncoding.Bytes52Length];
            }

            validators.Add(new Validator(new Bytes52(bytes), (ulong)index));
        }

        var targetCount = validators.Count > 0
            ? (ulong)validators.Count
            : Math.Max(1UL, initialValidatorCount);
        while ((ulong)validators.Count < targetCount)
        {
            validators.Add(new Validator(Bytes52.Zero(), (ulong)validators.Count));
        }

        return validators;
    }

    private static bool TryBuildJustificationsMap(
        IReadOnlyList<Bytes32> roots,
        IReadOnlyList<bool> flattenedVotes,
        int validatorCount,
        out Dictionary<string, JustificationVotes> map,
        out string reason)
    {
        map = new Dictionary<string, JustificationVotes>(StringComparer.Ordinal);
        reason = string.Empty;

        if (validatorCount <= 0)
        {
            return true;
        }

        if (flattenedVotes.Count != roots.Count * validatorCount)
        {
            reason = $"Invalid justifications layout. roots={roots.Count} votes={flattenedVotes.Count} validators={validatorCount}.";
            return false;
        }

        for (var index = 0; index < roots.Count; index++)
        {
            var votes = new bool[validatorCount];
            var start = index * validatorCount;
            for (var voteIndex = 0; voteIndex < validatorCount; voteIndex++)
            {
                votes[voteIndex] = flattenedVotes[start + voteIndex];
            }

            map[ToKey(roots[index])] = new JustificationVotes(roots[index], votes);
        }

        return true;
    }

    private static bool TryIsSlotJustified(
        ulong slot,
        ulong finalizedSlot,
        IReadOnlyList<bool> justifiedSlots,
        out bool isJustified,
        out string reason)
    {
        if (slot <= finalizedSlot)
        {
            isJustified = true;
            reason = string.Empty;
            return true;
        }

        var index = new Slot(slot).JustifiedIndexAfter(new Slot(finalizedSlot));
        if (!index.HasValue)
        {
            isJustified = true;
            reason = string.Empty;
            return true;
        }

        if (index.Value >= justifiedSlots.Count)
        {
            isJustified = false;
            reason = $"Justified slot index {index.Value} is outside bitlist length {justifiedSlots.Count}.";
            return false;
        }

        isJustified = justifiedSlots[index.Value];
        reason = string.Empty;
        return true;
    }

    private static void EnsureBooleanCapacity(List<bool> values, int requiredLength)
    {
        while (values.Count < requiredLength)
        {
            values.Add(false);
        }
    }

    private static void ShiftLeft(List<bool> values, ulong delta)
    {
        if (delta == 0 || values.Count == 0)
        {
            return;
        }

        if (delta >= (ulong)values.Count)
        {
            values.Clear();
            return;
        }

        values.RemoveRange(0, (int)delta);
    }

    private static bool TryParseHexBytes(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if ((normalized.Length & 1) != 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(normalized.AsSpan());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToKey(Bytes32 root)
    {
        return Convert.ToHexString(root.AsSpan());
    }

    private sealed class JustificationVotes
    {
        public JustificationVotes(Bytes32 root, bool[] votes)
        {
            Root = root;
            Votes = votes;
        }

        public Bytes32 Root { get; }

        public bool[] Votes { get; }
    }
}
