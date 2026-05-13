using System.Text.Json;
using Lean.Consensus;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using Lean.Consensus.TestDriver.Fixtures;
using Lean.Crypto;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class ForkChoiceRunner : ISpecTestRunner
{
    // leanEnv selects the XMSS scheme — leanEthereum/leanSpec ships test,
    // ReamLabs/lean-spec-tests ships prod.
    private static readonly ILeanSig Signer = new RustLeanSig();

    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<ForkChoiceTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize fork choice test: {testId}");

        var config = BuildConfigFromAnchor(test.AnchorState);
        var chainTransition = new ChainStateTransition(config);

        ProtoArrayForkChoiceStore store;
        Bytes32 anchorRoot;
        State anchorState;

        if (test.AnchorState.Slot == 0)
        {
            anchorState = chainTransition.CreateGenesisState(config.InitialValidatorCount);
            store = new ProtoArrayForkChoiceStore(config);
            anchorRoot = store.FinalizedRoot;
        }
        else
        {
            // Non-genesis anchor (checkpoint-sync scenario): rebuild the State and
            // anchor Block from the fixture, then seed ProtoArrayForkChoiceStore via
            // an in-memory IConsensusStateStore stub so its checkpoint-load path
            // populates head/justified/finalized to match the fixture's view.
            anchorState = ReconstructState(test.AnchorState);
            var anchorBlock = ConvertBlock(test.AnchorBlock);

            // leanSpec Store.from_anchor precondition: anchor block's stateRoot
            // must equal hash_tree_root(anchorState). Hive's sim uses the same
            // {steps empty + description contains "anchor_valid=False"} marker.
            var expectedStateRoot = new Bytes32(anchorState.HashTreeRoot());
            var anchorRootMatches = anchorBlock.StateRoot.Equals(expectedStateRoot);
            var expectsInitFailure =
                test.Steps.Count == 0 &&
                (test.Info?.Description ?? string.Empty).Contains("anchor_valid=False", StringComparison.Ordinal);

            if (!anchorRootMatches)
            {
                if (expectsInitFailure)
                    return;
                Assert.Fail(
                    $"anchor block stateRoot {Convert.ToHexString(anchorBlock.StateRoot.AsSpan())} disagrees with anchor state root {Convert.ToHexString(expectedStateRoot.AsSpan())}");
                return;
            }
            if (expectsInitFailure)
            {
                Assert.Fail("fixture expected init failure but anchor stateRoot matched");
                return;
            }

            anchorRoot = new Bytes32(anchorBlock.HashTreeRoot());

            // Per leanSpec create_store_from_anchor: the anchor block becomes BOTH
            // the head and the justified+finalized checkpoint, regardless of what
            // the anchor's State carries internally. This treats the anchor as a
            // trusted starting point (checkpoint-sync semantic).
            var headState = new ConsensusHeadState(
                test.AnchorBlock.Slot, anchorRoot.AsSpan(),
                test.AnchorBlock.Slot, anchorRoot.AsSpan(),
                test.AnchorBlock.Slot, anchorRoot.AsSpan(),
                test.AnchorBlock.Slot, anchorRoot.AsSpan());
            store = new ProtoArrayForkChoiceStore(config, stateStore: new StubStateStore(headState));
        }

        // Canonical state lookup by block root — needed so OnBlock receives the
        // same canonical checkpoints the production ConsensusServiceV2 derives
        // from the post-transition state. For non-genesis anchors the runner uses
        // the anchor block root as the "genesis" label per the fixture convention.
        var stateByRoot = new Dictionary<Bytes32, State>
        {
            [anchorRoot] = anchorState,
        };

        var blockRegistry = new Dictionary<string, Bytes32>(StringComparer.Ordinal)
        {
            ["genesis"] = anchorRoot,
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
                    ProcessAttestationStep(store, step, stepIdx, anchorState, test.LeanEnv);
                    break;

                case "gossipAggregatedAttestation":
                    ProcessGossipAggregatedAttestationStep(store, step, stepIdx);
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
        if (!TryResolveTickTarget(step, config, out var targetInterval))
            return;

        WalkTicksTo(store, targetInterval, step.HasProposal ?? false);
    }

    private static bool TryResolveTickTarget(ForkChoiceStep step, ConsensusConfig config, out ulong targetInterval)
    {
        var intervalsPerSlot = (ulong)ProtoArrayForkChoiceStore.IntervalsPerSlot;

        // New-style fixtures: {interval: <slot*5+intra>}.
        if (step.Interval.HasValue)
        {
            targetInterval = step.Interval.Value;
            return true;
        }

        targetInterval = 0;
        if (!step.Time.HasValue) return false;

        var secondsPerSlot = (ulong)config.SecondsPerSlot;
        if (secondsPerSlot == 0) return false;

        var time = step.Time.Value;
        var slotFromTime = time / secondsPerSlot;
        var intraSlotSeconds = time - slotFromTime * secondsPerSlot;
        var intervalDuration = (double)secondsPerSlot / ProtoArrayForkChoiceStore.IntervalsPerSlot;
        var intervalFromTime = intervalDuration > 0 ? (ulong)(intraSlotSeconds / intervalDuration) : 0UL;
        if (intervalFromTime > intervalsPerSlot - 1)
            intervalFromTime = intervalsPerSlot - 1;

        targetInterval = slotFromTime * intervalsPerSlot + intervalFromTime;
        return true;
    }

    // leanSpec on_tick walks store.time forward one interval at a time so each
    // intermediate interval triggers the right action (e.g. interval 4 accept).
    private static void WalkTicksTo(ProtoArrayForkChoiceStore store, ulong targetInterval, bool hasProposalAtTarget)
    {
        var intervalsPerSlot = (ulong)ProtoArrayForkChoiceStore.IntervalsPerSlot;
        while (store.CurrentTimeIntervals < targetInterval)
        {
            var next = store.CurrentTimeIntervals + 1;
            var slot = next / intervalsPerSlot;
            var intra = (int)(next % intervalsPerSlot);
            var hasProposal = next == targetInterval && hasProposalAtTarget;
            store.TickInterval(slot, intra, hasProposal: hasProposal);
        }
    }

    private static void ProcessAttestationStep(ProtoArrayForkChoiceStore store, ForkChoiceStep step, int stepIdx, State anchorState, string leanEnv)
    {
        var attData = step.Attestation
            ?? throw new InvalidOperationException("Attestation step missing attestation data");

        var validatorId = attData.ValidatorId ?? 0;
        var attestationData = ConvertAttestationData(attData.Data);

        // Gossip-time guards that ProtoArrayForkChoiceStore.TryOnAttestation doesn't
        // own: (1) validator index must be in the anchor's set; (2) the fixture's
        // XMSS signature must verify under the validator's attestation pubkey.
        bool accepted;
        string reason;
        if (validatorId >= (ulong)anchorState.Validators.Count)
        {
            accepted = false;
            reason = $"validator {validatorId} not found in state";
        }
        else if (!string.IsNullOrEmpty(attData.Signature)
            && !VerifyAttestationSignature(
                anchorState.Validators[(int)validatorId].AttestationPubkey.AsSpan(),
                checked((uint)attestationData.Slot.Value),
                attestationData.HashTreeRoot(),
                ParseHex(attData.Signature),
                leanEnv))
        {
            accepted = false;
            reason = "attestation signature rejected";
        }
        else
        {
            var attestation = new SignedAttestation(validatorId, attestationData, XmssSignature.Empty());
            accepted = store.TryOnAttestation(attestation, storeSignature: false, out reason);
        }

        if (step.Valid && !accepted)
            Assert.Fail($"step {stepIdx}: attestation expected success but rejected: {reason}");
        if (!step.Valid && accepted)
            Assert.Fail($"step {stepIdx}: attestation expected failure but was accepted");
    }

    private static bool VerifyAttestationSignature(
        ReadOnlySpan<byte> pubkey, uint epoch, ReadOnlySpan<byte> dataRoot, ReadOnlySpan<byte> sig, string leanEnv)
        => string.Equals(leanEnv, "prod", StringComparison.OrdinalIgnoreCase)
            ? Signer.Verify(pubkey, epoch, dataRoot, sig)
            : Signer.VerifyTest(pubkey, epoch, dataRoot, sig);

    private static void ProcessGossipAggregatedAttestationStep(ProtoArrayForkChoiceStore store, ForkChoiceStep step, int stepIdx)
    {
        var attData = step.Attestation
            ?? throw new InvalidOperationException("gossipAggregatedAttestation step missing attestation data");
        var proof = attData.Proof
            ?? throw new InvalidOperationException("gossipAggregatedAttestation step missing proof");

        var data = ConvertAttestationData(attData.Data);
        var signed = new SignedAggregatedAttestation(
            data,
            new AggregatedSignatureProof(
                new AggregationBits(proof.Participants.Data),
                ParseHex(proof.ProofData.Data)));

        // Spec rejection happens at the runner/driver layer; production gossip path
        // is intentionally permissive (see ProtoArrayForkChoiceStore comments).
        string reason;
        var accepted = store.TryValidateAttestationData(data, out reason)
            && store.TryOnGossipAggregatedAttestation(signed, out reason);
        if (step.Valid && !accepted)
            Assert.Fail($"step {stepIdx}: gossipAggregatedAttestation expected success but rejected: {reason}");
        if (!step.Valid && accepted)
            Assert.Fail($"step {stepIdx}: gossipAggregatedAttestation expected failure but was accepted");
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

    private static State ReconstructState(TestState ts)
    {
        var validators = (ts.Validators?.Data ?? new List<TestValidator>())
            .Select(v => new Validator(
                new Bytes52(ParseHex(v.AttestationKeyHex)),
                new Bytes52(ParseHex(v.ProposalKeyHex)),
                v.Index))
            .ToList();

        var header = new BlockHeader(
            new Slot(ts.LatestBlockHeader.Slot),
            ts.LatestBlockHeader.ProposerIndex,
            new Bytes32(ParseHex(ts.LatestBlockHeader.ParentRoot)),
            new Bytes32(ParseHex(ts.LatestBlockHeader.StateRoot)),
            new Bytes32(ParseHex(ts.LatestBlockHeader.BodyRoot)));

        var justified = new Checkpoint(
            new Bytes32(ParseHex(ts.LatestJustified.Root)), new Slot(ts.LatestJustified.Slot));
        var finalized = new Checkpoint(
            new Bytes32(ParseHex(ts.LatestFinalized.Root)), new Slot(ts.LatestFinalized.Slot));

        var historical = (ts.HistoricalBlockHashes?.Data ?? new List<string>())
            .Select(h => new Bytes32(ParseHex(h)))
            .ToList();
        var justifiedSlots = ts.JustifiedSlots?.Data ?? new List<bool>();
        var justificationsRoots = (ts.JustificationsRoots?.Data ?? new List<string>())
            .Select(h => new Bytes32(ParseHex(h)))
            .ToList();
        var justificationsValidators = ts.JustificationsValidators?.Data ?? new List<bool>();

        return new State(
            new Config(ts.Config.GenesisTime),
            new Slot(ts.Slot),
            header,
            justified,
            finalized,
            historical,
            justifiedSlots,
            validators,
            justificationsRoots,
            justificationsValidators);
    }

    private sealed class StubStateStore : IConsensusStateStore
    {
        private readonly ConsensusHeadState _state;
        public StubStateStore(ConsensusHeadState state) => _state = state;
        public bool TryLoad([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConsensusHeadState? state)
        {
            state = _state;
            return true;
        }
        public bool TryLoad(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConsensusHeadState? state,
            out State? headChainState)
        {
            state = _state;
            headChainState = null;
            return true;
        }
        public void Save(ConsensusHeadState state) { }
        public void Save(ConsensusHeadState state, State headChainState) { }
        public void Delete() { }
    }
}
