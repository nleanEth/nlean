using Lean.Consensus.Types;
using Lean.Crypto;

namespace Lean.Consensus;

public sealed class ForkChoiceStore
{
    public const int IntervalsPerSlot = 4;

    private readonly Dictionary<string, Block> _blocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ForkChoiceNodeState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, State> _chainStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _chainStateFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, AttestationData> _latestKnownAttestations = new();
    private readonly Dictionary<ulong, AttestationData> _latestPendingAttestations = new();
    private readonly IForkChoiceStateTransition _stateTransition;
    private readonly ChainStateTransition _chainStateTransition;
    private readonly bool _preferChainStateTransition;
    private readonly ILeanSig? _leanSig;
    private readonly ILeanMultiSig? _leanMultiSig;
    private readonly ulong _initialValidatorCount;
    private readonly Bytes32 _canonicalGenesisRoot;
    private readonly string _genesisKey;
    private string _startKey;
    private string _headKey;
    private ulong _headSlot;
    private Bytes32 _headRoot = Bytes32.Zero();
    private Checkpoint _latestJustified = Checkpoint.Default();
    private Checkpoint _latestFinalized = Checkpoint.Default();
    private Bytes32 _safeTarget = Bytes32.Zero();
    private ulong _safeTargetSlot;

    public ForkChoiceStore()
        : this(new ForkChoiceStateTransition(new ConsensusConfig()), new ConsensusConfig(), null, null)
    {
    }

    public ForkChoiceStore(IForkChoiceStateTransition stateTransition)
        : this(stateTransition, new ConsensusConfig(), null, null)
    {
    }

    public ForkChoiceStore(IForkChoiceStateTransition stateTransition, ConsensusConfig config)
        : this(stateTransition, config, null, null)
    {
    }

    public ForkChoiceStore(
        IForkChoiceStateTransition stateTransition,
        ConsensusConfig config,
        ILeanSig? leanSig,
        ILeanMultiSig? leanMultiSig)
    {
        ArgumentNullException.ThrowIfNull(stateTransition);
        ArgumentNullException.ThrowIfNull(config);
        _stateTransition = stateTransition;
        _chainStateTransition = new ChainStateTransition(config);
        _preferChainStateTransition = stateTransition is ForkChoiceStateTransition;
        _leanSig = leanSig;
        _leanMultiSig = leanMultiSig;
        _initialValidatorCount = Math.Max(1UL, config.InitialValidatorCount);
        var genesisState = _chainStateTransition.CreateGenesisState(_initialValidatorCount);
        _canonicalGenesisRoot = ComputeCanonicalGenesisRoot(genesisState);
        _headRoot = _canonicalGenesisRoot;
        _headKey = ToKey(_headRoot);
        _genesisKey = _headKey;
        _startKey = _genesisKey;
        _blocks[_headKey] = CreateAnchorBlock(0, _headRoot);

        var genesisCheckpoint = new Checkpoint(_headRoot, new Slot(0));
        _states[_headKey] = new ForkChoiceNodeState(genesisCheckpoint, genesisCheckpoint, _initialValidatorCount);
        _chainStates[_headKey] = genesisState;
        _latestJustified = genesisCheckpoint;
        _latestFinalized = genesisCheckpoint;
        _safeTarget = _headRoot;
        _safeTargetSlot = 0;
    }

    public ulong HeadSlot => _headSlot;

    public Bytes32 HeadRoot => _headRoot;

    public Checkpoint LatestJustified => _latestJustified;

    public Checkpoint LatestFinalized => _latestFinalized;

    public Bytes32 SafeTarget => _safeTarget;

    public ulong SafeTargetSlot => _safeTargetSlot;

    public AttestationData CreateAttestationData(ulong slot, int safeTargetLookbackSlots)
    {
        var source = _latestJustified;
        var head = new Checkpoint(_headRoot, new Slot(_headSlot));
        var target = SelectAttestationTargetCheckpoint(safeTargetLookbackSlots);
        if (target.Slot.Value < source.Slot.Value ||
            !IsSlotJustifiableAfterFinalized(target.Slot.Value))
        {
            target = source;
        }

        return new AttestationData(new Slot(slot), head, target, source);
    }

    public ConsensusHeadState CreateHeadState()
    {
        return new ConsensusHeadState(
            _headSlot,
            _headRoot.AsSpan(),
            _latestJustified.Slot.Value,
            _latestJustified.Root.AsSpan(),
            _latestFinalized.Slot.Value,
            _latestFinalized.Root.AsSpan(),
            _safeTargetSlot,
            _safeTarget.AsSpan());
    }

