using Lean.Consensus.Sync;
using Lean.Consensus.Types;

namespace Lean.Consensus.ForkChoice;

public sealed class ProtoArrayForkChoiceStore : IAttestationSink
{
    public const int IntervalsPerSlot = 5;

    private readonly ProtoArray _protoArray;
    private readonly IForkChoiceStateTransition _stateTransition;
    private readonly Dictionary<string, ForkChoiceNodeState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, AttestationData> _pendingAttestations = new();
    private readonly Dictionary<ulong, AttestationData> _knownAttestations = new();
    private readonly Dictionary<(ulong, string), XmssSignature> _gossipSignatures = new();
    private readonly Dictionary<string, AttestationData> _attestationDataByRoot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AggregatedSignatureProof>> _newAggregatedPayloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AggregatedSignatureProof>> _knownAggregatedPayloads = new(StringComparer.Ordinal);

    private Bytes32 _headRoot;
    private ulong _headSlot;
    private Checkpoint _latestJustified;
    private Checkpoint _latestFinalized;

    public ProtoArrayForkChoiceStore(IForkChoiceStateTransition stateTransition, ConsensusConfig config)
    {
        ArgumentNullException.ThrowIfNull(stateTransition);
        ArgumentNullException.ThrowIfNull(config);

        _stateTransition = stateTransition;
        var initialValidatorCount = Math.Max(1UL, config.InitialValidatorCount);

        var chainTransition = new ChainStateTransition(config);
        var genesisState = chainTransition.CreateGenesisState(initialValidatorCount);
        var genesisRoot = new Bytes32(genesisState.LatestBlockHeader.HashTreeRoot());

        var genesisCheckpoint = new Checkpoint(genesisRoot, new Slot(0));
        _latestJustified = genesisCheckpoint;
        _latestFinalized = genesisCheckpoint;
        _headRoot = genesisRoot;
        _headSlot = 0;

        _protoArray = new ProtoArray(genesisRoot, 0, 0);

        var genesisNodeState = new ForkChoiceNodeState(genesisCheckpoint, genesisCheckpoint, initialValidatorCount);
        _states[ProtoArray.RootKey(genesisRoot)] = genesisNodeState;
    }

    public Bytes32 HeadRoot => _headRoot;
    public ulong HeadSlot => _headSlot;
    public ulong JustifiedSlot => _latestJustified.Slot.Value;
    public Bytes32 JustifiedRoot => _latestJustified.Root;
    public ulong FinalizedSlot => _latestFinalized.Slot.Value;
    public Bytes32 FinalizedRoot => _latestFinalized.Root;
    public bool ContainsBlock(Bytes32 root) => _protoArray.ContainsBlock(root);
    public int PendingAggregatedPayloadCount => _newAggregatedPayloads.Values.Sum(v => v.Count);

    public void OnGossipSignature(ulong validatorId, Bytes32 dataRoot, XmssSignature signature)
    {
        var key = (validatorId, ProtoArray.RootKey(dataRoot));
        _gossipSignatures[key] = signature;
    }

    public bool HasGossipSignature(ulong validatorId, Bytes32 dataRoot)
    {
        return _gossipSignatures.ContainsKey((validatorId, ProtoArray.RootKey(dataRoot)));
    }

    public void OnGossipAggregatedAttestation(SignedAggregatedAttestation signed)
    {
        var dataRootBytes = signed.Data.HashTreeRoot();
        var dataRootKey = Convert.ToHexString(dataRootBytes);

        _attestationDataByRoot[dataRootKey] = signed.Data;

        if (!_newAggregatedPayloads.TryGetValue(dataRootKey, out var list))
        {
            list = new List<AggregatedSignatureProof>();
            _newAggregatedPayloads[dataRootKey] = list;
        }
        list.Add(signed.Proof);
    }

    public List<AggregatedAttestation> ExtractAttestationsForBlock()
    {
        var result = new List<AggregatedAttestation>();
        foreach (var (dataRootKey, payloads) in _knownAggregatedPayloads)
        {
            if (!_attestationDataByRoot.TryGetValue(dataRootKey, out var data))
                continue;

            foreach (var proof in payloads)
            {
                result.Add(new AggregatedAttestation(proof.Participants, data));
            }
        }
        return result;
    }

