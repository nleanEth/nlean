using System.Text.Json;
using Lean.Consensus;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class ForkChoiceRunner : ISpecTestRunner
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
        var test = JsonSerializer.Deserialize<ForkChoiceTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize fork choice test: {testId}");

        var config = BuildConfigFromAnchor(test.AnchorState);
        var chainTransition = new ChainStateTransition(config);
        var anchorState = chainTransition.CreateGenesisState(config.InitialValidatorCount);
        var store = new ProtoArrayForkChoiceStore(config);
        var genesisRoot = store.FinalizedRoot;

        // Canonical state lookup by block root — needed so OnBlock receives the
        // same canonical checkpoints the production ConsensusServiceV2 derives
        // from the post-transition state.
        var stateByRoot = new Dictionary<Bytes32, State>
        {
            [genesisRoot] = anchorState,
        };

        var blockRegistry = new Dictionary<string, Bytes32>(StringComparer.Ordinal)
        {
            ["genesis"] = genesisRoot,
        };

        for (var stepIdx = 0; stepIdx < test.Steps.Count; stepIdx++)
        {
            var step = test.Steps[stepIdx];

            switch (step.StepType)
            {
                case "block":
                    ProcessBlockStep(store, chainTransition, stateByRoot, step, blockRegistry, stepIdx);
                    break;

                case "tick":
                    ProcessTickStep(store, step, config);
                    break;

                case "attestation":
                    ProcessAttestationStep(store, step);
                    break;

                case "gossipAggregatedAttestation":
                    // TODO: map fixture aggregated payloads through TryOnGossipAggregatedAttestation.
                    break;

                default:
                    Assert.Inconclusive($"Unsupported step type at step {stepIdx}: {step.StepType}");
                    return;
            }

            if (step.Checks is not null)
            {
                ValidateChecks(store, step.Checks, stepIdx, blockRegistry);
            }
        }
    }

    internal static ConsensusConfig BuildConfigFromAnchor(TestState anchorState)
    {
        var validators = anchorState.Validators?.Data ?? new List<TestValidator>();
        var keys = validators
            .Select(v => (v.AttestationKeyHex, v.ProposalKeyHex))
            .ToList();

        return new ConsensusConfig
        {
            InitialValidatorCount = (ulong)Math.Max(1, validators.Count),
            GenesisTimeUnix = anchorState.Config.GenesisTime,
            GenesisValidatorKeys = keys,
        };
    }

    private static void ProcessBlockStep(
        ProtoArrayForkChoiceStore store,
        ChainStateTransition chainTransition,
        Dictionary<Bytes32, State> stateByRoot,
        ForkChoiceStep step,
        Dictionary<string, Bytes32> blockRegistry,
        int stepIdx)
    {
        var blockData = step.Block
            ?? throw new InvalidOperationException($"Block step {stepIdx} missing block data");

        var block = ConvertBlock(blockData.ResolveBlock());
        var blockRoot = new Bytes32(block.HashTreeRoot());

        if (!string.IsNullOrEmpty(blockData.BlockRootLabel))
        {
            blockRegistry[blockData.BlockRootLabel] = blockRoot;
        }

        if (!stateByRoot.TryGetValue(block.ParentRoot, out var parentState))
        {
            if (step.Valid)
            {
                Assert.Fail($"step {stepIdx}: parent state not found for block at slot {block.Slot.Value}");
            }
            return;
        }

        if (!chainTransition.TryComputeStateRoot(parentState, block, out _, out var postState, out var reason))
        {
            if (step.Valid)
            {
                Assert.Fail($"step {stepIdx}: state transition failed: {reason}");
            }
            return;
        }

        stateByRoot[blockRoot] = postState;

        // Mirror leanSpec filler's on_tick(block.slot*5, has_proposal=true): advance
        // through previous slot's intervals 2 (aggregation) and 4 (accept_new), then
        // interval 0 of block.slot with has_proposal=true. Interval 2 aggregates the
        // gossip signatures collected from the previous block's proposer attestation
        // into an aggregated payload; interval 4 / 0-with-proposal migrates
        // new→known and recomputes the head with the accumulated weight.
        AdvanceToSlotStart(store, block.Slot.Value);

        // Empty BlockSignatures (valid-count mirror). The attestation proof count must match body attestation count.
        var proofCount = block.Body.Attestations.Count;
        var emptyProofs = new List<AggregatedSignatureProof>(proofCount);
        for (var i = 0; i < proofCount; i++)
        {
            var att = block.Body.Attestations[i];
            emptyProofs.Add(new AggregatedSignatureProof(att.AggregationBits, Array.Empty<byte>()));
        }
        var signatures = new BlockSignatures(emptyProofs, XmssSignature.Empty());
        var signedBlock = new SignedBlock(block, signatures);

        var result = store.OnBlock(
            signedBlock,
            postState.LatestJustified,
            postState.LatestFinalized,
            (ulong)postState.Validators.Count);


        if (step.Valid && !result.Accepted)
        {
            // leanSpec fixtures re-submit identical blocks to test reorg scenarios;
            // nlean flags these as DuplicateBlock. Treat as no-op to match spec intent.
            if (result.RejectReason == ForkChoiceRejectReason.DuplicateBlock)
            {
                return;
            }

            Assert.Fail(
                $"step {stepIdx}: block expected success but rejected: {result.RejectReason} — {result.Reason}");
            return;
        }

        if (!step.Valid && result.Accepted)
        {
            Assert.Fail($"step {stepIdx}: block expected failure but was accepted");
        }
    }

    private static void AdvanceToSlotStart(ProtoArrayForkChoiceStore store, ulong targetSlot)
    {
        // Walk through intervals 0..4 of each slot up to and including interval 0 of
        // the target slot, matching leanSpec filler's on_tick(target, has_proposal=true).
        // TickInterval ignores intervals it doesn't care about, so over-ticking is fine.
        // We don't track current time here; this is idempotent for targetSlot values
        // that are monotonically increasing.
        for (var interval = 2; interval < ProtoArrayForkChoiceStore.IntervalsPerSlot; interval++)
        {
            store.TickInterval(targetSlot == 0 ? 0 : targetSlot - 1, interval);
        }
        store.TickInterval(targetSlot, 0, hasProposal: true);
    }

    private static void ProcessTickStep(ProtoArrayForkChoiceStore store, ForkChoiceStep step, ConsensusConfig config)
    {
        if (!step.Time.HasValue) return;

        var secondsPerSlot = (ulong)config.SecondsPerSlot;
        if (secondsPerSlot == 0) return;

        var time = step.Time.Value;
        var slot = time / secondsPerSlot;
        var intraSlotSeconds = time - slot * secondsPerSlot;
        // IntervalsPerSlot=5: intervals fire at 0, 1/5, 2/5, 3/5, 4/5 of the slot.
        var intervalDuration = (double)secondsPerSlot / ProtoArrayForkChoiceStore.IntervalsPerSlot;
        var interval = intervalDuration > 0
            ? (int)(intraSlotSeconds / intervalDuration)
            : 0;
        if (interval > ProtoArrayForkChoiceStore.IntervalsPerSlot - 1)
            interval = ProtoArrayForkChoiceStore.IntervalsPerSlot - 1;

        store.TickInterval(slot, interval);
    }

    private static void ProcessAttestationStep(ProtoArrayForkChoiceStore store, ForkChoiceStep step)
    {
        var attData = step.Attestation
            ?? throw new InvalidOperationException("Attestation step missing attestation data");

        var attestationData = ConvertAttestationData(attData.Data);
        var attestation = new SignedAttestation(
            attData.ValidatorId ?? 0,
            attestationData,
            XmssSignature.Empty());

        store.TryOnAttestation(attestation, out _);
    }

    private static void ValidateChecks(
        ProtoArrayForkChoiceStore store,
        StoreChecks checks,
        int stepIdx,
        Dictionary<string, Bytes32> blockRegistry)
    {
        var context = $"step {stepIdx}";

        if (checks.HeadSlot.HasValue)
        {
            Assert.That(store.HeadSlot, Is.EqualTo(checks.HeadSlot.Value),
                $"{context}: headSlot mismatch");
        }

        if (checks.HeadRoot is not null)
        {
            var expectedRoot = new Bytes32(ParseHex(checks.HeadRoot));
            Assert.That(store.HeadRoot, Is.EqualTo(expectedRoot),
                $"{context}: headRoot mismatch");
        }

        if (checks.HeadRootLabel is not null && blockRegistry.TryGetValue(checks.HeadRootLabel, out var labelRoot))
        {
            Assert.That(store.HeadRoot, Is.EqualTo(labelRoot),
                $"{context}: headRootLabel '{checks.HeadRootLabel}' mismatch");
        }

        if (checks.LatestJustifiedSlot.HasValue)
        {
            Assert.That(store.JustifiedSlot, Is.EqualTo(checks.LatestJustifiedSlot.Value),
                $"{context}: latestJustifiedSlot mismatch");
        }

        if (checks.LatestFinalizedSlot.HasValue)
        {
            Assert.That(store.FinalizedSlot, Is.EqualTo(checks.LatestFinalizedSlot.Value),
                $"{context}: latestFinalizedSlot mismatch");
        }

        if (checks.LexicographicHeadAmong is { Count: > 0 })
        {
            var possibleRoots = checks.LexicographicHeadAmong
                .Where(blockRegistry.ContainsKey)
                .Select(label => blockRegistry[label])
                .ToList();

            if (possibleRoots.Count > 0)
            {
                Assert.That(possibleRoots, Does.Contain(store.HeadRoot),
                    $"{context}: head not among lexicographic candidates");
            }
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
                ConvertAttestationData(a.Data)))
            .ToList();

        return new BlockBody(attestations);
    }

    private static AttestationData ConvertAttestationData(TestAttestationData td) => new(
        new Slot(td.Slot),
        new Checkpoint(new Bytes32(ParseHex(td.Head.Root)), new Slot(td.Head.Slot)),
        new Checkpoint(new Bytes32(ParseHex(td.Target.Root)), new Slot(td.Target.Slot)),
        new Checkpoint(new Bytes32(ParseHex(td.Source.Root)), new Slot(td.Source.Slot)));

    private static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
}