    public bool ContainsBlock(Bytes32 root)
    {
        return _blocks.ContainsKey(ToKey(root));
    }

    public void InitializeHead(ConsensusHeadState state)
    {
        if (state.HeadRoot.Length != SszEncoding.Bytes32Length)
        {
            return;
        }

        _blocks.Clear();
        _states.Clear();
        _chainStates.Clear();
        _chainStateFailures.Clear();
        _latestKnownAttestations.Clear();
        _latestPendingAttestations.Clear();

        _blocks[_genesisKey] = CreateAnchorBlock(0, _canonicalGenesisRoot);
        var genesisCheckpoint = new Checkpoint(_canonicalGenesisRoot, new Slot(0));
        _states[_genesisKey] = new ForkChoiceNodeState(genesisCheckpoint, genesisCheckpoint, _initialValidatorCount);
        _chainStates[_genesisKey] = _chainStateTransition.CreateGenesisState(_initialValidatorCount);

        var anchorRoot = new Bytes32(state.HeadRoot);
        _headRoot = anchorRoot;
        _headSlot = state.HeadSlot;
        _headKey = ToKey(anchorRoot);
        _startKey = _headKey;
        _blocks[_headKey] = CreateAnchorBlock(state.HeadSlot, anchorRoot);

        var anchorCheckpoint = new Checkpoint(anchorRoot, new Slot(state.HeadSlot));
        _states[_headKey] = new ForkChoiceNodeState(anchorCheckpoint, anchorCheckpoint, _initialValidatorCount);
        if (state.HeadSlot == 0)
        {
            // After adopting a peer-provided genesis anchor root, keep a chain-state snapshot
            // under that anchor key so post-genesis state transitions can proceed.
            _chainStates[_headKey] = _chainStateTransition.CreateGenesisState(_initialValidatorCount);
        }

        _latestJustified = ToCheckpointOrFallback(state.LatestJustifiedSlot, state.LatestJustifiedRoot, anchorCheckpoint);
        if (_latestJustified.Slot.Value > _headSlot)
        {
            _latestJustified = anchorCheckpoint;
        }

        _latestFinalized = ToCheckpointOrFallback(state.LatestFinalizedSlot, state.LatestFinalizedRoot, _latestJustified);
        if (_latestFinalized.Slot.Value > _latestJustified.Slot.Value || _latestFinalized.Slot.Value > _headSlot)
        {
            _latestFinalized = _latestJustified;
        }

        _safeTarget = ToRootOrFallback(state.SafeTargetRoot, _latestJustified.Root);
        _safeTargetSlot = state.SafeTargetSlot;
        if (_safeTargetSlot > _headSlot)
        {
            _safeTargetSlot = _headSlot;
        }
    }

