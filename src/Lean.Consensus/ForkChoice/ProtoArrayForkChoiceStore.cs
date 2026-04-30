using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using Lean.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lean.Consensus.ForkChoice;

public readonly record struct GossipSignatureEntry(ulong ValidatorId, XmssSignature Signature);

/// <summary>
/// Compares <see cref="GossipSignatureEntry"/> by <see cref="GossipSignatureEntry.ValidatorId"/> only.
/// XmssSignature is a reference type without value equality, so the default record struct
/// equality would fail to deduplicate entries from the same validator.
/// </summary>
internal sealed class GossipSignatureEntryByValidatorComparer : IEqualityComparer<GossipSignatureEntry>
{
    public static readonly GossipSignatureEntryByValidatorComparer Instance = new();
    public bool Equals(GossipSignatureEntry x, GossipSignatureEntry y) => x.ValidatorId == y.ValidatorId;
    public int GetHashCode(GossipSignatureEntry obj) => obj.ValidatorId.GetHashCode();
}

public sealed class ProtoArrayForkChoiceStore : IAttestationSink
{
    public const int IntervalsPerSlot = 5;
    public const int JustificationLookbackSlots = 3;
    public const int MaxAttestationAgeSlots = 16;

    // Future-slot tolerance for gossip attestations, in intervals.
    // Bounds the clock skew the time check is willing to absorb when admitting
    // a vote whose slot has not yet started locally. One interval is ~800 ms.
    // (leanSpec GOSSIP_DISPARITY_INTERVALS, see #682.)
    public const ulong GossipDisparityIntervals = 1;

    public object SyncRoot { get; } = new object();

