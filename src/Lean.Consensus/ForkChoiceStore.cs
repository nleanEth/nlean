using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class ForkChoiceStore
{
    private readonly Dictionary<string, Block> _blocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ForkChoiceNodeState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, AttestationData> _latestKnownAttestations = new();
    private readonly Dictionary<ulong, AttestationData> _latestPendingAttestations = new();
    private readonly IForkChoiceStateTransition _stateTransition;
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
        : this(new Devnet2ForkChoiceStateTransition(new ConsensusConfig()))
    {
    }

    public ForkChoiceStore(IForkChoiceStateTransition stateTransition)
    {
        ArgumentNullException.ThrowIfNull(stateTransition);
        _stateTransition = stateTransition;
        _headKey = ToKey(_headRoot);
        _genesisKey = _headKey;
        _startKey = _genesisKey;
        _blocks[_headKey] = CreateAnchorBlock(0, _headRoot);

        var genesisCheckpoint = new Checkpoint(_headRoot, new Slot(0));
        _states[_headKey] = new ForkChoiceNodeState(genesisCheckpoint, genesisCheckpoint, 1);
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

    public void InitializeHead(ConsensusHeadState state)
    {
        if (state.HeadRoot.Length != SszEncoding.Bytes32Length)
        {
            return;
        }

        _blocks.Clear();
        _states.Clear();
        _latestKnownAttestations.Clear();
        _latestPendingAttestations.Clear();

        _blocks[_genesisKey] = CreateAnchorBlock(0, Bytes32.Zero());
        var genesisCheckpoint = new Checkpoint(Bytes32.Zero(), new Slot(0));
        _states[_genesisKey] = new ForkChoiceNodeState(genesisCheckpoint, genesisCheckpoint, 1);

        var anchorRoot = new Bytes32(state.HeadRoot);
        _headRoot = anchorRoot;
        _headSlot = state.HeadSlot;
        _headKey = ToKey(anchorRoot);
        _startKey = _headKey;
        _blocks[_headKey] = CreateAnchorBlock(state.HeadSlot, anchorRoot);

        var anchorCheckpoint = new Checkpoint(anchorRoot, new Slot(state.HeadSlot));
        _states[_headKey] = new ForkChoiceNodeState(anchorCheckpoint, anchorCheckpoint, 1);
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

    public ForkChoiceApplyResult ApplyGossipAttestation(SignedAttestation signedAttestation, ulong currentSlot)
    {
        ArgumentNullException.ThrowIfNull(signedAttestation);

        if (!TryValidateAttestationData(signedAttestation.Message, currentSlot, out var reason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Invalid gossip attestation: {reason}",
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

        if (signedAttestation.ValidatorId >= targetState.ValidatorCount)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Validator {signedAttestation.ValidatorId} is out of range for target validator count {targetState.ValidatorCount}.",
                _headSlot,
                _headRoot);
        }

        UpsertAttestation(_latestPendingAttestations, signedAttestation.ValidatorId, signedAttestation.Message);
        UpdateSafeTarget();
        return ForkChoiceApplyResult.AcceptedResult(false, _headSlot, _headRoot);
    }

    public ForkChoiceApplyResult OnSlotTick(ulong currentSlot)
    {
        PromotePendingAttestations();

        var previousHeadKey = _headKey;
        var newHeadKey = ComputeLmdGhostHead(ToKey(_latestJustified.Root), _latestKnownAttestations);
        if (_blocks.TryGetValue(newHeadKey, out var newHeadBlock))
        {
            _headKey = newHeadKey;
            _headSlot = newHeadBlock.Slot.Value;
            _headRoot = ToRoot(newHeadKey);
        }

        UpdateSafeTarget();
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

        if (!TryValidateAttestationData(proposerAttestation.Data, currentSlot, out var proposerReason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Invalid proposer attestation: {proposerReason}",
                _headSlot,
                _headRoot);
        }

        foreach (var aggregated in block.Body.Attestations)
        {
            if (!TryValidateAttestationData(aggregated.Data, currentSlot, out var aggregatedReason))
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.InvalidAttestation,
                    $"Invalid aggregated attestation: {aggregatedReason}",
                    _headSlot,
                    _headRoot);
            }
        }

        if (!_stateTransition.TryTransition(parentState, signedBlock, out var postState, out var transitionReason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.StateTransitionFailed,
                transitionReason,
                _headSlot,
                _headRoot);
        }

        _blocks[blockKey] = block;
        _states[blockKey] = postState;

        foreach (var aggregated in block.Body.Attestations)
        {
            foreach (var validatorId in aggregated.AggregationBits.ToValidatorIndices())
            {
                UpsertAttestation(_latestKnownAttestations, validatorId, aggregated.Data);
            }
        }

        UpdateCheckpoints(postState);

        var previousHeadKey = _headKey;
        _ = OnSlotTick(currentSlot);

        UpsertAttestation(_latestPendingAttestations, proposerAttestation.ValidatorId, proposerAttestation.Data);
        UpdateSafeTarget();

        var headChanged = previousHeadKey != _headKey;
        return ForkChoiceApplyResult.AcceptedResult(headChanged, _headSlot, _headRoot);
    }

    private bool TryValidateAttestationData(AttestationData data, ulong currentSlot, out string reason)
    {
        if (data.Source.Slot.Value > data.Target.Slot.Value)
        {
            reason = "Source checkpoint slot exceeds target checkpoint slot.";
            return false;
        }

        if (data.Target.Slot.Value > data.Head.Slot.Value)
        {
            reason = "Target checkpoint slot exceeds head checkpoint slot.";
            return false;
        }

        if (data.Slot.Value < data.Target.Slot.Value)
        {
            reason = "Attestation slot is before target slot.";
            return false;
        }

        if (data.Slot.Value > currentSlot + 1)
        {
            reason = $"Attestation slot {data.Slot.Value} is too far in the future for current slot {currentSlot}.";
            return false;
        }

        if (!_blocks.TryGetValue(ToKey(data.Source.Root), out var sourceBlock))
        {
            reason = $"Unknown source root {ToKey(data.Source.Root)}.";
            return false;
        }

        if (!_blocks.TryGetValue(ToKey(data.Target.Root), out var targetBlock))
        {
            reason = $"Unknown target root {ToKey(data.Target.Root)}.";
            return false;
        }

        if (!_blocks.TryGetValue(ToKey(data.Head.Root), out var headBlock))
        {
            reason = $"Unknown head root {ToKey(data.Head.Root)}.";
            return false;
        }

        if (sourceBlock.Slot.Value != data.Source.Slot.Value)
        {
            reason = "Source checkpoint slot does not match source block slot.";
            return false;
        }

        if (targetBlock.Slot.Value != data.Target.Slot.Value)
        {
            reason = "Target checkpoint slot does not match target block slot.";
            return false;
        }

        if (headBlock.Slot.Value != data.Head.Slot.Value)
        {
            reason = "Head checkpoint slot does not match head block slot.";
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

    private static string ToKey(Bytes32 root)
    {
        return Convert.ToHexString(root.AsSpan());
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
