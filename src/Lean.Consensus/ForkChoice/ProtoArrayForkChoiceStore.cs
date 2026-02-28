using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lean.Consensus.ForkChoice;

public sealed class ProtoArrayForkChoiceStore : IAttestationSink
{
    public const int IntervalsPerSlot = 5;
    public const int JustificationLookbackSlots = 3;
    public const int MaxAttestationAgeSlots = 16;

    public object SyncRoot { get; } = new object();

    private readonly ProtoArray _protoArray;
    private readonly Dictionary<ulong, AttestationData> _pendingAttestations = new();
    private readonly Dictionary<ulong, AttestationData> _knownAttestations = new();
    private readonly Dictionary<(ulong, string), XmssSignature> _gossipSignatures = new();
    private readonly Dictionary<string, AttestationData> _attestationDataByRoot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AggregatedSignatureProof>> _newAggregatedPayloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AggregatedSignatureProof>> _knownAggregatedPayloads = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    private Bytes32 _headRoot;
    private ulong _headSlot;
    private ulong _currentSlot;
    private Checkpoint _latestJustified;
    private Checkpoint _latestFinalized;
    private Bytes32 _safeTarget;
    private ulong _validatorCount;

    public ProtoArrayForkChoiceStore(ConsensusConfig config, ILogger<ProtoArrayForkChoiceStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        _validatorCount = Math.Max(1UL, config.InitialValidatorCount);

        var chainTransition = new ChainStateTransition(config);
        var genesisState = chainTransition.CreateGenesisState(_validatorCount);
        var genesisRoot = new Bytes32(genesisState.LatestBlockHeader.HashTreeRoot());

        var genesisCheckpoint = new Checkpoint(genesisRoot, new Slot(0));
        _latestJustified = genesisCheckpoint;
        _latestFinalized = genesisCheckpoint;
        _headRoot = genesisRoot;
        _headSlot = 0;
        _currentSlot = 0;

        _protoArray = new ProtoArray(genesisRoot, 0, 0);
        _safeTarget = genesisRoot;
        _logger = logger ?? (ILogger)NullLogger<ProtoArrayForkChoiceStore>.Instance;
    }

    public Bytes32 HeadRoot => _headRoot;
    public ulong HeadSlot => _headSlot;
    public ulong JustifiedSlot => _latestJustified.Slot.Value;
    public Bytes32 JustifiedRoot => _latestJustified.Root;
    public ulong FinalizedSlot => _latestFinalized.Slot.Value;
    public Bytes32 FinalizedRoot => _latestFinalized.Root;
    public Bytes32 SafeTarget => _safeTarget;
    public ProtoArray ProtoArray => _protoArray;
    public bool ContainsBlock(Bytes32 root) => _protoArray.ContainsBlock(root);
    public int PendingAggregatedPayloadCount
    {
        get
        {
            int count = 0;
            foreach (var list in _newAggregatedPayloads.Values)
                count += list.Count;
            return count;
        }
    }

