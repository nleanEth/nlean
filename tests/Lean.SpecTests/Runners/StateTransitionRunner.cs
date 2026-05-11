using System.Text.Json;
using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Consensus.TestDriver.Fixtures;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class StateTransitionRunner : ISpecTestRunner
{
    private static readonly HashSet<string> KnownGaps = new(StringComparer.Ordinal);

    public void Run(string testId, string testJson)
    {
        foreach (var gap in KnownGaps)
        {
            if (testId.Contains(gap, StringComparison.Ordinal))
            {
                Assert.Ignore($"Known consensus-layer gap: {gap}. Tracked separately.");
                return;
            }
        }
        var test = JsonSerializer.Deserialize<StateTransitionTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize state transition test: {testId}");

        var config = ForkChoiceRunner.BuildConfigFromAnchor(test.Pre);
        var chainTransition = new ChainStateTransition(config);
        var state = chainTransition.CreateGenesisState(config.InitialValidatorCount);

        // Fixtures can pre-advance the state past slot 0 (via leanSpec's
        // pre_state.process_slots(n)) without replaying a block at each slot.
        // Walk nlean genesis forward the same way so later block.parentRoot
        // comparisons match.
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

        // Some fixtures (e.g. test_process_slots_target_equal_to_state_slot_rejected)
        // describe a slot-monotonicity invariant by leaving `blocks: []` and only
        // setting `expectException`. The Python spec test internally synthesizes
        // a BlockSpec at pre.slot to trigger process_slots(state, target=state.slot)
        // — that block isn't serialized to the JSON fixture. Mirror that by
        // synthesizing an empty block at the current state.slot so nlean's
        // TryComputeStateRoot exercises the same "target slot must be in the
        // future" rejection path.
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
            var block = ConvertBlock(test.Blocks[i]);
            if (!chainTransition.TryComputeStateRoot(state, block, out var computedRoot, out var postState, out var reason))
            {
                sawFailure = true;
                failureReason ??= $"block {i} (slot {block.Slot.Value}): {reason}";
                continue;
            }

            // Spec contract: block.state_root MUST equal hash_tree_root(post-state).
            // Enforce unconditionally so fixtures with a deliberately-wrong
            // (or placeholder zero) state_root reject as expected.
            if (!block.StateRoot.Equals(computedRoot))
            {
                sawFailure = true;
                failureReason ??= $"block {i} (slot {block.Slot.Value}): state root mismatch";
                continue;
            }

            state = postState;
        }

        if (test.ExpectException is not null)
        {
            Assert.That(sawFailure, Is.True,
                $"Expected transition to raise {test.ExpectException}, but every block applied.");
            return;
        }

        Assert.That(failureReason, Is.Null, $"Unexpected transition failure: {failureReason}");

        if (test.Post is null)
        {
            Assert.Fail("Fixture is missing both `post` and `expectException`.");
            return;
        }

        Assert.That(state.Slot.Value, Is.EqualTo(test.Post.Slot),
            "post state slot mismatch");

        if (test.Post.LatestBlockHeaderSlot.HasValue)
        {
            Assert.That(state.LatestBlockHeader.Slot.Value, Is.EqualTo(test.Post.LatestBlockHeaderSlot.Value),
                "post state latest block header slot mismatch");
        }

        if (test.Post.HistoricalBlockHashesCount.HasValue)
        {
            Assert.That((ulong)state.HistoricalBlockHashes.Count, Is.EqualTo(test.Post.HistoricalBlockHashesCount.Value),
                "historical block hashes count mismatch");
        }
    }

    private static Block ConvertBlock(TestBlock tb) => new(
        new Slot(tb.Slot),
        tb.ProposerIndex,
        new Bytes32(ParseHex(tb.ParentRoot)),
        new Bytes32(ParseHex(tb.StateRoot)),
        ConvertBlockBody(tb.Body));

    private static BlockBody ConvertBlockBody(TestBlockBody? body)
    {
        if (body?.Attestations?.Data is null or { Count: 0 })
            return new BlockBody(Array.Empty<AggregatedAttestation>());

        var attestations = body.Attestations.Data
            .Select(a => new AggregatedAttestation(
                new AggregationBits(a.AggregationBits.Data),
                new AttestationData(
                    new Slot(a.Data.Slot),
                    new Checkpoint(new Bytes32(ParseHex(a.Data.Head.Root)), new Slot(a.Data.Head.Slot)),
                    new Checkpoint(new Bytes32(ParseHex(a.Data.Target.Root)), new Slot(a.Data.Target.Slot)),
                    new Checkpoint(new Bytes32(ParseHex(a.Data.Source.Root)), new Slot(a.Data.Source.Slot)))))
            .ToList();

        return new BlockBody(attestations);
    }

    private static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
}
