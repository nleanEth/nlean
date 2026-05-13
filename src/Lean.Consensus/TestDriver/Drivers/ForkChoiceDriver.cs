using Lean.Consensus.ForkChoice;
using Lean.Consensus.TestDriver.Fixtures;
using Lean.Consensus.Types;
using Lean.Crypto;

namespace Lean.Consensus.TestDriver.Drivers;

/// <summary>
/// Stateful driver for /lean/v0/test_driver/fork_choice/{init,step}. Init builds
/// a fresh ProtoArrayForkChoiceStore from the fixture's anchor; subsequent
/// step calls feed block/tick/attestation steps and return a snapshot the
/// hive sim asserts against.
/// </summary>
public sealed class ForkChoiceDriver
{
    // Hive (ReamLabs/lean-spec-tests) only ships leanEnv=prod fixtures, and the
    // init request body is fixed by the upstream simulator with no leanEnv field,
    // so the HTTP driver hardcodes the prod-scheme verifier.
    private static readonly ILeanSig Signer = new RustLeanSig();

    private ProtoArrayForkChoiceStore? _store;
    private ChainStateTransition? _chainTransition;
    private ConsensusConfig? _config;
    private Bytes32 _anchorRoot;
    private readonly Dictionary<Bytes32, State> _stateByRoot = new();
    private readonly Dictionary<string, Bytes32> _blockRegistry = new(StringComparer.Ordinal);

    public readonly record struct Checkpoint(ulong Slot, string Root);

    public readonly record struct Snapshot(
        ulong HeadSlot,
        string HeadRoot,
        ulong Time,
        Checkpoint JustifiedCheckpoint,
        Checkpoint FinalizedCheckpoint,
        string SafeTarget);

    public readonly record struct StepResult(bool Accepted, string? Error, Snapshot Snapshot);