    public ForkChoiceApplyResult OnBlock(SignedBlockWithAttestation signedBlock)
    {
        var block = signedBlock.Message.Block;
        var blockRoot = new Bytes32(signedBlock.Message.Block.HashTreeRoot());
        var blockKey = ProtoArray.RootKey(blockRoot);

        // Reject duplicates
        if (_protoArray.ContainsBlock(blockRoot))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.DuplicateBlock,
                "Block already known.",
                _headSlot, _headRoot);
        }

        // Reject unknown parent
        var parentKey = ProtoArray.RootKey(block.ParentRoot);
        if (!_states.ContainsKey(parentKey))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.UnknownParent,
                "Parent block not found in store.",
                _headSlot, _headRoot);
        }

        // Run state transition
        var parentState = _states[parentKey];
        if (!_stateTransition.TryTransition(parentState, signedBlock, out var postState, out var reason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.StateTransitionFailed,
                reason,
                _headSlot, _headRoot);
        }

        // Register in proto-array
        _protoArray.RegisterBlock(
            blockRoot, block.ParentRoot, block.Slot.Value,
            postState.LatestJustified.Slot.Value,
            postState.LatestFinalized.Slot.Value);

        _states[blockKey] = postState;

        // Update store checkpoints
        if (postState.LatestJustified.Slot.Value > _latestJustified.Slot.Value)
            _latestJustified = postState.LatestJustified;
        if (postState.LatestFinalized.Slot.Value > _latestFinalized.Slot.Value)
        {
            _latestFinalized = postState.LatestFinalized;
            _protoArray.Prune(_latestFinalized.Root);

            // Clean up states for pruned nodes to prevent memory leak
            var staleKeys = new List<string>();
            foreach (var key in _states.Keys)
            {
                if (!_protoArray.ContainsKey(key))
                    staleKeys.Add(key);
            }

            foreach (var key in staleKeys)
                _states.Remove(key);

            // Prune stale attestation data
            _gossipSignatures.Clear();
            var staleDataKeys = _attestationDataByRoot
                .Where(kv => kv.Value.Slot.Value <= _latestFinalized.Slot.Value)
                .Select(kv => kv.Key).ToList();
            foreach (var dataKey in staleDataKeys)
            {
                _attestationDataByRoot.Remove(dataKey);
                _knownAggregatedPayloads.Remove(dataKey);
                _newAggregatedPayloads.Remove(dataKey);
            }
        }

        return ForkChoiceApplyResult.AcceptedResult(false, _headSlot, _headRoot);
    }

    public void OnAttestation(SignedAttestation attestation)
    {
        _pendingAttestations[attestation.ValidatorId] = attestation.Message;
    }

    void IAttestationSink.AddAttestation(SignedAttestation attestation) => OnAttestation(attestation);

    /// <summary>
    /// Called at each interval within a slot.
    /// At the last interval (IntervalsPerSlot - 1), promotes pending attestations
    /// and recomputes the head.
    /// </summary>
    public void TickInterval(ulong slot, int intervalInSlot)
    {
        if (intervalInSlot != IntervalsPerSlot - 1)
            return;

        // Promote pending attestations to known
        var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (validatorId, data) in _pendingAttestations)
        {
            var headKey = ProtoArray.RootKey(data.Head.Root);
            if (!_protoArray.ContainsBlock(data.Head.Root))
                continue;

            // Remove old vote if exists
            if (_knownAttestations.TryGetValue(validatorId, out var oldData))
            {
                var oldKey = ProtoArray.RootKey(oldData.Head.Root);
                if (_protoArray.ContainsBlock(oldData.Head.Root))
                    deltas[oldKey] = deltas.GetValueOrDefault(oldKey) - 1;
            }

            // Add new vote
            deltas[headKey] = deltas.GetValueOrDefault(headKey) + 1;
            _knownAttestations[validatorId] = data;
        }

        _pendingAttestations.Clear();

        // Promote aggregated payloads: new → known
        foreach (var (key, payloads) in _newAggregatedPayloads)
        {
            if (!_knownAggregatedPayloads.TryGetValue(key, out var knownList))
            {
                knownList = new List<AggregatedSignatureProof>();
                _knownAggregatedPayloads[key] = knownList;
            }
            knownList.AddRange(payloads);
        }
        _newAggregatedPayloads.Clear();

        // Apply deltas and find head
        _protoArray.ApplyScoreChanges(deltas, _latestJustified.Slot.Value, _latestFinalized.Slot.Value);
        var newHead = _protoArray.FindHead(_latestJustified.Root, _latestJustified.Slot.Value, _latestFinalized.Slot.Value);

        var headChanged = !newHead.Equals(_headRoot);
        _headRoot = newHead;
        _headSlot = _protoArray.GetSlot(newHead) ?? _headSlot;
    }
}