    private readonly ProtoArray _protoArray;
    private readonly Dictionary<ulong, AttestationTracker> _attestationTrackers = new();
    private readonly Dictionary<string, (AttestationData Data, HashSet<GossipSignatureEntry> Entries)> _gossipSignatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AttestationData Data, HashSet<AggregatedSignatureProof> Proofs)> _newAggregatedPayloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AttestationData Data, HashSet<AggregatedSignatureProof> Proofs)> _knownAggregatedPayloads = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    private long[] _deltas = Array.Empty<long>();

    private Bytes32 _headRoot;
    private ulong _headSlot;
    private ulong _currentSlot;

    // Total intervals elapsed since genesis (= _currentSlot * IntervalsPerSlot
    // + intervalInSlot). Mirrors the spec's `Store.time`, in intervals. Used
    // by the gossip-attestation future-slot bound.
    private ulong _currentTimeIntervals;
    private Checkpoint _latestJustified;
    private Checkpoint _latestFinalized;
    private Bytes32 _safeTarget;
    private ulong _maxPeerHeadSlot;
    private ulong _validatorCount;
    private readonly IReadOnlySet<ulong> _localValidatorIds;
    private readonly int _attestationCommitteeCount;
    private enum VoteSource
    {
        Known,
        New,
        Merged
    }

    // Callback for requesting a block by root when fork choice / attestation
    // validation encounters a root that's not in proto-array. Wired to the
    // SyncService's BackfillTrigger by ConsensusServiceV2. Optional so tests
    // that don't need sync behavior can skip it.
    private readonly Action<Bytes32>? _requestBlockByRoot;
    private readonly int _pruneNodeThreshold;

    public ProtoArrayForkChoiceStore(
        ConsensusConfig config,
        IConsensusStateStore? stateStore = null,
        ILogger<ProtoArrayForkChoiceStore>? logger = null,
        Action<Bytes32>? requestBlockByRoot = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _requestBlockByRoot = requestBlockByRoot;

        _validatorCount = Math.Max(1UL, config.InitialValidatorCount);
        _localValidatorIds = config.LocalValidatorIds;
        _attestationCommitteeCount = Math.Max(1, config.AttestationCommitteeCount);
        _pruneNodeThreshold = Math.Max(0, config.PruneNodeThreshold);
        _logger = logger ?? (ILogger)NullLogger<ProtoArrayForkChoiceStore>.Instance;

        ConsensusHeadState? loaded = null;
        stateStore?.TryLoad(out loaded);

        if (loaded is not null
            && loaded.HeadRoot.Length == 32
            && loaded.HeadSlot > 0)
        {
            var headRoot = new Bytes32(loaded.HeadRoot);
            var justifiedRoot = new Bytes32(loaded.LatestJustifiedRoot);
            var finalizedRoot = new Bytes32(loaded.LatestFinalizedRoot);

            _headRoot = headRoot;
            _headSlot = loaded.HeadSlot;
            _latestJustified = new Checkpoint(justifiedRoot, new Slot(loaded.LatestJustifiedSlot));
            _latestFinalized = new Checkpoint(finalizedRoot, new Slot(loaded.LatestFinalizedSlot));
            _currentSlot = loaded.HeadSlot;
            _currentTimeIntervals = loaded.HeadSlot * (ulong)IntervalsPerSlot;
            _safeTarget = new Bytes32(loaded.SafeTargetRoot);

            var rootSlot = headRoot.Equals(finalizedRoot) && loaded.HeadSlot != loaded.LatestFinalizedSlot
                ? loaded.HeadSlot
                : loaded.LatestFinalizedSlot;
            _protoArray = new ProtoArray(finalizedRoot, rootSlot, loaded.LatestJustifiedSlot, loaded.LatestFinalizedSlot);

            // Register justifiedRoot as an explicit block so local attestations built
            // with Source = current justified pass the ContainsBlock check in
            // TryValidateAttestationData. Without this, a node that restarts while
            // justifiedRoot ≠ finalizedRoot (the normal case) rejects every local
            // attestation with "Unknown source root" until a new justification
            // advances _latestJustified to a block that is in proto-array — which
            // never happens when ≥50% of validators restart together and the
            // remaining set cannot reach supermajority.
            // Checkpoint-sync path: the historical Block objects aren't available, so the
            // true proposer_index isn't known. leanSpec's proposer selection is deterministic
            // (`slot % num_validators`), so we use the same formula as a canonical fallback.
            var modulus = Math.Max(1UL, _validatorCount);
            var headParent = finalizedRoot;
            if (!justifiedRoot.Equals(finalizedRoot))
            {
                _protoArray.RegisterBlock(justifiedRoot, finalizedRoot, loaded.LatestJustifiedSlot,
                    loaded.LatestJustifiedSlot, loaded.LatestFinalizedSlot,
                    loaded.LatestJustifiedSlot % modulus);
                headParent = justifiedRoot;
            }

            if (!headRoot.Equals(finalizedRoot) && !headRoot.Equals(justifiedRoot))
            {
                _protoArray.RegisterBlock(headRoot, headParent, loaded.HeadSlot,
                    loaded.LatestJustifiedSlot, loaded.LatestFinalizedSlot,
                    loaded.HeadSlot % modulus);
            }

            _logger.LogInformation(
                "Loaded checkpoint state. HeadSlot={HeadSlot}, FinalizedSlot={FinalizedSlot}, JustifiedSlot={JustifiedSlot}",
                loaded.HeadSlot, loaded.LatestFinalizedSlot, loaded.LatestJustifiedSlot);
        }
        else
        {
            var chainTransition = new ChainStateTransition(config);
            var genesisState = chainTransition.CreateGenesisState(_validatorCount);
            var genesisRoot = ChainStateTransition.GenesisBlockRoot(genesisState);

            var genesisCheckpoint = new Checkpoint(genesisRoot, new Slot(0));
            _latestJustified = genesisCheckpoint;
            _latestFinalized = genesisCheckpoint;
            _headRoot = genesisRoot;
            _headSlot = 0;
            _currentSlot = 0;
            _currentTimeIntervals = 0;
            _protoArray = new ProtoArray(genesisRoot, 0, 0);
            _safeTarget = genesisRoot;
        }
    }

    public Bytes32 HeadRoot => _headRoot;
    public ulong HeadSlot => _headSlot;
    public ulong JustifiedSlot => _latestJustified.Slot.Value;
    public Bytes32 JustifiedRoot => _latestJustified.Root;
    public ulong FinalizedSlot => _latestFinalized.Slot.Value;
    public Bytes32 FinalizedRoot => _latestFinalized.Root;
    public bool IsReadyForDuties
    {
        get
        {
            var tolerance = Math.Max(8UL, _validatorCount * 2 / 3);
            if (_currentSlot > _headSlot + tolerance && _maxPeerHeadSlot > _headSlot + 2)
                return false;
            return true;
        }
    }
    public Bytes32 SafeTarget => _safeTarget;
    public ProtoArray ProtoArray => _protoArray;
    public ulong ValidatorCount => _validatorCount;
    public bool ContainsBlock(Bytes32 root) => _protoArray.ContainsBlock(root);
    public int PendingAggregatedPayloadCount
    {
        get
        {
            int count = 0;
            foreach (var (_, proofs) in _newAggregatedPayloads.Values)
                count += proofs.Count;
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

    public void OnGossipSignature(ulong validatorId, AttestationData data, XmssSignature signature)
    {
        var dataRootKey = ToDataRootKey(data);
        if (!_gossipSignatures.TryGetValue(dataRootKey, out var entry))
        {
            entry = (data, new HashSet<GossipSignatureEntry>(GossipSignatureEntryByValidatorComparer.Instance));
            _gossipSignatures[dataRootKey] = entry;
        }
        entry.Entries.Add(new GossipSignatureEntry(validatorId, signature));
        LeanMetrics.SetGossipSignatures(_gossipSignatures.Values.Sum(v => v.Entries.Count));
    }

    public bool HasGossipSignature(ulong validatorId, Bytes32 dataRoot)
    {
        var dataRootKey = ProtoArray.RootKey(dataRoot);
        if (!_gossipSignatures.TryGetValue(dataRootKey, out var entry))
            return false;
        return entry.Entries.Contains(new GossipSignatureEntry(validatorId, null!));
    }

    public void OnGossipAggregatedAttestation(SignedAggregatedAttestation signed)
    {
        _ = TryOnGossipAggregatedAttestation(signed, out _);
    }

    public bool ApplyLocalAggregationResult(SignedAggregatedAttestation signed, out string reason)
    {
        if (!signed.Proof.Participants.TryToValidatorIndices(out var participantIds) || participantIds.Count == 0)
        {
            reason = "Aggregated attestation must include at least one participant.";
            return false;
        }

        var dataRootKey = ToDataRootKey(signed.Data);
        if (!_knownAggregatedPayloads.TryGetValue(dataRootKey, out var entry))
        {
            entry = (signed.Data, new HashSet<AggregatedSignatureProof>());
            _knownAggregatedPayloads[dataRootKey] = entry;
        }
        entry.Proofs.Add(signed.Proof);
        LeanMetrics.SetLatestKnownAggregatedPayloads(
            _knownAggregatedPayloads.Values.Sum(v => v.Proofs.Count));

        reason = string.Empty;
        return true;
    }

    public bool TryOnGossipAggregatedAttestation(SignedAggregatedAttestation signed, out string reason)
    {
        if (!signed.Proof.Participants.TryToValidatorIndices(out var participantIds) || participantIds.Count == 0)
        {
            reason = "Aggregated attestation must include at least one participant.";
            return false;
        }

        var dataRootKey = ToDataRootKey(signed.Data);
        if (!_newAggregatedPayloads.TryGetValue(dataRootKey, out var entry))
        {
            entry = (signed.Data, new HashSet<AggregatedSignatureProof>());
            _newAggregatedPayloads[dataRootKey] = entry;
        }
        entry.Proofs.Add(signed.Proof);
        LeanMetrics.SetLatestNewAggregatedPayloads(
            _newAggregatedPayloads.Values.Sum(v => v.Proofs.Count));

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Extract per-validator attestation data from ALL known aggregated payloads,
    /// filtering only by slot (attestation.slot &lt; blockSlot).
    /// Used by the fixed-point block building loop.
    /// </summary>
    public List<Attestation> ExtractAllAttestationsFromKnownPayloads(ulong blockSlot)
    {
        var perValidator = new Dictionary<ulong, AttestationData>();

        foreach (var (dataRootKey, (data, proofs)) in _knownAggregatedPayloads)
        {
            if (data.Slot.Value >= blockSlot)
                continue;

            foreach (var proof in proofs)
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
    /// Used by the proof selection algorithm (greedy set cover).
    /// </summary>
    public IReadOnlyDictionary<string, (AttestationData Data, HashSet<AggregatedSignatureProof> Proofs)> GetKnownPayloadPool()
    {
        return _knownAggregatedPayloads;
    }

    /// <summary>
    /// Force pending-attestation acceptance
    /// before selecting proposal head for the slot.
    /// </summary>
    public void PrepareForProposal(ulong slot)
    {
        // Interval 0 proposer path can accept new attestations.
        TickInterval(slot, 0, hasProposal: true);
        // get_proposal_head() also performs an explicit accept after ticking.
        AcceptNewAttestations();
    }

    /// <summary>
    /// Returns the per-validator latest known attestation data from AttestationTrackers.
    /// Used by block building for de-duplicated attestation sets.
    /// </summary>
    public IReadOnlyDictionary<ulong, AttestationData> GetKnownAttestations()
    {
        var result = new Dictionary<ulong, AttestationData>();
        foreach (var (vid, tracker) in _attestationTrackers)
        {
            if (tracker.LatestKnown is { } known && known.Data is not null)
                result[vid] = known.Data;
        }
        return result;
    }

    /// <summary>
    /// Registers a block in the fork choice store using checkpoints from the
    /// canonical state transition.
    /// </summary>
    public ForkChoiceApplyResult OnBlock(
        SignedBlock signedBlock,
        Checkpoint canonicalJustified,
        Checkpoint canonicalFinalized,
        ulong validatorCount)
    {
        var block = signedBlock.Block;
        var blockRoot = new Bytes32(block.HashTreeRoot());
        var aggregatedAttestations = block.Body.Attestations;
        var attestationSignatures = signedBlock.Signature.AttestationSignatures;

        if (_protoArray.ContainsBlock(blockRoot))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.DuplicateBlock,
                "Block already known.",
                _headSlot, _headRoot);
        }

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

        if (aggregatedAttestations.Count > SszEncoding.MaxAttestationsData)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Block contains {aggregatedAttestations.Count} attestation data entries, exceeding max {SszEncoding.MaxAttestationsData}.",
                _headSlot, _headRoot);
        }

        // leanSpec PR #510: each AttestationData must appear at most once per block.
        var seenDataRoots = new HashSet<Bytes32>();
        for (var i = 0; i < aggregatedAttestations.Count; i++)
        {
            var key = new Bytes32(aggregatedAttestations[i].Data.HashTreeRoot());
            if (!seenDataRoots.Add(key))
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.InvalidAttestation,
                    "Block contains duplicate AttestationData entries.",
                    _headSlot, _headRoot);
            }
        }

        _protoArray.RegisterBlock(
            blockRoot, block.ParentRoot, block.Slot.Value,
            canonicalJustified.Slot.Value,
            canonicalFinalized.Slot.Value,
            block.ProposerIndex);

        _validatorCount = Math.Max(_validatorCount, validatorCount);

        UpdateStoreCheckpoints(canonicalJustified, canonicalFinalized);

        // Process block-body attestations: update tracker.LatestKnown (is_from_block=true).
        // Also store aggregated payloads as known for block building.
        for (var i = 0; i < aggregatedAttestations.Count; i++)
        {
            var attestation = aggregatedAttestations[i];
            var proof = attestationSignatures[i];
            var dataRootKey = ToDataRootKey(attestation.Data);

            if (!_knownAggregatedPayloads.TryGetValue(dataRootKey, out var knownEntry))
            {
                knownEntry = (attestation.Data, new HashSet<AggregatedSignatureProof>());
                _knownAggregatedPayloads[dataRootKey] = knownEntry;
            }
            knownEntry.Proofs.Add(proof);

            // Use AggregationBits to extract per-validator votes from block attestations.
            if (attestation.AggregationBits.TryToValidatorIndices(out var vids))
            {
                foreach (var vid in vids)
                {
                    var headIndex = _protoArray.GetIndex(attestation.Data.Head.Root);
                    if (headIndex.HasValue)
                        UpdateTrackerFromBlock(vid, headIndex.Value, attestation.Data.Slot.Value, attestation.Data);
                }
            }
        }

        // Compute head using full delta rebuild.
        var newHead = ComputeForkChoiceHead(VoteSource.Known, cutoffWeight: 0);

        var headChanged = !newHead.Root.Equals(_headRoot);
        _headRoot = newHead.Root;
        _headSlot = newHead.Slot;

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

        // Tracker updates happen in AcceptNewAttestations.
        // Individual attestations feed into aggregation; the aggregated payloads
        // are unpacked into per-validator trackers at tick intervals 0/4.

        if (storeSignature)
        {
            var dataRootKey = ToDataRootKey(attestation.Message);
            if (!_gossipSignatures.TryGetValue(dataRootKey, out var entry))
            {
                entry = (attestation.Message, new HashSet<GossipSignatureEntry>(GossipSignatureEntryByValidatorComparer.Instance));
                _gossipSignatures[dataRootKey] = entry;
            }
            entry.Entries.Add(new GossipSignatureEntry(attestation.ValidatorId, attestation.Signature));
            _logger.LogDebug(
                "Stored gossip signature. ValidatorId: {ValidatorId}, Slot: {Slot}, GossipSigCount: {Count}",
                attestation.ValidatorId, attestation.Message.Slot.Value,
                _gossipSignatures.Values.Sum(v => v.Entries.Count));
        }
        else
        {
            var dataRootKey = ToDataRootKey(attestation.Message);
            if (_gossipSignatures.TryGetValue(dataRootKey, out var entry))
            {
                entry.Entries.Remove(new GossipSignatureEntry(attestation.ValidatorId, null!));
                if (entry.Entries.Count == 0)
                    _gossipSignatures.Remove(dataRootKey);
            }
        }
        LeanMetrics.SetGossipSignatures(_gossipSignatures.Values.Sum(v => v.Entries.Count));
        reason = string.Empty;
        return true;
    }

    void IAttestationSink.AddAttestation(SignedAttestation attestation)
    {
        TryOnAttestation(attestation, storeSignature: true, out _);
    }

    bool IAttestationSink.TryAddAttestation(SignedAttestation attestation)
    {
        return TryOnAttestation(attestation, storeSignature: true, out _);
    }

    /// <summary>
    /// Collects gossiped committee signatures grouped by attestation data root.
    /// </summary>
    public List<(AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)> CollectAttestationsForAggregation()
    {
        _logger.LogDebug(
            "CollectAttestationsForAggregation. GossipSignatureCount: {Count}",
            _gossipSignatures.Values.Sum(v => v.Entries.Count));

        var result = new List<(AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)>();
        var consumedKeys = new List<string>();

        foreach (var (dataRootKey, (data, entries)) in _gossipSignatures)
        {
            if (entries.Count == 0) continue;

            // Sort by validatorId for deterministic aggregation
            var sorted = entries.OrderBy(e => e.ValidatorId).ToList();
            var ids = sorted.Select(e => e.ValidatorId).ToList();
            var sigs = sorted.Select(e => e.Signature).ToList();
            result.Add((data, ids, sigs));
            consumedKeys.Add(dataRootKey);
        }

        foreach (var key in consumedKeys)
        {
            _gossipSignatures.Remove(key);
        }

        return result;
    }

    /// <summary>
    /// Called at each interval within a slot.
    /// Tick interval mapping:
    ///   0 = accept_new_attestations (if has_proposal)
    ///   1 = nothing
    ///   2 = nothing
    ///   3 = update_safe_target
    ///   4 = accept_new_attestations (unconditional)
    /// </summary>
    public void TickInterval(ulong slot, int intervalInSlot, bool hasProposal = false, ulong maxPeerHeadSlot = 0UL)
    {
        _currentSlot = slot;
        _currentTimeIntervals = slot * (ulong)IntervalsPerSlot + (ulong)intervalInSlot;
        _maxPeerHeadSlot = maxPeerHeadSlot;

        switch (intervalInSlot)
        {
            case 0 when hasProposal:
                AcceptNewAttestations();
                break;
            case 3:
                UpdateSafeTarget();
                break;
            case 4:
                AcceptNewAttestations();
                break;
        }
    }

    /// <summary>
    /// Promote latestNew → latestKnown for all validators,
    /// then recompute head.
    /// </summary>
    private void AcceptNewAttestations()
    {
        _logger.LogDebug(
            "AcceptNewAttestations START: TrackerCount={TrackerCount}",
            _attestationTrackers.Count);

        // Step 1: Unpack received aggregated payloads into per-validator latestNew.
        // This is the sole path for tracker updates from
        // aggregated attestations. Gossip receive only stores payloads.
        foreach (var (dataRootKey, (data, proofs)) in _newAggregatedPayloads)
        {
            var headIndex = _protoArray.GetIndex(data.Head.Root);
            if (!headIndex.HasValue)
                continue;

            foreach (var proof in proofs)
            {
                if (!proof.Participants.TryToValidatorIndices(out var pids))
                    continue;

                foreach (var pid in pids)
                    UpdateTrackerFromGossip(pid, headIndex.Value, data.Slot.Value, data);
            }
        }

        // Step 2: Migrate aggregated payloads new -> known (for block building).
        foreach (var (key, (data, payloads)) in _newAggregatedPayloads)
        {
            if (!_knownAggregatedPayloads.TryGetValue(key, out var knownEntry))
            {
                knownEntry = (data, new HashSet<AggregatedSignatureProof>());
                _knownAggregatedPayloads[key] = knownEntry;
            }
            foreach (var p in payloads) knownEntry.Proofs.Add(p);
        }
        _newAggregatedPayloads.Clear();
        LeanMetrics.SetLatestNewAggregatedPayloads(0);
        LeanMetrics.SetLatestKnownAggregatedPayloads(
            _knownAggregatedPayloads.Values.Sum(v => v.Proofs.Count));

        // Step 3: Promote latestNew → latestKnown for all validators.
        // Once a vote has been accepted, latestNew
        // continues to mirror latestKnown instead of being cleared. This keeps the
        // tracker in a state where safe_target (which reads latestNew) still sees
        // accepted votes until a newer gossip vote replaces them.
        var keys = new List<ulong>(_attestationTrackers.Keys);
        foreach (var vid in keys)
        {
            var tracker = _attestationTrackers[vid];
            if (!tracker.LatestNew.HasValue)
            {
                continue;
            }

            var latestNew = tracker.LatestNew.Value;
            tracker.LatestKnown = latestNew;
            _attestationTrackers[vid] = tracker;
        }

        // Step 4: Compute head with full delta rebuild.
        var head = ComputeForkChoiceHead(VoteSource.Known, cutoffWeight: 0);
        _headRoot = head.Root;
        _headSlot = head.Slot;

        // Slot-based pruning of attestation data
        if (_currentSlot > (ulong)MaxAttestationAgeSlots)
        {
            PruneAttestationDataOlderThan(_currentSlot - (ulong)MaxAttestationAgeSlots);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var payloadBytes = _knownAggregatedPayloads.Values
                .Sum(entry => entry.Proofs.Sum(p => (long)p.ProofData.Length));
            _logger.LogDebug(
                "AcceptNewAttestations. Slot: {Slot}, HeadRoot: {HeadRoot}, HeadSlot: {HeadSlot}, JustifiedRoot: {JRoot}, JustifiedSlot: {JSlot}, FinalizedSlot: {FSlot}, ProtoNodeCount: {NodeCount}, PayloadPoolBytes: {PoolBytes}, PayloadPoolCount: {PoolCount}",
                _currentSlot,
                ProtoArray.RootKey(_headRoot)[..8], _headSlot,
                ProtoArray.RootKey(_latestJustified.Root)[..8], _latestJustified.Slot.Value,
                _latestFinalized.Slot.Value, _protoArray.NodeCount,
                payloadBytes, _knownAggregatedPayloads.Values.Sum(v => v.Proofs.Count));
        }
    }

    /// <summary>
    /// Computes safe_target at interval 3 using the same proto-array mechanism
    /// with cutoffWeight = ceil(2N/3). Reads only
    /// latestNew/new votes here; latestKnown continues to drive head/proposal.
    /// </summary>
    private void UpdateSafeTarget()
    {
        var numValidators = _validatorCount;
        var cutoffWeight = (long)((numValidators * 2 + 2) / 3);

        var safeHead = ComputeForkChoiceHead(VoteSource.New, cutoffWeight: cutoffWeight);
        var currentSafeSlot = _protoArray.GetSlot(_safeTarget) ?? 0UL;
        if (safeHead.Slot < currentSafeSlot)
        {
            _logger.LogDebug(
                "Safe target regression (allowed). NewSafeTargetSlot: {NewSafeTargetSlot}, CurrentSafeTargetSlot: {CurrentSafeTargetSlot}",
                safeHead.Slot,
                currentSafeSlot);
        }

        _safeTarget = safeHead.Root;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var safeSlot = safeHead.Slot;
            _logger.LogDebug(
                "UpdateSafeTarget. ValidatorCount: {ValidatorCount}, CutoffWeight: {CutoffWeight}, SafeTargetSlot: {SafeTargetSlot}, SafeTargetRoot: {SafeTargetRoot}",
                numValidators, cutoffWeight, safeSlot, Convert.ToHexString(_safeTarget.AsSpan())[..8]);
        }
    }

    /// <summary>
    /// Full-rebuild delta computation + proto-array head lookup.
    /// Known = LatestKnown (head election), New = LatestNew, Merged = per-validator fresher of known/new.
    /// </summary>
    private (Bytes32 Root, ulong Slot) ComputeForkChoiceHead(VoteSource source, long cutoffWeight)
    {
        var deltas = ComputeDeltas(source);
        _protoArray.ApplyDeltas(deltas, cutoffWeight);

        var justifiedIdx = _protoArray.GetIndex(_latestJustified.Root);
        if (!justifiedIdx.HasValue)
        {
            // During catch-up the justified root may not yet be in the proto-array.
            // Fall back to index 0 (finalized node) so head election still works.
            _logger.LogDebug(
                "ComputeForkChoiceHead: justified root not in proto-array, falling back to index 0. JustifiedRoot: {Root}",
                Convert.ToHexString(_latestJustified.Root.AsSpan())[..8]);
            justifiedIdx = 0;
        }

        var justifiedNode = _protoArray.GetNodeByIndex(justifiedIdx.Value);
        if (justifiedNode is null)
            return (_headRoot, _headSlot);

        var bestDescIdx = justifiedNode.BestDescendant ?? justifiedIdx.Value;
        var bestDesc = _protoArray.GetNodeByIndex(bestDescIdx);
        if (bestDesc is null)
            return (_headRoot, _headSlot);

        return (bestDesc.Root, bestDesc.Slot);
    }

    /// <summary>
    /// Full delta rebuild: for every validator, subtract old appliedIndex, add selected vote.
    /// </summary>
    private long[] ComputeDeltas(VoteSource source)
    {
        var nodeCount = _protoArray.NodeCount;

        // Grow buffer if needed
        if (_deltas.Length < nodeCount)
            _deltas = new long[nodeCount];

        // Zero-fill deltas buffer (NOT node weights — weights are maintained
        // incrementally via subtract-old/add-new delta mechanism).
        Array.Clear(_deltas, 0, nodeCount);

        foreach (var (validatorId, tracker) in _attestationTrackers)
        {
            var t = tracker;

            // Subtract old applied vote
            if (t.AppliedIndex.HasValue && t.AppliedIndex.Value < nodeCount)
                _deltas[t.AppliedIndex.Value] -= 1;

            t.AppliedIndex = null;

            // Select source based on vote set.
            ProtoAttestation? selected = source switch
            {
                VoteSource.Known => t.LatestKnown,
                VoteSource.New => t.LatestNew,
                VoteSource.Merged => SelectMergedVote(t),
                _ => null
            };

            if (selected.HasValue && selected.Value.Index < nodeCount)
            {
                _deltas[selected.Value.Index] += 1;
                t.AppliedIndex = selected.Value.Index;
            }

            _attestationTrackers[validatorId] = t;
        }

        return _deltas;
    }

    private static ProtoAttestation? SelectMergedVote(AttestationTracker tracker)
    {
        if (!tracker.LatestKnown.HasValue)
            return tracker.LatestNew;

        if (!tracker.LatestNew.HasValue)
            return tracker.LatestKnown;

        // Match extract_latest_attestations semantics: keep the freshest attestation.
        return tracker.LatestKnown.Value.Slot >= tracker.LatestNew.Value.Slot
            ? tracker.LatestKnown
            : tracker.LatestNew;
    }

    /// <summary>
    /// Update attestation tracker from block attestation (is_from_block=true).
    /// If a block vote is newer than latestKnown and
    /// also newer than latestNew, latestNew is pulled up to latestKnown instead of cleared.
    /// </summary>
    private void UpdateTrackerFromBlock(ulong validatorId, int headIndex, ulong slot, AttestationData data)
    {
        var tracker = _attestationTrackers.GetValueOrDefault(validatorId);

        var knownSlot = tracker.LatestKnown?.Slot ?? 0UL;
        if (slot > knownSlot)
        {
            tracker.LatestKnown = new ProtoAttestation { Index = headIndex, Slot = slot, Data = data };
        }

        // Keep latestNew aligned with latestKnown when the block attestation is newer
        var newSlot = tracker.LatestNew?.Slot ?? 0UL;
        if (slot > newSlot)
        {
            tracker.LatestNew = tracker.LatestKnown;
        }

        _attestationTrackers[validatorId] = tracker;
    }

    /// <summary>
    /// Update attestation tracker from gossip attestation (is_from_block=false).
    /// Sets latestNew only.
    /// </summary>
    private void UpdateTrackerFromGossip(ulong validatorId, int headIndex, ulong slot, AttestationData data)
    {
        var tracker = _attestationTrackers.GetValueOrDefault(validatorId);

        var newSlot = tracker.LatestNew?.Slot ?? 0UL;
        if (slot > newSlot)
        {
            tracker.LatestNew = new ProtoAttestation { Index = headIndex, Slot = slot, Data = data };
        }

        _attestationTrackers[validatorId] = tracker;
    }

    private void UpdateStoreCheckpoints(Checkpoint canonicalJustified, Checkpoint canonicalFinalized)
    {
        // Use >= to match leanSpec's max(..., key=slot) semantics.
        // In 3SF-mini, different forks can produce different justified roots at the same slot.
        // Using > would prevent switching to the correct justified root on the canonical fork.
        if (canonicalJustified.Slot.Value >= _latestJustified.Slot.Value)
        {
            _latestJustified = canonicalJustified;

            // 2/3 supermajority already formed on this root — it is the canonically
            // justified block. If we don't have it locally (e.g. the attestations
            // that justified it referenced a minority fork we never saw, included
            // in a canonical block we did process), trigger a BlocksByRoot fetch
            // so the block arrives and subsequent local attestations referencing
            // it as source no longer fail with "Unknown source root".
            if (!_protoArray.ContainsBlock(canonicalJustified.Root))
            {
                _logger.LogWarning(
                    "Justified advanced to unknown block, queueing backfill. Slot: {Slot}, Root: {Root}",
                    canonicalJustified.Slot.Value,
                    Convert.ToHexString(canonicalJustified.Root.AsSpan()));
                _requestBlockByRoot?.Invoke(canonicalJustified.Root);
            }
        }

        if (canonicalFinalized.Slot.Value <= _latestFinalized.Slot.Value)
        {
            return;
        }

        _latestFinalized = canonicalFinalized;

        // Prune proto-array lazily, lighthouse-style: only fire when the
        // finalized node has moved far enough from the array head to make
        // the Vec rebuild + attestation-tracker remap worthwhile. The delay
        // doubles as a grace window for in-flight attestations whose
        // source/target/head points at a block near the finalization
        // boundary — leaving them in proto-array lets TryValidateAttestationData
        // pass its existence checks instead of failing with Unknown*Root.
        //
        // Only the block-tree prune is gated; attestation-pool cleanup
        // (PruneFinalizedAttestationData) still tracks latest_finalized,
        // because stale attestations are useless regardless of whether
        // their target block is physically present.
        var finalizedIdx = _protoArray.GetIndex(_latestFinalized.Root);
        if (finalizedIdx is { } idx && idx >= _pruneNodeThreshold)
        {
            var indexMapping = _protoArray.Prune(_latestFinalized.Root);
            if (indexMapping.Count > 0)
                RemapAttestationTrackerIndices(indexMapping);
        }

        PruneFinalizedAttestationData();
    }

    /// <summary>
    /// Remap AttestationTracker indices after proto-array pruning.
    /// Null out indices that were pruned.
    /// </summary>
    private void RemapAttestationTrackerIndices(Dictionary<int, int> oldToNew)
    {
        var keys = new List<ulong>(_attestationTrackers.Keys);
        foreach (var vid in keys)
        {
            var tracker = _attestationTrackers[vid];
            var changed = false;

            if (tracker.AppliedIndex.HasValue)
            {
                if (oldToNew.TryGetValue(tracker.AppliedIndex.Value, out var newIdx))
                {
                    tracker.AppliedIndex = newIdx;
                }
                else
                {
                    tracker.AppliedIndex = null;
                }
                changed = true;
            }

            if (tracker.LatestKnown.HasValue)
            {
                var known = tracker.LatestKnown.Value;
                if (oldToNew.TryGetValue(known.Index, out var newIdx))
                {
                    known.Index = newIdx;
                    tracker.LatestKnown = known;
                }
                else
                {
                    tracker.LatestKnown = null;
                }
                changed = true;
            }

            if (tracker.LatestNew.HasValue)
            {
                var n = tracker.LatestNew.Value;
                if (oldToNew.TryGetValue(n.Index, out var newIdx))
                {
                    n.Index = newIdx;
                    tracker.LatestNew = n;
                }
                else
                {
                    tracker.LatestNew = null;
                }
                changed = true;
            }

            if (changed)
                _attestationTrackers[vid] = tracker;
        }
    }

    private void PruneFinalizedAttestationData()
    {
        PruneAttestationDataOlderThan(_latestFinalized.Slot.Value);
    }

    private void PruneAttestationDataOlderThan(ulong cutoffSlot)
    {
        var staleKeys = new List<string>();

        foreach (var (key, (data, _)) in _gossipSignatures)
            if (data.Target.Slot.Value <= cutoffSlot) staleKeys.Add(key);
        foreach (var key in staleKeys) _gossipSignatures.Remove(key);

        staleKeys.Clear();
        foreach (var (key, (data, _)) in _newAggregatedPayloads)
            if (data.Target.Slot.Value <= cutoffSlot) staleKeys.Add(key);
        foreach (var key in staleKeys) _newAggregatedPayloads.Remove(key);

        staleKeys.Clear();
        foreach (var (key, (data, _)) in _knownAggregatedPayloads)
            if (data.Target.Slot.Value <= cutoffSlot) staleKeys.Add(key);
        foreach (var key in staleKeys) _knownAggregatedPayloads.Remove(key);

        LeanMetrics.SetGossipSignatures(_gossipSignatures.Values.Sum(v => v.Entries.Count));
        LeanMetrics.SetLatestKnownAggregatedPayloads(
            _knownAggregatedPayloads.Values.Sum(v => v.Proofs.Count));
        LeanMetrics.SetLatestNewAggregatedPayloads(
            _newAggregatedPayloads.Values.Sum(v => v.Proofs.Count));

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var payloadBytes = _knownAggregatedPayloads.Values
                .Sum(entry => entry.Proofs.Sum(p => (long)p.ProofData.Length));
            _logger.LogDebug(
                "PruneAttestationData. CutoffSlot: {CutoffSlot}, RemainingSignatures: {RemainingSigs}, RemainingPayloads: {RemainingPayloads}, PayloadBytes: {PayloadBytes}",
                cutoffSlot,
                _gossipSignatures.Values.Sum(v => v.Entries.Count),
                _knownAggregatedPayloads.Values.Sum(v => v.Proofs.Count),
                payloadBytes);
        }
    }

    /// <summary>
    /// Computes attestation target checkpoint matching leanSpec get_attestation_target().
    /// </summary>
    public Checkpoint ComputeTargetCheckpoint()
    {
        var targetRoot = _headRoot;
        var targetSlot = _headSlot;

        var safeTargetSlot = _protoArray.GetSlot(_safeTarget) ?? 0UL;
        var finalizedSlot = _latestFinalized.Slot.Value;

        var initialHeadSlot = targetSlot;

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

        // Honest validators emit votes only after their slot has begun.
        // Allow at most GossipDisparityIntervals (~one interval, ~800 ms) of
        // clock skew. The bound is in intervals, not slots: a whole-slot
        // margin would let an adversary pre-publish next-slot aggregates
        // ahead of any honest validator. (leanSpec #682.)
        var attestationStartInterval = data.Slot.Value * (ulong)IntervalsPerSlot;
        if (attestationStartInterval > _currentTimeIntervals + GossipDisparityIntervals)
        {
            reason = $"Attestation slot {data.Slot.Value} is too far in the future (start interval {attestationStartInterval}, store time {_currentTimeIntervals}).";
            return false;
        }

        if (!_protoArray.ContainsBlock(data.Source.Root))
        {
            _requestBlockByRoot?.Invoke(data.Source.Root);
            reason = $"Unknown source root {Convert.ToHexString(data.Source.Root.AsSpan())}.";
            return false;
        }

        if (!_protoArray.ContainsBlock(data.Target.Root))
        {
            _requestBlockByRoot?.Invoke(data.Target.Root);
            reason = $"Unknown target root {Convert.ToHexString(data.Target.Root.AsSpan())}.";
            return false;
        }

        if (!_protoArray.ContainsBlock(data.Head.Root))
        {
            _requestBlockByRoot?.Invoke(data.Head.Root);
            reason = $"Unknown head root {Convert.ToHexString(data.Head.Root.AsSpan())}.";
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
