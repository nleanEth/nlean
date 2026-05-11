using Lean.Consensus.TestDriver.Fixtures;
using Lean.Consensus.Types;

namespace Lean.Consensus.TestDriver.Drivers;

/// <summary>
/// Drives leanSpec state_transition fixtures end-to-end: builds genesis from
/// the fixture validators, advances state.slot to match pre.slot, then applies
/// each block via ChainStateTransition. Returns a structured Result that the
/// /test_driver/state_transition/run HTTP endpoint serializes back to hive.
/// </summary>
public static class StateTransitionDriver
{
    public readonly record struct PostSummary(
        ulong Slot,
        ulong LatestBlockHeaderSlot,
        string LatestBlockHeaderStateRoot,
        int HistoricalBlockHashesCount);

    public readonly record struct Result(bool Succeeded, string? Error, PostSummary? Post);

    public static Result Run(StateTransitionTest test)
    {
        var config = FixtureConverter.BuildConfigFromAnchor(test.Pre);
        var chainTransition = new ChainStateTransition(config);
        var state = chainTransition.CreateGenesisState(config.InitialValidatorCount);

        // Pre-advance to match the fixture's pre.slot. Mirrors leanSpec's
        // pre_state.process_slots(n) without replaying a block at each slot.
        while (state.Slot.Value < test.Pre.Slot)
        {
            var hdr = state.LatestBlockHeader;
            if (hdr.StateRoot.Equals(Bytes32.Zero()))
            {
                hdr = hdr with { StateRoot = new Bytes32(state.HashTreeRoot()) };
            }
            state = state with
            {
                LatestBlockHeader = hdr,
                Slot = new Slot(state.Slot.Value + 1),
            };
        }

        var sawFailure = false;
        string? failureReason = null;

        // Slot-monotonicity guard: empty-blocks fixtures with `expectException`
        // describe a process_slots(target == state.slot) rejection. Synthesize
        // an empty same-slot block so nlean's TryComputeStateRoot exercises
        // the same "target must be in the future" path.
        if (test.Blocks.Count == 0 && test.ExpectException is not null && state.Slot.Value > 0)
        {
            var emptyBlock = new Block(
                state.Slot,
                0UL,
                new Bytes32(state.LatestBlockHeader.HashTreeRoot()),
                Bytes32.Zero(),
                new BlockBody(Array.Empty<AggregatedAttestation>()));
            if (!chainTransition.TryComputeStateRoot(state, emptyBlock, out _, out _, out var reason))
            {
                sawFailure = true;
                failureReason = $"synthesized same-slot block (slot {state.Slot.Value}): {reason}";
            }
        }

        for (var i = 0; i < test.Blocks.Count; i++)
        {
            var block = FixtureConverter.ConvertBlock(test.Blocks[i]);
            if (!chainTransition.TryComputeStateRoot(state, block, out var computedRoot, out var postState, out var reason))
            {
                sawFailure = true;
                failureReason ??= $"block {i} (slot {block.Slot.Value}): {reason}";
                continue;
            }

            // Spec contract: block.state_root MUST equal hash_tree_root(post-state).
            // ream enforces this unconditionally and so do we — fixtures with
            // intentional mismatches (e.g. test_block_with_wrong_slot,
            // test_block_with_invalid_state_root) carry a placeholder zero
            // state_root that has to be rejected here for the expectException
            // path to fire.
            if (!block.StateRoot.Equals(computedRoot))
            {
                sawFailure = true;
                failureReason ??= $"block {i} (slot {block.Slot.Value}): state root mismatch";
                continue;
            }

            state = postState;
        }

        // hive contract: `succeeded == expectException.is_none()`. We just report
        // whether the state transition itself accepted every block; the caller
        // matches it against ExpectException on the fixture.
        if (sawFailure)
        {
            return new Result(false, failureReason, null);
        }

        var post = new PostSummary(
            Slot: state.Slot.Value,
            LatestBlockHeaderSlot: state.LatestBlockHeader.Slot.Value,
            LatestBlockHeaderStateRoot: "0x" + Convert.ToHexString(state.LatestBlockHeader.StateRoot.AsSpan()).ToLowerInvariant(),
            HistoricalBlockHashesCount: state.HistoricalBlockHashes.Count);
        return new Result(true, null, post);
    }
}
