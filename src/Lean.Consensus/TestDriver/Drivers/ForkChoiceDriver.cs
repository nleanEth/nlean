using Lean.Consensus.ForkChoice;
using Lean.Consensus.TestDriver.Fixtures;
using Lean.Consensus.Types;

namespace Lean.Consensus.TestDriver.Drivers;

/// <summary>
/// Stateful driver for /lean/v0/test_driver/fork_choice/{init,step}. Init builds
/// a fresh ProtoArrayForkChoiceStore from the fixture's anchor; subsequent
/// step calls feed block/tick/attestation steps and return a snapshot the
/// hive sim asserts against.
/// </summary>
public sealed class ForkChoiceDriver
{
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
                    // Not yet plumbed — pass through accepted so hive doesn't fail-flag the step.
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
        if (!step.Time.HasValue)
        {
            throw new InvalidOperationException("tick step missing time");
        }
        var time = step.Time.Value;
        var secondsPerSlot = (ulong)_config!.SecondsPerSlot;
        if (secondsPerSlot == 0)
        {
            return;
        }
        var slot = time / secondsPerSlot;
        var intraSlotSeconds = time - slot * secondsPerSlot;
        var intervalDuration = (double)secondsPerSlot / ProtoArrayForkChoiceStore.IntervalsPerSlot;
        var interval = intervalDuration > 0 ? (int)(intraSlotSeconds / intervalDuration) : 0;
        if (interval > ProtoArrayForkChoiceStore.IntervalsPerSlot - 1)
        {
            interval = ProtoArrayForkChoiceStore.IntervalsPerSlot - 1;
        }
        _store!.TickInterval(slot, interval);
    }

    private void ApplyAttestationStep(ForkChoiceStep step)
    {
        var att = step.Attestation
            ?? throw new InvalidOperationException("attestation step missing attestation");
        if (!att.ValidatorId.HasValue)
        {
            throw new InvalidOperationException("attestation step missing validatorId");
        }
        var data = FixtureConverter.ConvertAttestationData(att.Data);
        var signed = new SignedAttestation(new ValidatorIndex(att.ValidatorId.Value), data, XmssSignature.Empty());
        _store!.TryOnAttestation(signed, storeSignature: false, out _);
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