    public bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out string reason)
    {
        ArgumentNullException.ThrowIfNull(candidateBlock);

        var parentKey = ToKey(candidateBlock.ParentRoot);
        if (!_chainStates.TryGetValue(parentKey, out var parentState))
        {
            stateRoot = Bytes32.Zero();
            if (_chainStateFailures.TryGetValue(parentKey, out var parentFailure))
            {
                reason = $"Missing chain state snapshot for parent root {parentKey}. Parent transition failure: {parentFailure}";
                return false;
            }

            reason = $"Missing chain state snapshot for parent root {parentKey}.";
            return false;
        }

        return _chainStateTransition.TryComputeStateRoot(
            parentState,
            candidateBlock,
            out stateRoot,
            out _,
            out reason);
    }

    public ForkChoiceApplyResult ApplyGossipAttestation(SignedAttestation signedAttestation, ulong currentSlot)
    {
        ArgumentNullException.ThrowIfNull(signedAttestation);

        if (!TryValidateAttestationData(
                signedAttestation.Message,
                currentSlot,
                allowUnknownRoots: false,
                out var reason,
                enforceChainTopology: false))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Invalid gossip attestation: {reason}",
                _headSlot,
                _headRoot);
        }

        if (signedAttestation.Message.Slot.Value > currentSlot)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Invalid gossip attestation: attestation slot {signedAttestation.Message.Slot.Value} exceeds current slot {currentSlot}.",
                _headSlot,
                _headRoot);
        }

        var targetKey = ToKey(signedAttestation.Message.Target.Root);
        if (!_states.TryGetValue(targetKey, out var targetState))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"No state snapshot for attestation target {targetKey}.",
                _headSlot,
                _headRoot);
        }

        if (!TryVerifyGossipAttestationSignature(signedAttestation, out var signatureReason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Invalid gossip attestation: {signatureReason}",
                _headSlot,
                _headRoot);
        }

        if (signedAttestation.ValidatorId >= targetState.ValidatorCount)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Attestation validator {signedAttestation.ValidatorId} is out of range for target state validator count {targetState.ValidatorCount}.",
                _headSlot,
                _headRoot);
        }

        UpsertAttestation(_latestPendingAttestations, signedAttestation.ValidatorId, signedAttestation.Message);
        return ForkChoiceApplyResult.AcceptedResult(false, _headSlot, _headRoot);
    }

    public ForkChoiceApplyResult OnSlotTick(ulong currentSlot)
    {
        // A full slot tick applies interval-2 + interval-3 effects.
        var previousHeadKey = _headKey;
        UpdateSafeTarget();
        PromotePendingAttestations();
        UpdateHeadFromKnownAttestations();
        return ForkChoiceApplyResult.AcceptedResult(previousHeadKey != _headKey, _headSlot, _headRoot);
    }

    public ForkChoiceApplyResult OnIntervalTick(ulong currentSlot, int intervalInSlot)
    {
        var previousHeadKey = _headKey;
        var normalizedInterval = NormalizeInterval(intervalInSlot);
        if (normalizedInterval == 2)
        {
            // Interval 2: update safe target from pending/new attestations.
            UpdateSafeTarget();
        }
        else if (normalizedInterval == 3)
        {
            // Interval 3: accept pending/new attestations and update head.
            PromotePendingAttestations();
            UpdateHeadFromKnownAttestations();
        }

        return ForkChoiceApplyResult.AcceptedResult(previousHeadKey != _headKey, _headSlot, _headRoot);
    }

    public ForkChoiceApplyResult PrepareProposalHead()
    {
        var previousHeadKey = _headKey;
        PromotePendingAttestations();
        UpdateHeadFromKnownAttestations();
        return ForkChoiceApplyResult.AcceptedResult(previousHeadKey != _headKey, _headSlot, _headRoot);
    }

    public ForkChoiceApplyResult ApplyBlock(
        SignedBlockWithAttestation signedBlock,
        Bytes32 blockRoot,
        ulong currentSlot)
    {
        ArgumentNullException.ThrowIfNull(signedBlock);

        var block = signedBlock.Message.Block;
        var proposerAttestation = signedBlock.Message.ProposerAttestation;
        var blockKey = ToKey(blockRoot);
        if (_blocks.ContainsKey(blockKey))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.DuplicateBlock,
                "Duplicate block root.",
                _headSlot,
                _headRoot);
        }

        if (block.Slot.Value > currentSlot + 1)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.FutureSlot,
                $"Block slot {block.Slot.Value} is too far in the future for current slot {currentSlot}.",
                _headSlot,
                _headRoot);
        }

        var parentKey = ToKey(block.ParentRoot);
        if (!_blocks.TryGetValue(parentKey, out var parentBlock))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.UnknownParent,
                $"Unknown parent root {parentKey}.",
                _headSlot,
                _headRoot);
        }

        if (!_states.TryGetValue(parentKey, out var parentState))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.StateTransitionFailed,
                $"Missing parent state for root {parentKey}.",
                _headSlot,
                _headRoot);
        }

        State? chainPostState = null;
        if (_preferChainStateTransition &&
            _chainStates.TryGetValue(parentKey, out var parentChainState))
        {
            if (!_chainStateTransition.TryComputeStateRoot(
                    parentChainState,
                    block,
                    out _,
                    out var computedChainState,
                    out var chainTransitionReason))
            {
                _chainStateFailures[blockKey] = chainTransitionReason;
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.StateTransitionFailed,
                    chainTransitionReason,
                    _headSlot,
                    _headRoot);
            }

            chainPostState = computedChainState;
            _chainStateFailures.Remove(blockKey);
        }
        else if (_preferChainStateTransition &&
                 _chainStateFailures.TryGetValue(parentKey, out var parentFailure))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.StateTransitionFailed,
                $"Missing parent chain state snapshot for root {parentKey}. Parent transition failure: {parentFailure}",
                _headSlot,
                _headRoot);
        }

        if (block.Slot.Value <= parentBlock.Slot.Value)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidSlot,
                $"Block slot {block.Slot.Value} must be greater than parent slot {parentBlock.Slot.Value}.",
                _headSlot,
                _headRoot);
        }

        if (proposerAttestation.ValidatorId != block.ProposerIndex)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.ProposerMismatch,
                $"Proposer attestation validator {proposerAttestation.ValidatorId} does not match block proposer {block.ProposerIndex}.",
                _headSlot,
                _headRoot);
        }

        if (proposerAttestation.Data.Slot.Value != block.Slot.Value)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Proposer attestation slot {proposerAttestation.Data.Slot.Value} must equal block slot {block.Slot.Value}.",
                _headSlot,
                _headRoot);
        }

        if (signedBlock.Signature.AttestationSignatures.Count != block.Body.Attestations.Count)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                "Attestation signatures count must match block body attestations count.",
                _headSlot,
                _headRoot);
        }

        foreach (var signature in signedBlock.Signature.AttestationSignatures)
        {
            if (signature.Participants.Length == 0)
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.InvalidAttestation,
                    "Aggregated signature participants bitlist cannot be empty.",
                    _headSlot,
                    _headRoot);
            }

            if (signature.ProofData.Length == 0)
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.InvalidAttestation,
                    "Aggregated signature proof data cannot be empty.",
                    _headSlot,
                    _headRoot);
            }
        }

        if (!TryValidateAttestationData(
                proposerAttestation.Data,
                currentSlot,
                allowUnknownRoots: false,
                out var proposerReason,
                pendingHeadKey: blockKey,
                pendingHeadSlot: block.Slot.Value,
                pendingHeadParentKey: parentKey,
                requirePendingHeadAncestry: true,
                enforceChainTopology: false))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Invalid proposer attestation: {proposerReason}",
                _headSlot,
                _headRoot);
        }

        foreach (var aggregated in block.Body.Attestations)
        {
            if (!aggregated.AggregationBits.TryToValidatorIndices(out _))
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.InvalidAttestation,
                    "Aggregated attestation participants bitlist cannot be empty.",
                    _headSlot,
                    _headRoot);
            }

            if (!TryValidateAttestationData(
                    aggregated.Data,
                    currentSlot,
                    allowUnknownRoots: false,
                    out var aggregatedReason,
                    pendingHeadKey: blockKey,
                    pendingHeadSlot: block.Slot.Value,
                    pendingHeadParentKey: parentKey,
                    requirePendingHeadAncestry: false,
                    enforceChainTopology: false))
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.InvalidAttestation,
                    $"Invalid aggregated attestation: {aggregatedReason}",
                    _headSlot,
                    _headRoot);
            }

        }

        if (!TryVerifyBlockSignatures(parentKey, signedBlock, out var signatureReason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Invalid block signature: {signatureReason}",
                _headSlot,
                _headRoot);
        }

        ForkChoiceNodeState postState;
        if (chainPostState is not null)
        {
            postState = ToForkChoiceNodeState(chainPostState);
        }
        else if (!_stateTransition.TryTransition(parentState, signedBlock, out postState, out var transitionReason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.StateTransitionFailed,
                transitionReason,
                _headSlot,
                _headRoot);
        }

        _blocks[blockKey] = block;
        _states[blockKey] = postState;
        if (chainPostState is not null)
        {
            _chainStates[blockKey] = chainPostState;
        }

        foreach (var aggregated in block.Body.Attestations)
        {
            if (!aggregated.AggregationBits.TryToValidatorIndices(out var validatorIds))
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.InvalidAttestation,
                    "Aggregated attestation participants bitlist cannot be empty.",
                    _headSlot,
                    _headRoot);
            }

            foreach (var validatorId in validatorIds)
            {
                UpsertAttestation(_latestKnownAttestations, validatorId, aggregated.Data);
                if (_latestPendingAttestations.TryGetValue(validatorId, out var pending) &&
                    pending.Slot.Value <= aggregated.Data.Slot.Value)
                {
                    _latestPendingAttestations.Remove(validatorId);
                }
            }
        }

        UpdateCheckpoints(postState);

        var previousHeadKey = _headKey;
        UpdateHeadFromKnownAttestations();

        UpsertAttestation(_latestPendingAttestations, proposerAttestation.ValidatorId, proposerAttestation.Data);

        var headChanged = previousHeadKey != _headKey;
        return ForkChoiceApplyResult.AcceptedResult(headChanged, _headSlot, _headRoot);
    }

    private bool TryValidateAttestationData(
        AttestationData data,
        ulong currentSlot,
        bool allowUnknownRoots,
        out string reason,
        string? pendingHeadKey = null,
        ulong pendingHeadSlot = 0,
        string? pendingHeadParentKey = null,
        bool requirePendingHeadAncestry = true,
        bool enforceChainTopology = true)
    {
        if (data.Source.Slot.Value > data.Target.Slot.Value)
        {
            reason = "Source checkpoint slot exceeds target checkpoint slot.";
            return false;
        }

        if (data.Slot.Value > currentSlot + 1)
        {
            reason = $"Attestation slot {data.Slot.Value} is too far in the future for current slot {currentSlot}.";
            return false;
        }

        var sourceKey = ToKey(data.Source.Root);
        var sourceKnown = _blocks.TryGetValue(sourceKey, out var sourceBlock);
        if (!sourceKnown && !allowUnknownRoots)
        {
            reason = $"Unknown source root {sourceKey}.";
            return false;
        }

        var targetKey = ToKey(data.Target.Root);
        var targetKnown = _blocks.TryGetValue(targetKey, out var targetBlock);
        if (!targetKnown && !allowUnknownRoots)
        {
            reason = $"Unknown target root {targetKey}.";
            return false;
        }

        var headKey = ToKey(data.Head.Root);
        var headKnown = _blocks.TryGetValue(headKey, out var headBlock);
        var headMatchesPending =
            pendingHeadKey is not null &&
            string.Equals(headKey, pendingHeadKey, StringComparison.Ordinal) &&
            data.Head.Slot.Value == pendingHeadSlot;

        if (!headKnown && headMatchesPending)
        {
            headKnown = true;
        }

        if (!headKnown && !allowUnknownRoots)
        {
            reason = $"Unknown head root {headKey}.";
            return false;
        }

        if (enforceChainTopology &&
            sourceKnown &&
            targetKnown &&
            !IsAncestorOrSelf(sourceKey, targetKey))
        {
            reason = $"Source checkpoint root {sourceKey} is not an ancestor of target checkpoint root {targetKey}.";
            return false;
        }

        if (enforceChainTopology &&
            targetKnown &&
            headKnown &&
            !headMatchesPending &&
            !IsAncestorOrSelf(targetKey, headKey))
        {
            reason = $"Target checkpoint root {targetKey} is not on head chain {headKey}.";
            return false;
        }

        if (enforceChainTopology && requirePendingHeadAncestry)
        {
            if (targetKnown &&
                pendingHeadKey is not null &&
                !IsAncestorOfPendingHead(targetKey, pendingHeadKey, pendingHeadParentKey))
            {
                reason = $"Target checkpoint root {targetKey} is not on parent chain for pending head {pendingHeadKey}.";
                return false;
            }

            if (headKnown &&
                pendingHeadKey is not null &&
                !IsAncestorOfPendingHead(headKey, pendingHeadKey, pendingHeadParentKey))
            {
                reason = $"Head checkpoint root {headKey} is not on parent chain for pending head {pendingHeadKey}.";
                return false;
            }
        }

        if (sourceKnown && sourceBlock!.Slot.Value != data.Source.Slot.Value)
        {
            reason = "Source checkpoint slot does not match source block slot.";
            return false;
        }

        if (targetKnown && targetBlock!.Slot.Value != data.Target.Slot.Value)
        {
            reason = "Target checkpoint slot does not match target block slot.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void UpdateCheckpoints(ForkChoiceNodeState postState)
    {
        if (postState.LatestJustified.Slot.Value > _latestJustified.Slot.Value)
        {
            _latestJustified = postState.LatestJustified;
        }

        if (postState.LatestFinalized.Slot.Value > _latestFinalized.Slot.Value)
        {
            _latestFinalized = postState.LatestFinalized;
        }
    }

    private void UpdateSafeTarget()
    {
        var safeTarget = ComputeSafeTarget();
        _safeTarget = safeTarget.Root;
        _safeTargetSlot = safeTarget.Slot;
    }

    private SafeTargetValue ComputeSafeTarget()
    {
        var headState = CurrentHeadStateSnapshot();
        var minScore = ComputeTwoThirdsThreshold(headState.ValidatorCount);
        var key = ComputeLmdGhostHead(ToKey(_latestJustified.Root), _latestPendingAttestations, minScore);
        if (_blocks.TryGetValue(key, out var block))
        {
            return new SafeTargetValue(ToRoot(key), block.Slot.Value);
        }

        return new SafeTargetValue(_latestJustified.Root, _latestJustified.Slot.Value);
    }

    private Checkpoint SelectAttestationTargetCheckpoint(int safeTargetLookbackSlots)
    {
        var targetKey = WalkBackForSafeTarget(_headKey, safeTargetLookbackSlots);
        while (_blocks.TryGetValue(targetKey, out var targetBlock) &&
               !IsSlotJustifiableAfterFinalized(targetBlock.Slot.Value))
        {
            var parentKey = ToKey(targetBlock.ParentRoot);
            if (parentKey == targetKey || !_blocks.ContainsKey(parentKey))
            {
                break;
            }

            targetKey = parentKey;
        }

        if (_blocks.TryGetValue(targetKey, out var selectedBlock) &&
            IsSlotJustifiableAfterFinalized(selectedBlock.Slot.Value))
        {
            return new Checkpoint(ToRoot(targetKey), selectedBlock.Slot);
        }

        return _latestJustified;
    }

    private string WalkBackForSafeTarget(string startKey, int safeTargetLookbackSlots)
    {
        var targetKey = startKey;
        var lookbackSlots = Math.Max(0, safeTargetLookbackSlots);
        for (var i = 0; i < lookbackSlots; i++)
        {
            if (!_blocks.TryGetValue(targetKey, out var targetBlock))
            {
                break;
            }

            if (targetBlock.Slot.Value <= _safeTargetSlot)
            {
                break;
            }

            var parentKey = ToKey(targetBlock.ParentRoot);
            if (parentKey == targetKey || !_blocks.ContainsKey(parentKey))
            {
                break;
            }

            targetKey = parentKey;
        }

        return targetKey;
    }

    private bool IsSlotJustifiableAfterFinalized(ulong slot)
    {
        if (slot < _latestFinalized.Slot.Value)
        {
            return false;
        }

        return new Slot(slot).IsJustifiableAfter(_latestFinalized.Slot);
    }

    private ForkChoiceNodeState CurrentHeadStateSnapshot()
    {
        if (_states.TryGetValue(_headKey, out var headState))
        {
            return headState;
        }

        return _states[_genesisKey];
    }

    private static int ComputeTwoThirdsThreshold(ulong validatorCount)
    {
        if (validatorCount == 0)
        {
            return 0;
        }

        return checked((int)((validatorCount * 2 + 2) / 3));
    }

    private static void UpsertAttestation(
        Dictionary<ulong, AttestationData> destination,
        ulong validatorId,
        AttestationData data)
    {
        if (!destination.TryGetValue(validatorId, out var current) || current.Slot.Value < data.Slot.Value)
        {
            destination[validatorId] = data;
        }
    }

    private void PromotePendingAttestations()
    {
        foreach (var (validatorId, data) in _latestPendingAttestations)
        {
            UpsertAttestation(_latestKnownAttestations, validatorId, data);
        }

        _latestPendingAttestations.Clear();
    }

    private void UpdateHeadFromKnownAttestations()
    {
        var newHeadKey = ComputeLmdGhostHead(ToKey(_latestJustified.Root), _latestKnownAttestations);
        if (_blocks.TryGetValue(newHeadKey, out var newHeadBlock))
        {
            _headKey = newHeadKey;
            _headSlot = newHeadBlock.Slot.Value;
            _headRoot = ToRoot(newHeadKey);
        }
    }

    private bool TryVerifyGossipAttestationSignature(SignedAttestation signedAttestation, out string reason)
    {
        if (_leanSig is null)
        {
            reason = string.Empty;
            return true;
        }

        var targetKey = ToKey(signedAttestation.Message.Target.Root);
        if (!_chainStates.TryGetValue(targetKey, out var targetChainState))
        {
            reason = $"Missing chain state snapshot for attestation target root {targetKey}.";
            return false;
        }

        if (signedAttestation.ValidatorId >= (ulong)targetChainState.Validators.Count)
        {
            reason = $"Attestation validator {signedAttestation.ValidatorId} is out of range for target state validator count {targetChainState.Validators.Count}.";
            return false;
        }

        if (!TryToSignatureEpoch(signedAttestation.Message.Slot.Value, out var epoch))
        {
            reason = $"Attestation slot {signedAttestation.Message.Slot.Value} cannot be represented as XMSS epoch.";
            return false;
        }

        var publicKey = targetChainState.Validators[checked((int)signedAttestation.ValidatorId)].Pubkey.AsSpan();
        if (publicKey.Length == 0)
        {
            reason = $"Missing public key for attestation validator {signedAttestation.ValidatorId}.";
            return false;
        }

        var messageRoot = signedAttestation.Message.HashTreeRoot();
        try
        {
            if (!_leanSig.Verify(publicKey, epoch, messageRoot, signedAttestation.Signature.Bytes))
            {
                reason = "Attestation signature verification failed.";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"Attestation signature verification threw: {ex.Message}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryVerifyBlockSignatures(string parentKey, SignedBlockWithAttestation signedBlock, out string reason)
    {
        if (_leanSig is null || _leanMultiSig is null)
        {
            reason = string.Empty;
            return true;
        }

        if (!_chainStates.TryGetValue(parentKey, out var parentChainState))
        {
            reason = $"Missing parent chain state snapshot for signature verification at root {parentKey}.";
            return false;
        }

        var validators = parentChainState.Validators;
        var block = signedBlock.Message.Block;
        if (block.ProposerIndex >= (ulong)validators.Count)
        {
            reason = $"Block proposer {block.ProposerIndex} is out of range for parent validator count {validators.Count}.";
            return false;
        }

        if (!TryToSignatureEpoch(signedBlock.Message.ProposerAttestation.Data.Slot.Value, out var proposerEpoch))
        {
            reason = $"Proposer attestation slot {signedBlock.Message.ProposerAttestation.Data.Slot.Value} cannot be represented as XMSS epoch.";
            return false;
        }

        var proposerPublicKey = validators[checked((int)block.ProposerIndex)].Pubkey.AsSpan();
        if (proposerPublicKey.Length == 0)
        {
            reason = $"Missing public key for block proposer {block.ProposerIndex}.";
            return false;
        }

        var proposerMessageRoot = signedBlock.Message.ProposerAttestation.Data.HashTreeRoot();
        try
        {
            if (!_leanSig.Verify(
                    proposerPublicKey,
                    proposerEpoch,
                    proposerMessageRoot,
                    signedBlock.Signature.ProposerSignature.Bytes))
            {
                reason = "Proposer signature verification failed.";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"Proposer signature verification threw: {ex.Message}";
            return false;
        }

        var aggregatedAttestations = block.Body.Attestations;
        for (var i = 0; i < aggregatedAttestations.Count; i++)
        {
            var aggregated = aggregatedAttestations[i];
            var proof = signedBlock.Signature.AttestationSignatures[i];

            if (!aggregated.AggregationBits.TryToValidatorIndices(out var participantIds))
            {
                reason = $"Aggregated attestation {i} has no participants.";
                return false;
            }

            var publicKeys = new List<ReadOnlyMemory<byte>>(participantIds.Count);
            foreach (var participantId in participantIds)
            {
                if (participantId >= (ulong)validators.Count)
                {
                    reason = $"Aggregated attestation participant {participantId} is out of range for parent validator count {validators.Count}.";
                    return false;
                }

                var publicKey = validators[checked((int)participantId)].Pubkey.AsSpan();
                if (publicKey.Length == 0)
                {
                    reason = $"Missing public key for aggregated attestation participant {participantId}.";
                    return false;
                }

                publicKeys.Add(publicKey.ToArray());
            }

            if (!TryToSignatureEpoch(aggregated.Data.Slot.Value, out var epoch))
            {
                reason = $"Aggregated attestation slot {aggregated.Data.Slot.Value} cannot be represented as XMSS epoch.";
                return false;
            }

            var messageRoot = aggregated.Data.HashTreeRoot();
            try
            {
                if (!_leanMultiSig.VerifyAggregate(publicKeys, messageRoot, proof.ProofData, epoch))
                {
                    reason = $"Aggregated signature verification failed for attestation index {i}.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = $"Aggregated signature verification threw for attestation index {i}: {ex.Message}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryToSignatureEpoch(ulong slot, out uint epoch)
    {
        if (slot > uint.MaxValue)
        {
            epoch = 0;
            return false;
        }

        epoch = (uint)slot;
        return true;
    }

    private static int NormalizeInterval(int intervalInSlot)
    {
        var normalized = intervalInSlot % IntervalsPerSlot;
        return normalized < 0 ? normalized + IntervalsPerSlot : normalized;
    }

    // TODO(proto-array): this keeps leanSpec-compatible full recomputation on each head update.
    // Replace with an incremental ProtoArray implementation once behavior parity tests are locked.
    private string ComputeLmdGhostHead(
        string startKey,
        IReadOnlyDictionary<ulong, AttestationData> attestations,
        int minScore = 0)
    {
        if (!_blocks.ContainsKey(startKey))
        {
            startKey = _blocks.ContainsKey(_startKey) ? _startKey : _genesisKey;
        }

        var startSlot = _blocks[startKey].Slot.Value;
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var attestation in attestations.Values)
        {
            var currentKey = ToKey(attestation.Head.Root);
            while (_blocks.TryGetValue(currentKey, out var currentBlock) && currentBlock.Slot.Value > startSlot)
            {
                weights.TryGetValue(currentKey, out var currentWeight);
                weights[currentKey] = currentWeight + 1;
                currentKey = ToKey(currentBlock.ParentRoot);
            }
        }

        var children = BuildChildrenMap();
        var head = startKey;
        while (children.TryGetValue(head, out var childKeys) && childKeys.Count > 0)
        {
            var candidates = childKeys
                .Where(key => (weights.TryGetValue(key, out var weight) ? weight : 0) >= minScore)
                .ToList();

            if (candidates.Count == 0)
            {
                break;
            }

            // Align with leanSpec/ethlambda LMD-GHOST tie-break:
            // 1) vote weight, 2) root hash.
            head = candidates
                .OrderByDescending(key => weights.TryGetValue(key, out var weight) ? weight : 0)
                .ThenByDescending(key => key, StringComparer.Ordinal)
                .First();
        }

        return head;
    }

    private Dictionary<string, List<string>> BuildChildrenMap()
    {
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, block) in _blocks)
        {
            var parentKey = ToKey(block.ParentRoot);
            if (!children.TryGetValue(parentKey, out var childList))
            {
                childList = new List<string>();
                children[parentKey] = childList;
            }

            if (key != parentKey)
            {
                childList.Add(key);
            }
        }

        return children;
    }

    private ulong GetSlotForRoot(Bytes32 root)
    {
        var key = ToKey(root);
        return _blocks.TryGetValue(key, out var block) ? block.Slot.Value : 0;
    }

    private bool IsAncestorOrSelf(string ancestorKey, string descendantKey)
    {
        if (string.Equals(ancestorKey, descendantKey, StringComparison.Ordinal))
        {
            return true;
        }

        var currentKey = descendantKey;
        var traversed = 0;
        var maxTraversal = _blocks.Count + 1;
        while (traversed < maxTraversal && _blocks.TryGetValue(currentKey, out var block))
        {
            var parentKey = ToKey(block.ParentRoot);
            if (string.Equals(parentKey, ancestorKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(parentKey, currentKey, StringComparison.Ordinal))
            {
                break;
            }

            currentKey = parentKey;
            traversed++;
        }

        return false;
    }

    private bool IsAncestorOfPendingHead(
        string ancestorKey,
        string pendingHeadKey,
        string? pendingHeadParentKey)
    {
        if (string.Equals(ancestorKey, pendingHeadKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(pendingHeadParentKey))
        {
            return false;
        }

        return IsAncestorOrSelf(ancestorKey, pendingHeadParentKey);
    }

    private static ForkChoiceNodeState ToForkChoiceNodeState(State state)
    {
        return new ForkChoiceNodeState(
            state.LatestJustified,
            state.LatestFinalized,
            (ulong)state.Validators.Count);
    }

    private static string ToKey(Bytes32 root)
    {
        return Convert.ToHexString(root.AsSpan());
    }

    private static Bytes32 ComputeCanonicalGenesisRoot(State genesisState)
    {
        var latestBlockHeader = genesisState.LatestBlockHeader;
        if (latestBlockHeader.StateRoot.Equals(Bytes32.Zero()))
        {
            latestBlockHeader = latestBlockHeader with { StateRoot = new Bytes32(genesisState.HashTreeRoot()) };
        }

        return new Bytes32(latestBlockHeader.HashTreeRoot());
    }

    private static Bytes32 ToRoot(string key)
    {
        return new Bytes32(Convert.FromHexString(key));
    }

    private static Block CreateAnchorBlock(ulong slot, Bytes32 root)
    {
        var emptyAttestations = new List<AggregatedAttestation>();
        return new Block(new Slot(slot), 0, root, Bytes32.Zero(), new BlockBody(emptyAttestations));
    }

    private static Checkpoint ToCheckpointOrFallback(ulong slot, byte[] root, Checkpoint fallback)
    {
        if (!TryParseRoot(root, out var parsedRoot))
        {
            return fallback;
        }

        return new Checkpoint(parsedRoot, new Slot(slot));
    }

    private static Bytes32 ToRootOrFallback(byte[] root, Bytes32 fallback)
    {
        return TryParseRoot(root, out var parsedRoot) ? parsedRoot : fallback;
    }

    private static bool TryParseRoot(byte[] root, out Bytes32 parsedRoot)
    {
        if (root.Length == SszEncoding.Bytes32Length)
        {
            parsedRoot = new Bytes32(root);
            return true;
        }

        parsedRoot = default;
        return false;
    }

    private readonly record struct SafeTargetValue(Bytes32 Root, ulong Slot);
}
