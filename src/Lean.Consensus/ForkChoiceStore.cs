using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class ForkChoiceStore
{
    private readonly Dictionary<string, Block> _blocks = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, AttestationData> _latestKnownAttestations = new();
    private readonly Dictionary<ulong, AttestationData> _latestPendingAttestations = new();
    private readonly string _genesisKey;
    private string _startKey;
    private string _headKey;
    private ulong _headSlot;
    private Bytes32 _headRoot = Bytes32.Zero();

    public ForkChoiceStore()
    {
        _headKey = ToKey(_headRoot);
        _genesisKey = _headKey;
        _startKey = _genesisKey;
        _blocks[_headKey] = CreateAnchorBlock(0, _headRoot);
    }

    public ulong HeadSlot => _headSlot;

    public Bytes32 HeadRoot => _headRoot;

    public void InitializeHead(ConsensusHeadState state)
    {
        if (state.HeadRoot.Length != SszEncoding.Bytes32Length)
        {
            return;
        }

        _blocks.Clear();
        _latestKnownAttestations.Clear();
        _latestPendingAttestations.Clear();

        var anchorRoot = new Bytes32(state.HeadRoot);
        _headRoot = anchorRoot;
        _headSlot = state.HeadSlot;
        _headKey = ToKey(anchorRoot);
        _startKey = _headKey;
        _blocks[_genesisKey] = CreateAnchorBlock(0, Bytes32.Zero());
        _blocks[_headKey] = CreateAnchorBlock(state.HeadSlot, anchorRoot);
    }

    public ForkChoiceApplyResult ApplyBlock(
        SignedBlockWithAttestation signedBlock,
        Bytes32 blockRoot,
        ulong currentSlot)
    {
        ArgumentNullException.ThrowIfNull(signedBlock);

        PromotePendingAttestations();

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

        _blocks[blockKey] = block;

        foreach (var aggregated in block.Body.Attestations)
        {
            foreach (var validatorId in aggregated.AggregationBits.ToValidatorIndices())
            {
                UpsertAttestation(_latestKnownAttestations, validatorId, aggregated.Data);
            }
        }

        var previousHeadKey = _headKey;
        var newHeadKey = ComputeLmdGhostHead(_startKey, _latestKnownAttestations);
        if (_blocks.TryGetValue(newHeadKey, out var newHeadBlock))
        {
            _headKey = newHeadKey;
            _headSlot = newHeadBlock.Slot.Value;
            _headRoot = new Bytes32(Convert.FromHexString(newHeadKey));
        }

        UpsertAttestation(_latestPendingAttestations, proposerAttestation.ValidatorId, proposerAttestation.Data);
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

    private string ComputeLmdGhostHead(string startKey, IReadOnlyDictionary<ulong, AttestationData> attestations)
    {
        if (!_blocks.ContainsKey(startKey))
        {
            startKey = _genesisKey;
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
            head = childKeys
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

    private static string ToKey(Bytes32 root)
    {
        return Convert.ToHexString(root.AsSpan());
    }

    private static Block CreateAnchorBlock(ulong slot, Bytes32 root)
    {
        var emptyAttestations = new List<AggregatedAttestation>();
        return new Block(new Slot(slot), 0, root, Bytes32.Zero(), new BlockBody(emptyAttestations));
    }
}