    public IReadOnlySet<Bytes32> GetAllBlockRoots()
    {
        var roots = new HashSet<Bytes32>();
        foreach (var (root, _, _) in _protoArray.GetAllBlocks())
            roots.Add(root);
        return roots;
    }

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
        _ = TryOnGossipAggregatedAttestation(signed, out _);
    }

    public bool TryOnGossipAggregatedAttestation(SignedAggregatedAttestation signed, out string reason)
    {
        if (!TryValidateAttestationData(signed.Data, out reason))
        {
            return false;
        }

        if (!signed.Proof.Participants.TryToValidatorIndices(out var participantIds) || participantIds.Count == 0)
        {
            reason = "Aggregated attestation must include at least one participant.";
            return false;
        }

        var dataRootKey = ToDataRootKey(signed.Data);

        _attestationDataByRoot[dataRootKey] = signed.Data;

        if (!_newAggregatedPayloads.TryGetValue(dataRootKey, out var list))
        {
            list = new List<AggregatedSignatureProof>();
            _newAggregatedPayloads[dataRootKey] = list;
        }
        list.Add(signed.Proof);

        foreach (var vid in participantIds)
        {
            _pendingAttestations[vid] = signed.Data;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Extract per-validator attestation data from ALL known aggregated payloads,
    /// filtering only by slot (attestation.slot &lt; blockSlot).
    /// No source checkpoint filtering — used by the fixed-point block building loop
    /// which needs to re-check available attestations against changing justified checkpoints.
    /// </summary>
    public List<Attestation> ExtractAllAttestationsFromKnownPayloads(ulong blockSlot)
    {
        var perValidator = new Dictionary<ulong, AttestationData>();

        foreach (var (dataRootKey, payloads) in _knownAggregatedPayloads)
        {
            if (!_attestationDataByRoot.TryGetValue(dataRootKey, out var data))
                continue;

            if (data.Slot.Value >= blockSlot)
                continue;

            foreach (var proof in payloads)
            {
                if (!proof.Participants.TryToValidatorIndices(out var pids))
                    continue;

                foreach (var vid in pids)
                {
                    if (!perValidator.TryGetValue(vid, out var existing) || existing.Slot.Value < data.Slot.Value)
                    {
                        perValidator[vid] = data;
                    }
                }
            }
        }

        var list = new List<Attestation>(perValidator.Count);
        foreach (var kv in perValidator)
            list.Add(new Attestation(kv.Key, kv.Value));
        return list;
    }

    /// <summary>
    /// Returns the raw known aggregated payload pool keyed by data root hex.
    /// Used by the proof selection algorithm (greedy set cover) to look up
    /// candidate proofs for each attestation data group.
    /// </summary>
    public IReadOnlyDictionary<string, List<AggregatedSignatureProof>> GetKnownPayloadPool()
    {
        return _knownAggregatedPayloads;
    }

    public bool TryGetAttestationData(string dataRootKey, out AttestationData data)
    {
        return _attestationDataByRoot.TryGetValue(dataRootKey, out data!);
    }

    /// <summary>
    /// Registers a block in the fork choice store using checkpoints from the
    /// canonical state transition. Matches zeam/ethlambda architecture where
    /// the fork choice store does not run its own state transition.
    /// </summary>
    public ForkChoiceApplyResult OnBlock(
        SignedBlockWithAttestation signedBlock,
        Checkpoint canonicalJustified,
        Checkpoint canonicalFinalized,
        ulong validatorCount)
    {
        var block = signedBlock.Message.Block;
        var blockRoot = new Bytes32(block.HashTreeRoot());
        var proposerAttestation = signedBlock.Message.ProposerAttestation;
        var aggregatedAttestations = block.Body.Attestations;
        var attestationSignatures = signedBlock.Signature.AttestationSignatures;

        // Reject duplicates
        if (_protoArray.ContainsBlock(blockRoot))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.DuplicateBlock,
                "Block already known.",
                _headSlot, _headRoot);
        }

        // Reject unknown parent
        if (!_protoArray.ContainsBlock(block.ParentRoot))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.UnknownParent,
                "Parent block not found in store.",
                _headSlot, _headRoot);
        }

        if (aggregatedAttestations.Count != attestationSignatures.Count)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                "Attestation signatures count does not match block attestations count.",
                _headSlot,
                _headRoot);
        }

        // Register in proto-array using canonical state transition checkpoints.
        // Do not clamp to store checkpoints; node viability must reflect canonical state.
        _protoArray.RegisterBlock(
            blockRoot, block.ParentRoot, block.Slot.Value,
            canonicalJustified.Slot.Value,
            canonicalFinalized.Slot.Value);

        _validatorCount = Math.Max(_validatorCount, validatorCount);

        UpdateStoreCheckpoints(canonicalJustified, canonicalFinalized);

        // Block-body attestations become immediately known.
        // Also extract per-validator fork choice votes: leanSpec on_block() calls
        // process_attestation() which updates both state AND latest messages.
        // Without this, cross-client attestation votes embedded in blocks are
        // invisible to fork choice, causing persistent fork deadlocks.
        for (var i = 0; i < aggregatedAttestations.Count; i++)
        {
            var attestation = aggregatedAttestations[i];
            var proof = attestationSignatures[i];
            var dataRootKey = ToDataRootKey(attestation.Data);
            _attestationDataByRoot[dataRootKey] = attestation.Data;

            if (!_knownAggregatedPayloads.TryGetValue(dataRootKey, out var knownProofs))
            {
                knownProofs = new List<AggregatedSignatureProof>();
                _knownAggregatedPayloads[dataRootKey] = knownProofs;
            }
            knownProofs.Add(proof);

            if (proof.Participants.TryToValidatorIndices(out var vids))
            {
                foreach (var vid in vids)
                    _pendingAttestations[vid] = attestation.Data;
            }
        }

        // Proposer attestation signature enters gossip pool for future aggregation.
        var proposerDataRootKey = ToDataRootKey(proposerAttestation.Data);
        _attestationDataByRoot[proposerDataRootKey] = proposerAttestation.Data;
        _gossipSignatures[(proposerAttestation.ValidatorId, proposerDataRootKey)] =
            signedBlock.Signature.ProposerSignature;
        _pendingAttestations[proposerAttestation.ValidatorId] = proposerAttestation.Data;

        // Recompute head after every block using proto-array (O(N) propagation + O(1) head).
        // IsViable is disabled (always true) so FindHead follows BestDescendant purely by
        // weight, matching leanSpec's behavior. With the >= justified update, the justified
        // root switches to the majority fork, and FindHead tracks the heaviest chain tip.
        _protoArray.ApplyScoreChanges(
            new Dictionary<string, long>(), _latestJustified.Slot.Value, _latestFinalized.Slot.Value);
        var newHead = _protoArray.FindHead(
            _latestJustified.Root, _latestJustified.Slot.Value, _latestFinalized.Slot.Value);

        var headChanged = !newHead.Equals(_headRoot);
        _headRoot = newHead;
        _headSlot = _protoArray.GetSlot(newHead) ?? _headSlot;

        return ForkChoiceApplyResult.AcceptedResult(headChanged, _headSlot, _headRoot);
    }

    public void OnAttestation(SignedAttestation attestation)
    {
        _ = TryOnAttestation(attestation, storeSignature: true, out _);
    }

    public bool TryOnAttestation(SignedAttestation attestation, out string reason)
    {
        return TryOnAttestation(attestation, storeSignature: true, out reason);
    }

    public bool TryOnAttestation(SignedAttestation attestation, bool storeSignature, out string reason)
    {
        if (!TryValidateAttestationData(attestation.Message, out reason))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "TryOnAttestation REJECTED: ValidatorId={ValidatorId}, Slot={Slot}, Reason={Reason}, HeadRoot={HeadRoot}, TargetRoot={TargetRoot}, SourceRoot={SourceRoot}",
                    attestation.ValidatorId, attestation.Message.Slot.Value, reason,
                    Convert.ToHexString(attestation.Message.Head.Root.AsSpan())[..8],
                    Convert.ToHexString(attestation.Message.Target.Root.AsSpan())[..8],
                    Convert.ToHexString(attestation.Message.Source.Root.AsSpan())[..8]);
            }
            return false;
        }

        var dataRootKey = ToDataRootKey(attestation.Message);
        _attestationDataByRoot[dataRootKey] = attestation.Message;
        _pendingAttestations[attestation.ValidatorId] = attestation.Message;
        if (storeSignature)
        {
            _gossipSignatures[(attestation.ValidatorId, dataRootKey)] = attestation.Signature;
            _logger.LogDebug(
                "Stored gossip signature. ValidatorId: {ValidatorId}, Slot: {Slot}, GossipSigCount: {Count}",
                attestation.ValidatorId, attestation.Message.Slot.Value, _gossipSignatures.Count);
        }
        else
        {
            _gossipSignatures.Remove((attestation.ValidatorId, dataRootKey));
        }
        reason = string.Empty;
        return true;
    }

    void IAttestationSink.AddAttestation(SignedAttestation attestation)
    {
        TryOnAttestation(attestation, storeSignature: true, out _);
    }

    /// <summary>
    /// Collects gossiped committee signatures grouped by attestation data root.
    /// Returns groups of (AttestationData, validatorIds, signatures).
    /// </summary>
    public List<(AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)> CollectAttestationsForAggregation()
    {
        _logger.LogDebug(
            "CollectAttestationsForAggregation. GossipSignatureCount: {Count}, AttestationDataCount: {DataCount}",
            _gossipSignatures.Count, _attestationDataByRoot.Count);

        var groups = new Dictionary<string, (AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)>(StringComparer.Ordinal);
        var consumedKeys = new List<(ulong, string)>();

        foreach (var (key, signature) in _gossipSignatures)
        {
            if (!_attestationDataByRoot.TryGetValue(key.Item2, out var data))
                continue;

            if (!groups.TryGetValue(key.Item2, out var group))
            {
                group = (data, new List<ulong>(), new List<XmssSignature>());
                groups[key.Item2] = group;
            }

            group.ValidatorIds.Add(key.Item1);
            group.Signatures.Add(signature);
            consumedKeys.Add(key);
        }

        foreach (var key in consumedKeys)
        {
            _gossipSignatures.Remove(key);
        }

        // Sort each group by validator ID (ascending) so aggregation proof
        // matches the verification order used by other clients (ethlambda),
        // which extract public keys in ascending order from the bitfield.
        var result = new List<(AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)>();
        foreach (var group in groups.Values)
        {
            var ids = group.ValidatorIds;
            var sigs = group.Signatures;
            var indices = new int[ids.Count];
            for (var i = 0; i < indices.Length; i++)
                indices[i] = i;
            Array.Sort(indices, (a, b) => ids[a].CompareTo(ids[b]));
            var sortedIds = new List<ulong>(ids.Count);
            var sortedSigs = new List<XmssSignature>(ids.Count);
            foreach (var idx in indices)
            {
                sortedIds.Add(ids[idx]);
                sortedSigs.Add(sigs[idx]);
            }
            result.Add((group.Data, sortedIds, sortedSigs));
        }

        return result;
    }

    /// <summary>
    /// Called at each interval within a slot.
    /// At interval 0 and the last interval (IntervalsPerSlot - 1), promotes pending
    /// attestations and recomputes the head. This matches the leanSpec/ethlambda
    /// behavior where the proposer at interval 0 needs up-to-date attestation state
    /// before building a block.
    /// </summary>
    public void TickInterval(ulong slot, int intervalInSlot)
    {
        _currentSlot = slot;

        if (intervalInSlot == 3)
        {
            UpdateSafeTarget();
            return;
        }

        // Promote and recompute head at interval 0 (before block proposal) and
        // interval 4 (end of slot). All other intervals are no-ops for the store.
        if (intervalInSlot != 0 && intervalInSlot != IntervalsPerSlot - 1)
            return;

        AcceptNewAttestations();
    }

    private void AcceptNewAttestations()
    {
        _logger.LogDebug(
            "AcceptNewAttestations START: PendingCount={PendingCount}, KnownCount={KnownCount}",
            _pendingAttestations.Count, _knownAttestations.Count);

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

        // Promote aggregated payloads: new → known.
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

        // Use proto-array for head selection (O(N) propagation + O(1) head lookup).
        // Viability is disabled, so FindHead follows the heaviest chain tip purely by weight.
        _protoArray.ApplyScoreChanges(deltas, _latestJustified.Slot.Value, _latestFinalized.Slot.Value);
        var newHead = _protoArray.FindHead(
            _latestJustified.Root, _latestJustified.Slot.Value, _latestFinalized.Slot.Value);
        _headRoot = newHead;
        _headSlot = _protoArray.GetSlot(newHead) ?? _headSlot;

        // Slot-based pruning: remove attestation data older than MaxAttestationAgeSlots
        // regardless of finalization state. This bounds memory growth when finalization stalls.
        if (_currentSlot > (ulong)MaxAttestationAgeSlots)
        {
            PruneAttestationDataOlderThan(_currentSlot - (ulong)MaxAttestationAgeSlots);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var payloadBytes = _knownAggregatedPayloads.Values
                .Sum(list => list.Sum(p => (long)p.ProofData.Length));
            _logger.LogDebug(
                "AcceptNewAttestations. Slot: {Slot}, PendingPromoted: {Promoted}, DeltaCount: {DeltaCount}, FindHeadRoot: {HeadRoot}, FindHeadSlot: {HeadSlot}, JustifiedRoot: {JRoot}, JustifiedSlot: {JSlot}, FinalizedSlot: {FSlot}, ProtoNodeCount: {NodeCount}, PayloadPoolBytes: {PoolBytes}, PayloadPoolCount: {PoolCount}",
                _currentSlot, deltas.Count, deltas.Values.Sum(v => Math.Abs(v)),
                ProtoArray.RootKey(_headRoot)[..8], _headSlot,
                ProtoArray.RootKey(_latestJustified.Root)[..8], _latestJustified.Slot.Value,
                _latestFinalized.Slot.Value, _protoArray.NodeCount,
                payloadBytes, _knownAggregatedPayloads.Values.Sum(v => v.Count));
        }
    }

    private void UpdateStoreCheckpoints(Checkpoint canonicalJustified, Checkpoint canonicalFinalized)
    {
        // Match ethlambda: strictly greater. Using >= causes the justified root to
        // flip-flop between forks when they produce the same justified slot but
        // different roots. This taints attestation source checkpoints with the wrong
        // root, causing the state transition to skip them (source.Root != history[slot]).
        if (canonicalJustified.Slot.Value > _latestJustified.Slot.Value)
        {
            _latestJustified = canonicalJustified;
        }

        if (canonicalFinalized.Slot.Value <= _latestFinalized.Slot.Value)
        {
            return;
        }

        _latestFinalized = canonicalFinalized;
        _protoArray.Prune(_latestFinalized.Root);
        PruneFinalizedAttestationData();
    }

    private void PruneFinalizedAttestationData()
    {
        PruneAttestationDataOlderThan(_latestFinalized.Slot.Value);
    }

    /// <summary>
    /// Slot-based pruning: removes all attestation data, gossip signatures, and
    /// aggregated payloads for slots at or below the given cutoff. Called both on
    /// finalization advances (cutoff = finalized slot) and periodically on tick
    /// (cutoff = currentSlot - MaxAttestationAgeSlots) to bound memory growth
    /// even when finalization stalls.
    /// </summary>
    private void PruneAttestationDataOlderThan(ulong cutoffSlot)
    {
        var staleDataKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in _attestationDataByRoot)
        {
            if (kv.Value.Slot.Value <= cutoffSlot)
                staleDataKeys.Add(kv.Key);
        }

        if (staleDataKeys.Count == 0)
            return;

        var staleSignatureKeys = new List<(ulong, string)>();
        foreach (var key in _gossipSignatures.Keys)
        {
            if (staleDataKeys.Contains(key.Item2))
                staleSignatureKeys.Add(key);
        }
        foreach (var key in staleSignatureKeys)
        {
            _gossipSignatures.Remove(key);
        }

        foreach (var dataKey in staleDataKeys)
        {
            _attestationDataByRoot.Remove(dataKey);
            _knownAggregatedPayloads.Remove(dataKey);
            _newAggregatedPayloads.Remove(dataKey);
        }

        // Prune validator attestation records whose data is now stale.
        var staleVids = new List<ulong>();
        foreach (var kv in _knownAttestations)
        {
            if (kv.Value.Slot.Value <= cutoffSlot)
                staleVids.Add(kv.Key);
        }
        foreach (var vid in staleVids)
            _knownAttestations.Remove(vid);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var payloadBytes = _knownAggregatedPayloads.Values
                .Sum(list => list.Sum(p => (long)p.ProofData.Length));
            _logger.LogDebug(
                "PruneAttestationData. CutoffSlot: {CutoffSlot}, PrunedDataKeys: {PrunedCount}, RemainingDataKeys: {RemainingDataKeys}, RemainingSignatures: {RemainingSigs}, RemainingPayloads: {RemainingPayloads}, PayloadBytes: {PayloadBytes}",
                cutoffSlot, staleDataKeys.Count, _attestationDataByRoot.Count,
                _gossipSignatures.Count, _knownAggregatedPayloads.Values.Sum(v => v.Count),
                payloadBytes);
        }

    }

    /// <summary>
    /// Computes safe_target at interval 3 by running LMD GHOST with a 2/3 supermajority
    /// threshold. Merges both attestation pools (known + new) for the full picture.
    /// Matches leanSpec update_safe_target() / ethlambda update_safe_target().
    /// </summary>
    private void UpdateSafeTarget()
    {
        var numValidators = _validatorCount;
        // ceil(2/3 * numValidators)
        var minTargetScore = (long)((numValidators * 2 + 2) / 3);

        // Merge both attestation pools to extract per-validator votes
        var allAttestations = new Dictionary<ulong, AttestationData>();

        // Known individual attestations (already promoted)
        foreach (var (vid, data) in _knownAttestations)
            allAttestations[vid] = data;

        // Pending individual attestations (not yet promoted)
        foreach (var (vid, data) in _pendingAttestations)
            allAttestations[vid] = data;

        // Extract votes from known aggregated payloads
        ExtractVotesFromPayloads(_knownAggregatedPayloads, allAttestations);

        // Extract votes from new aggregated payloads
        ExtractVotesFromPayloads(_newAggregatedPayloads, allAttestations);

        // Run LMD GHOST with min_score threshold
        _safeTarget = ComputeLmdGhostHead(_latestJustified.Root, allAttestations, minTargetScore);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var safeSlot = _protoArray.GetSlot(_safeTarget) ?? 0UL;
            _logger.LogDebug(
                "UpdateSafeTarget. ValidatorCount: {ValidatorCount}, MinTargetScore: {MinTargetScore}, AttestationVoters: {AttestationVoters}, SafeTargetSlot: {SafeTargetSlot}, SafeTargetRoot: {SafeTargetRoot}",
                numValidators, minTargetScore, allAttestations.Count, safeSlot, Convert.ToHexString(_safeTarget.AsSpan())[..8]);
        }
    }

    private void ExtractVotesFromPayloads(
        Dictionary<string, List<AggregatedSignatureProof>> payloads,
        Dictionary<ulong, AttestationData> target)
    {
        foreach (var (dataRootKey, proofs) in payloads)
        {
            if (!_attestationDataByRoot.TryGetValue(dataRootKey, out var data))
                continue;

            foreach (var proof in proofs)
            {
                if (proof.Participants.TryToValidatorIndices(out var ids))
                {
                    foreach (var id in ids)
                        target[id] = data;
                }
            }
        }
    }

    /// <summary>
    /// LMD GHOST implementation for safe_target computation.
    /// Walks from start_root, accumulating attestation weights, filtering by min_score.
    /// Matches leanSpec _compute_lmd_ghost_head() / ethlambda compute_lmd_ghost_head().
    /// </summary>
    private Bytes32 ComputeLmdGhostHead(
        Bytes32 startRoot,
        Dictionary<ulong, AttestationData> attestations,
        long minScore)
    {
        var startSlot = _protoArray.GetSlot(startRoot) ?? 0UL;

        // Build blocks lookup: root -> (slot, parentRoot)
        var blocks = new Dictionary<string, (ulong Slot, Bytes32 ParentRoot)>(StringComparer.Ordinal);
        foreach (var (root, slot, parentRoot) in _protoArray.GetAllBlocks())
            blocks[ProtoArray.RootKey(root)] = (slot, parentRoot);

        // Compute per-block weights by walking from each vote's head back to start
        var weights = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var data in attestations.Values)
        {
            var currentKey = ProtoArray.RootKey(data.Head.Root);
            while (blocks.TryGetValue(currentKey, out var blk) && blk.Slot > startSlot)
            {
                weights[currentKey] = weights.GetValueOrDefault(currentKey) + 1;
                currentKey = ProtoArray.RootKey(blk.ParentRoot);
            }
        }

        // Build children map, filtering by min_score
        var childrenMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (rootKey, blk) in blocks)
        {
            var parentKey = ProtoArray.RootKey(blk.ParentRoot);
            if (blk.ParentRoot.Equals(Bytes32.Zero()))
                continue;

            if (minScore > 0 && weights.GetValueOrDefault(rootKey) < minScore)
                continue;

            if (!childrenMap.TryGetValue(parentKey, out var children))
            {
                children = new List<string>();
                childrenMap[parentKey] = children;
            }
            children.Add(rootKey);
        }

        // Greedy walk from start_root following heaviest child
        var head = ProtoArray.RootKey(startRoot);
        while (childrenMap.TryGetValue(head, out var ch) && ch.Count > 0)
        {
            // Pick child with highest weight, tie-break by lexicographic root key
            head = ch.OrderByDescending(c => weights.GetValueOrDefault(c))
                     .ThenByDescending(c => c, StringComparer.Ordinal)
                     .First();
        }

        // Convert hex key back to Bytes32
        return new Bytes32(Convert.FromHexString(head));
    }

    /// <summary>
    /// Computes attestation target checkpoint matching leanSpec get_attestation_target():
    /// 1. Start at head
    /// 2. Walk back toward safe_target (max JUSTIFICATION_LOOKBACK_SLOTS steps)
    /// 3. Walk back until slot is justifiable after finalized_slot
    /// 4. Clamp to at least justified checkpoint (ethlambda safeguard)
    /// </summary>
    public Checkpoint ComputeTargetCheckpoint()
    {
        var targetRoot = _headRoot;
        var targetSlot = _headSlot;

        var safeTargetSlot = _protoArray.GetSlot(_safeTarget) ?? 0UL;
        var finalizedSlot = _latestFinalized.Slot.Value;

        var initialHeadSlot = targetSlot;

        // Walk back toward safe target (up to JUSTIFICATION_LOOKBACK_SLOTS steps).
        // Matches leanSpec: no justified guard — just walk toward safe_target.
        for (var i = 0; i < JustificationLookbackSlots; i++)
        {
            if (targetSlot > safeTargetSlot)
            {
                var parent = _protoArray.GetParentRoot(targetRoot);
                if (!parent.HasValue)
                    break;
                targetRoot = parent.Value;
                targetSlot = _protoArray.GetSlot(targetRoot) ?? targetSlot;
            }
            else
            {
                break;
            }
        }

        // Walk back until slot is justifiable after finalized_slot
        while (targetSlot > finalizedSlot &&
               !new Slot(targetSlot).IsJustifiableAfter(new Slot(finalizedSlot)))
        {
            var parent = _protoArray.GetParentRoot(targetRoot);
            if (!parent.HasValue)
                break;
            targetRoot = parent.Value;
            targetSlot = _protoArray.GetSlot(targetRoot) ?? targetSlot;
        }

        var justifiedSlot = _latestJustified.Slot.Value;

        _logger.LogDebug(
            "ComputeTargetCheckpoint. HeadSlot: {HeadSlot}, SafeTargetSlot: {SafeTargetSlot}, JustifiedSlot: {JustifiedSlot}, FinalizedSlot: {FinalizedSlot}, FinalTarget: {FinalTarget}",
            initialHeadSlot, safeTargetSlot, justifiedSlot, finalizedSlot, targetSlot);

        // Clamp: target must not walk behind justified (ethlambda safeguard).
        // When a block advances latest_justified between safe_target updates,
        // the walk-back above can land on a slot behind the new justified checkpoint.
        if (targetSlot < justifiedSlot)
        {
            _logger.LogDebug(
                "Attestation target walked behind justified source, clamping. TargetSlot: {TargetSlot}, JustifiedSlot: {JustifiedSlot}",
                targetSlot, justifiedSlot);
            return _latestJustified;
        }

        return new Checkpoint(targetRoot, new Slot(targetSlot));
    }

    private static string ToDataRootKey(AttestationData data)
    {
        return Convert.ToHexString(data.HashTreeRoot());
    }

    private bool TryValidateAttestationData(AttestationData data, out string reason)
    {
        if (data.Source.Slot.Value > data.Target.Slot.Value)
        {
            reason = "Source checkpoint slot exceeds target checkpoint slot.";
            return false;
        }

        if (data.Head.Slot.Value < data.Target.Slot.Value)
        {
            reason = "Head checkpoint must not be older than target checkpoint.";
            return false;
        }

        if (data.Slot.Value > _currentSlot + 1)
        {
            reason = $"Attestation slot {data.Slot.Value} is too far in the future for current slot {_currentSlot}.";
            return false;
        }

        if (!_protoArray.ContainsBlock(data.Source.Root))
        {
            reason = $"Unknown source root {data.Source.Root}.";
            return false;
        }

        if (!_protoArray.ContainsBlock(data.Target.Root))
        {
            reason = $"Unknown target root {data.Target.Root}.";
            return false;
        }

        if (!_protoArray.ContainsBlock(data.Head.Root))
        {
            reason = $"Unknown head root {data.Head.Root}.";
            return false;
        }

        var sourceSlot = _protoArray.GetSlot(data.Source.Root);
        if (!sourceSlot.HasValue || sourceSlot.Value != data.Source.Slot.Value)
        {
            reason = "Source checkpoint slot does not match source block slot.";
            return false;
        }

        var targetSlot = _protoArray.GetSlot(data.Target.Root);
        if (!targetSlot.HasValue || targetSlot.Value != data.Target.Slot.Value)
        {
            reason = "Target checkpoint slot does not match target block slot.";
            return false;
        }

        var headSlot = _protoArray.GetSlot(data.Head.Root);
        if (!headSlot.HasValue || headSlot.Value != data.Head.Slot.Value)
        {
            reason = "Head checkpoint slot does not match head block slot.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