    public bool TryInit(InitRequest request, out string? error)
    {
        try
        {
            _config = FixtureConverter.BuildConfigFromAnchor(request.AnchorState);
            _chainTransition = new ChainStateTransition(_config);

            if (request.AnchorState.Slot == 0)
            {
                var genesisState = _chainTransition.CreateGenesisState(_config.InitialValidatorCount);
                _store = new ProtoArrayForkChoiceStore(_config);
                _anchorRoot = _store.FinalizedRoot;
                _stateByRoot[_anchorRoot] = genesisState;
            }
            else
            {
                var anchorState = FixtureConverter.ReconstructState(request.AnchorState);
                var anchorBlock = FixtureConverter.ConvertBlock(request.AnchorBlock);

                // leanSpec Store.from_anchor precondition: anchor block's stateRoot
                // must equal hash_tree_root(anchorState). A mismatched pair would
                // poison every future block→state lookup, so reject at init time.
                var expectedStateRoot = new Bytes32(anchorState.HashTreeRoot());
                if (!anchorBlock.StateRoot.Equals(expectedStateRoot))
                {
                    error = $"anchor block stateRoot {Convert.ToHexString(anchorBlock.StateRoot.AsSpan())} disagrees with anchor state root {Convert.ToHexString(expectedStateRoot.AsSpan())}";
                    return false;
                }

                _anchorRoot = new Bytes32(anchorBlock.HashTreeRoot());

                // leanSpec create_store_from_anchor: anchor block doubles as head + justified + finalized.
                var headState = new ConsensusHeadState(
                    request.AnchorBlock.Slot, _anchorRoot.AsSpan(),
                    request.AnchorBlock.Slot, _anchorRoot.AsSpan(),
                    request.AnchorBlock.Slot, _anchorRoot.AsSpan(),
                    request.AnchorBlock.Slot, _anchorRoot.AsSpan());
                _store = new ProtoArrayForkChoiceStore(_config, stateStore: new StubStateStore(headState));
                _stateByRoot[_anchorRoot] = anchorState;
            }

            _blockRegistry["genesis"] = _anchorRoot;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public StepResult ApplyStep(ForkChoiceStep step)
    {
        if (_store is null || _chainTransition is null || _config is null)
        {
            return new StepResult(false, "store not initialized; call /test_driver/fork_choice/init first", default);
        }

        try
        {
            switch (step.StepType)
            {
                case "block":
                    ApplyBlockStep(step);
                    break;
                case "tick":
                    ApplyTickStep(step);
                    break;
                case "attestation":
                    ApplyAttestationStep(step);
                    break;
                case "gossipAggregatedAttestation":
                    ApplyGossipAggregatedAttestationStep(step);
                    break;
                default:
                    return new StepResult(false, $"unsupported stepType '{step.StepType}'", CaptureSnapshot());
            }
        }
        catch (Exception ex)
        {
            return new StepResult(false, ex.Message, CaptureSnapshot());
        }

        return new StepResult(true, null, CaptureSnapshot());
    }

    private void ApplyBlockStep(ForkChoiceStep step)
    {
        var blockData = step.Block
            ?? throw new InvalidOperationException("block step missing block data");
        var block = FixtureConverter.ConvertBlock(blockData.ResolveBlock());
        var blockRoot = new Bytes32(block.HashTreeRoot());

        if (!string.IsNullOrEmpty(blockData.BlockRootLabel))
        {
            _blockRegistry[blockData.BlockRootLabel] = blockRoot;
        }

        if (!_stateByRoot.TryGetValue(block.ParentRoot, out var parentState))
        {
            // Hive treats unknown parent as a rejection if step.valid=true; surface
            // via thrown exception so ApplyStep returns accepted=false.
            throw new InvalidOperationException($"unknown parent root {Convert.ToHexString(block.ParentRoot.AsSpan())}");
        }

        if (!_chainTransition!.TryComputeStateRoot(parentState, block, out _, out var postState, out var reason))
        {
            throw new InvalidOperationException($"state transition rejected block at slot {block.Slot.Value}: {reason}");
        }
        _stateByRoot[blockRoot] = postState;

        // Mirror leanSpec filler's on_tick(block.slot*5, has_proposal=true).
        AdvanceToSlotStart(block.Slot.Value);

        // Empty signatures: hive driver doesn't replay signatures (those are
        // covered by verify_signatures suite). Build a placeholder per-attestation
        // proof so the count matches block.body.
        var proofCount = block.Body.Attestations.Count;
        var emptyProofs = new List<AggregatedSignatureProof>(proofCount);
        for (var i = 0; i < proofCount; i++)
        {
            var att = block.Body.Attestations[i];
            emptyProofs.Add(new AggregatedSignatureProof(att.AggregationBits, Array.Empty<byte>()));
        }
        var signedBlock = new SignedBlock(block, new BlockSignatures(emptyProofs, XmssSignature.Empty()));

        var result = _store!.OnBlock(
            signedBlock,
            postState.LatestJustified,
            postState.LatestFinalized,
            (ulong)postState.Validators.Count);

        if (!result.Accepted)
        {
            // DuplicateBlock from re-submit is a benign no-op in fixtures.
            if (result.RejectReason == ForkChoiceRejectReason.DuplicateBlock)
            {
                return;
            }
            throw new InvalidOperationException($"fork-choice rejected block: {result.RejectReason} — {result.Reason}");
        }
    }

    private void AdvanceToSlotStart(ulong targetSlot)
    {
        for (var interval = 2; interval < ProtoArrayForkChoiceStore.IntervalsPerSlot; interval++)
        {
            _store!.TickInterval(targetSlot == 0 ? 0 : targetSlot - 1, interval);
        }
        _store!.TickInterval(targetSlot, 0, hasProposal: true);
    }

    private void ApplyTickStep(ForkChoiceStep step)
    {
        var (targetInterval, hasProposal) = ResolveTickTarget(step);
        WalkTicksTo(targetInterval, hasProposal);
    }

    private (ulong TargetInterval, bool HasProposal) ResolveTickTarget(ForkChoiceStep step)
    {
        // New-style fixtures: {interval: <slot*5+intra>, hasProposal}.
        if (step.Interval.HasValue)
        {
            return (step.Interval.Value, step.HasProposal ?? false);
        }
        if (!step.Time.HasValue)
        {
            throw new InvalidOperationException("tick step missing time");
        }
        var time = step.Time.Value;
        var secondsPerSlot = (ulong)_config!.SecondsPerSlot;
        if (secondsPerSlot == 0)
        {
            return (_store!.CurrentTimeIntervals, step.HasProposal ?? false);
        }
        var intervalsPerSlot = (ulong)ProtoArrayForkChoiceStore.IntervalsPerSlot;
        var slotFromTime = time / secondsPerSlot;
        var intraSlotSeconds = time - slotFromTime * secondsPerSlot;
        var intervalDuration = (double)secondsPerSlot / ProtoArrayForkChoiceStore.IntervalsPerSlot;
        var intervalFromTime = intervalDuration > 0 ? (ulong)(intraSlotSeconds / intervalDuration) : 0UL;
        if (intervalFromTime > intervalsPerSlot - 1)
        {
            intervalFromTime = intervalsPerSlot - 1;
        }
        return (slotFromTime * intervalsPerSlot + intervalFromTime, step.HasProposal ?? false);
    }

    // leanSpec on_tick walks store.time forward one interval at a time so each
    // intermediate interval gets the appropriate action (e.g. interval-4 accept).
    // Only the final tick carries the fixture's hasProposal flag.
    private void WalkTicksTo(ulong targetInterval, bool hasProposalAtTarget)
    {
        var intervalsPerSlot = (ulong)ProtoArrayForkChoiceStore.IntervalsPerSlot;
        while (_store!.CurrentTimeIntervals < targetInterval)
        {
            var next = _store.CurrentTimeIntervals + 1;
            var slot = next / intervalsPerSlot;
            var intra = (int)(next % intervalsPerSlot);
            var hasProposal = next == targetInterval && hasProposalAtTarget;
            _store.TickInterval(slot, intra, hasProposal: hasProposal);
        }
    }

    private void ApplyAttestationStep(ForkChoiceStep step)
    {
        var att = step.Attestation
            ?? throw new InvalidOperationException("attestation step missing attestation");
        if (!att.ValidatorId.HasValue)
        {
            throw new InvalidOperationException("attestation step missing validatorId");
        }
        // Validator index is rejected at gossip time when the validator is not in
        // the active set. ProtoArrayForkChoiceStore.TryOnAttestation doesn't see
        // the validator set, so guard here using the anchor state's validator count.
        if (!_stateByRoot.TryGetValue(_anchorRoot, out var anchorState))
        {
            throw new InvalidOperationException("anchor state missing for attestation validation");
        }
        if (att.ValidatorId.Value >= (ulong)anchorState.Validators.Count)
        {
            throw new InvalidOperationException($"validator {att.ValidatorId.Value} not found in state");
        }
        var data = FixtureConverter.ConvertAttestationData(att.Data);

        // Gossip-time XMSS signature check. The fixture publishes the validator's
        // signature over hash_tree_root(data) at slot epoch. Reject if it doesn't
        // verify under the validator's attestation pubkey.
        if (!string.IsNullOrEmpty(att.Signature))
        {
            var pubkeyBytes = anchorState.Validators[(int)att.ValidatorId.Value].AttestationPubkey.AsSpan();
            var sigBytes = FixtureConverter.ParseHex(att.Signature);
            var dataRoot = data.HashTreeRoot();
            var epoch = checked((uint)data.Slot.Value);
            if (!Signer.Verify(pubkeyBytes, epoch, dataRoot, sigBytes))
            {
                throw new InvalidOperationException("attestation signature rejected");
            }
        }

        var signed = new SignedAttestation(new ValidatorIndex(att.ValidatorId.Value), data, XmssSignature.Empty());
        if (!_store!.TryOnAttestation(signed, storeSignature: false, out var reason))
        {
            throw new InvalidOperationException($"attestation rejected: {reason}");
        }
    }

    private void ApplyGossipAggregatedAttestationStep(ForkChoiceStep step)
    {
        var att = step.Attestation
            ?? throw new InvalidOperationException("gossipAggregatedAttestation step missing attestation");
        var proof = att.Proof
            ?? throw new InvalidOperationException("gossipAggregatedAttestation step missing proof");

        var data = FixtureConverter.ConvertAttestationData(att.Data);
        var signed = new SignedAggregatedAttestation(
            data,
            new AggregatedSignatureProof(
                new AggregationBits(proof.Participants.Data),
                FixtureConverter.ParseHex(proof.ProofData.Data)));

        // Spec leanSpec gossip-validation rejects payloads whose data is structurally
        // invalid (slot disparity, source>target, unknown blocks). Production gossip
        // stores speculatively, so the driver applies the spec rule explicitly.
        if (!_store!.TryValidateAttestationData(data, out var reason))
        {
            throw new InvalidOperationException($"gossipAggregatedAttestation rejected: {reason}");
        }
        if (!_store!.TryOnGossipAggregatedAttestation(signed, out reason))
        {
            throw new InvalidOperationException($"gossipAggregatedAttestation rejected: {reason}");
        }
    }

    private Snapshot CaptureSnapshot()
    {
        var s = _store!;
        return new Snapshot(
            HeadSlot: s.HeadSlot,
            HeadRoot: "0x" + Convert.ToHexString(s.HeadRoot.AsSpan()).ToLowerInvariant(),
            Time: s.CurrentTimeIntervals,
            JustifiedCheckpoint: new Checkpoint(s.JustifiedSlot,
                "0x" + Convert.ToHexString(s.JustifiedRoot.AsSpan()).ToLowerInvariant()),
            FinalizedCheckpoint: new Checkpoint(s.FinalizedSlot,
                "0x" + Convert.ToHexString(s.FinalizedRoot.AsSpan()).ToLowerInvariant()),
            SafeTarget: "0x" + Convert.ToHexString(s.SafeTarget.AsSpan()).ToLowerInvariant());
    }

    public readonly record struct InitRequest(TestState AnchorState, TestBlock AnchorBlock, ulong? GenesisTime);

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
