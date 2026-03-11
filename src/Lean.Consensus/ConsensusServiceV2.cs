using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using Lean.Metrics;
using Lean.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lean.Consensus;

public sealed class ConsensusServiceV2 : IConsensusService, ITickTarget, IBlockProcessor
{
    private readonly ProtoArrayForkChoiceStore _store;
    private readonly ChainStateTransition _chainStateTransition;
    private readonly ChainStateCache _chainStateCache;
    private readonly SlotClock _clock;
    private readonly ConsensusConfig _config;
    private readonly ChainService _chainService;
    private readonly ISyncService? _syncService;
    private readonly INetworkService? _networkService;
    private readonly SignedBlockWithAttestationGossipDecoder _blockDecoder;
    private readonly SignedAttestationGossipDecoder _attestationDecoder;
    private readonly SignedAggregatedAttestationGossipDecoder _aggregatedAttestationDecoder;
    private readonly IStatusRpcRouter? _statusRpcRouter;
    private readonly IBlocksByRootRpcRouter? _blocksByRootRpcRouter;
    private readonly IBlockByRootStore _blockStore;
    private readonly IConsensusStateStore? _stateStore;
    private readonly IGossipTopicProvider _gossipTopics;
    private readonly ILogger<ConsensusServiceV2> _logger;
    private readonly string[] _attestationSubnetTopics;
    private IIntervalDutyTarget? _dutyTarget;
    private object _storeLock => _store.SyncRoot;

    private readonly Channel<ConsensusInboxMessage> _inbox = Channel.CreateUnbounded<ConsensusInboxMessage>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private volatile ConsensusSnapshot _snapshot = null!;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Task? _inboxTask;
    private int _started;
    private int _statusProbeInFlight;
    private long _lastPrunedFinalizedSlot;

    public ConsensusServiceV2(
        ProtoArrayForkChoiceStore store,
        SlotClock clock,
        ConsensusConfig config,
        ISyncService? syncService = null,
        INetworkService? networkService = null,
        SignedBlockWithAttestationGossipDecoder? blockDecoder = null,
        SignedAttestationGossipDecoder? attestationDecoder = null,
        SignedAggregatedAttestationGossipDecoder? aggregatedAttestationDecoder = null,
        IStatusRpcRouter? statusRpcRouter = null,
        IBlocksByRootRpcRouter? blocksByRootRpcRouter = null,
        IBlockByRootStore? blockStore = null,
        IGossipTopicProvider? gossipTopics = null,
        ChainStateCache? chainStateCache = null,
        IConsensusStateStore? stateStore = null,
        ILogger<ConsensusServiceV2>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(config);

        _store = store;
        _clock = clock;
        _config = config;
        _syncService = syncService;
        _networkService = networkService;
        _blockDecoder = blockDecoder ?? new SignedBlockWithAttestationGossipDecoder();
        _attestationDecoder = attestationDecoder ?? new SignedAttestationGossipDecoder();
        _aggregatedAttestationDecoder = aggregatedAttestationDecoder ?? new SignedAggregatedAttestationGossipDecoder();
        _statusRpcRouter = statusRpcRouter;
        _blocksByRootRpcRouter = blocksByRootRpcRouter;
        _blockStore = blockStore ?? NoOpBlockByRootStore.Instance;
        _stateStore = stateStore;
        _gossipTopics = gossipTopics ?? new GossipTopicProvider(GossipTopics.DefaultNetwork);
        _logger = logger ?? NullLogger<ConsensusServiceV2>.Instance;
        _chainStateTransition = new ChainStateTransition(_config);
        _chainStateCache = chainStateCache ?? new ChainStateCache();

        _attestationSubnetTopics = BuildAttestationSubnetTopics(
            _gossipTopics,
            _config.AttestationCommitteeCount,
            _config.IsAggregator,
            _config.LocalValidatorId);
        _chainService = new ChainService(_clock, this, ProtoArrayForkChoiceStore.IntervalsPerSlot);

        State? initialState = null;
        if (_stateStore is not null && _store.FinalizedSlot > 0)
        {
            _stateStore.TryLoad(out _, out initialState);
        }

        if (initialState is null)
        {
            initialState = _chainStateTransition.CreateGenesisState(Math.Max(1UL, _config.InitialValidatorCount));
        }

        _chainStateCache.Set(ChainStateCache.RootKey(_store.HeadRoot), initialState);
        RefreshSnapshot();
    }

    public void SetDutyTarget(IIntervalDutyTarget? target) => _dutyTarget = target;

    public ulong CurrentSlot => _clock.CurrentSlot;
    public ulong HeadSlot => _snapshot.HeadSlot;
    public ulong JustifiedSlot => _snapshot.JustifiedSlot;
    public ulong FinalizedSlot => _snapshot.FinalizedSlot;

    // Unknown roots should suppress duties only while actively syncing.
    public bool HasUnknownBlockRootsInFlight => _syncService is not null && _syncService.State == SyncState.Syncing;

    public byte[] HeadRoot => _snapshot.HeadRoot.AsSpan().ToArray();

    public (byte[] ParentRoot, AttestationData BaseAttestationData) GetProposalContext(ulong slot)
    {
        lock (_storeLock)
        {
            // Align with ethlambda/leanSpec get_proposal_head:
            // proposal path must force pending attestation acceptance before selecting parent/head.
            _store.PrepareForProposal(slot);
            RefreshSnapshot();

            var parentRoot = _store.HeadRoot.AsSpan().ToArray();
            var target = _store.ComputeTargetCheckpoint();
            var baseAttestationData = new AttestationData(
                new Slot(slot),
                new Checkpoint(_store.HeadRoot, new Slot(_store.HeadSlot)),
                target,
                new Checkpoint(_store.JustifiedRoot, new Slot(_store.JustifiedSlot)));

            return (parentRoot, baseAttestationData);
        }
    }

    public AttestationData CreateAttestationData(ulong slot)
    {
        lock (_storeLock)
        {
            var target = _store.ComputeTargetCheckpoint();
            return new AttestationData(
                new Slot(slot),
                new Checkpoint(_store.HeadRoot, new Slot(_store.HeadSlot)),
                target,
                new Checkpoint(_store.JustifiedRoot, new Slot(_store.JustifiedSlot)));
        }
    }

    // IBlockProcessor
    public bool IsBlockKnown(Bytes32 root)
    {
        lock (_storeLock) { return _store.ContainsBlock(root); }
    }

    public ForkChoiceApplyResult ProcessBlock(SignedBlockWithAttestation signedBlock)
    {
        ArgumentNullException.ThrowIfNull(signedBlock);

        var block = signedBlock.Message.Block;
        var blockRoot = new Bytes32(block.HashTreeRoot());
        var parentRoot = block.ParentRoot;

        // Phase 1: Read parent state under lock (fast).
        State? parentState;
        lock (_storeLock)
        {
            if (!_store.ContainsBlock(parentRoot))
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.UnknownParent,
                    $"Unknown parent root {parentRoot}.",
                    _store.HeadSlot,
                    _store.HeadRoot);
            }

            if (!_chainStateCache.TryGet(ChainStateCache.RootKey(parentRoot), out parentState))
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.StateTransitionFailed,
                    $"Missing chain state snapshot for parent root {parentRoot}.",
                    _store.HeadSlot,
                    _store.HeadRoot);
            }
        }

        // Phase 2: State transition WITHOUT lock (expensive, ~50-100ms).
        if (!_chainStateTransition.TryComputeStateRoot(
                parentState,
                block,
                out var computedStateRoot,
                out var postState,
                out var reason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.StateTransitionFailed,
                reason,
                _store.HeadSlot,
                _store.HeadRoot);
        }

        if (!computedStateRoot.Equals(block.StateRoot))
        {
            _logger.LogWarning(
                "State root mismatch. Slot={Slot}, BlockStateRoot={BlockStateRoot}, ComputedStateRoot={ComputedStateRoot}",
                block.Slot.Value,
                Convert.ToHexString(block.StateRoot.AsSpan()),
                Convert.ToHexString(computedStateRoot.AsSpan()));
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.StateTransitionFailed,
                $"State root mismatch at slot {block.Slot.Value}.",
                _store.HeadSlot,
                _store.HeadRoot);
        }

        _logger.LogDebug(
            "ProcessBlock: Slot={Slot}, BlockAttestationCount={AttCount}, PostJustifiedSlot={PostJSlot}, PostFinalizedSlot={PostFSlot}, ValidatorCount={VCount}",
            block.Slot.Value,
            block.Body.Attestations.Count,
            postState.LatestJustified.Slot.Value,
            postState.LatestFinalized.Slot.Value,
            postState.Validators.Count);

        // Phase 3: Apply to store under lock (fast).
        ForkChoiceApplyResult result;
        lock (_storeLock)
        {
            if (!_store.ContainsBlock(parentRoot))
            {
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.UnknownParent,
                    $"Parent pruned during state transition for {parentRoot}.",
                    _store.HeadSlot,
                    _store.HeadRoot);
            }

            result = _store.OnBlock(signedBlock, postState.LatestJustified, postState.LatestFinalized, (ulong)postState.Validators.Count);
            if (result.Accepted)
            {
                _chainStateCache.Set(ChainStateCache.RootKey(blockRoot), postState);
                RefreshSnapshot();
                _blockStore.Save(blockRoot, SszEncoding.Encode(signedBlock));
                _logger.LogInformation(
                    "V2 ProcessBlock accepted. Slot: {Slot}, BlockRoot: {BlockRoot}, ParentRoot: {ParentRoot}, ResultHeadSlot: {HeadSlot}, HeadChanged: {HeadChanged}",
                    block.Slot.Value,
                    blockRoot,
                    parentRoot,
                    result.HeadSlot,
                    result.HeadChanged);
            }
        }

        return result;
    }

    public bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out string reason)
    {
        return TryComputeBlockStateRoot(candidateBlock, out stateRoot, out _, out reason);
    }

    public bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out Checkpoint postJustified, out string reason)
    {
        ArgumentNullException.ThrowIfNull(candidateBlock);

        // Read parent state under lock (fast).
        State? parentState;
        lock (_storeLock)
        {
            if (!_chainStateCache.TryGet(ChainStateCache.RootKey(candidateBlock.ParentRoot), out parentState))
            {
                stateRoot = Bytes32.Zero();
                postJustified = new Checkpoint(Bytes32.Zero(), new Slot(0));
                reason = $"Missing chain state snapshot for parent root {candidateBlock.ParentRoot}.";
                return false;
            }
        }

        // State transition WITHOUT lock (expensive).
        var success = _chainStateTransition.TryComputeStateRoot(
            parentState,
            candidateBlock,
            out stateRoot,
            out var postState,
            out reason);

        postJustified = success ? postState.LatestJustified : new Checkpoint(Bytes32.Zero(), new Slot(0));
        return success;
    }

    public bool TryApplyLocalBlock(SignedBlockWithAttestation signedBlock, out string reason)
    {
        var result = ProcessBlock(signedBlock);
        reason = result.Accepted ? string.Empty : result.Reason;
        return result.Accepted;
    }

    public bool TryApplyLocalAttestation(SignedAttestation signedAttestation, out string reason)
    {
        lock (_storeLock) { return _store.TryOnAttestation(signedAttestation, _config.IsAggregator, out reason); }
    }

    public bool TryApplyLocalAggregatedAttestation(SignedAggregatedAttestation signed, out string reason)
    {
        lock (_storeLock) { return _store.TryOnGossipAggregatedAttestation(signed, out reason); }
    }

    public (IReadOnlyList<Attestation> Attestations, IReadOnlyDictionary<string, List<AggregatedSignatureProof>> PayloadPool)
        GetAllAvailableAttestationsForBlock(ulong slot)
    {
        lock (_storeLock)
        {
            var attestations = _store.ExtractAllAttestationsFromKnownPayloads(slot);
            var pool = _store.GetKnownPayloadPool();
            return (attestations, pool);
        }
    }

    public IReadOnlySet<Bytes32> GetKnownBlockRoots()
    {
        lock (_storeLock) { return _store.GetAllBlockRoots(); }
    }

    public (IReadOnlyList<AggregatedAttestation> Attestations, IReadOnlyList<AggregatedSignatureProof> Proofs) GetKnownAggregatedPayloadsForBlock(ulong slot, Checkpoint requiredSource)
    {
        lock (_storeLock)
        {
            // Keep tracker-first implementation, but match leanSpec's exact
            // SignatureKey(validator_id, data_root) gate semantics:
            // a validator contributes a block-building candidate only if there is
            // at least one known proof for this data_root that explicitly includes
            // that validator.
            var knownAttestations = _store.GetKnownAttestations();
            var attestations = new List<AggregatedAttestation>();
            var proofs = new List<AggregatedSignatureProof>();
            var pool = _store.GetKnownPayloadPool();
            foreach (var (validatorId, data) in knownAttestations)
            {
                if (data.Slot.Value >= slot)
                    continue;
                if (!data.Source.Equals(requiredSource))
                    continue;

                var key = Convert.ToHexString(data.HashTreeRoot());
                if (!pool.TryGetValue(key, out var poolProofs))
                {
                    continue;
                }

                foreach (var proof in poolProofs)
                {
                    if (!proof.Participants.TryToValidatorIndices(out var proofParticipants) ||
                        !proofParticipants.Contains(validatorId))
                    {
                        continue;
                    }

                    var bits = AggregationBits.FromValidatorIndices(new[] { validatorId });
                    attestations.Add(new AggregatedAttestation(bits, data));
                    proofs.Add(proof);
                }
            }

            return (attestations, proofs);
        }
    }

    public List<(AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)> CollectAttestationsForAggregation()
    {
        lock (_storeLock) { return _store.CollectAttestationsForAggregation(); }
    }

    public void OnTick(ulong slot, int intervalInSlot)
    {
        lock (_storeLock)
        {
            // leanSpec/ethlambda: interval 0 only promotes attestations when has_proposal.
            var hasProposal = intervalInSlot == 0 &&
                new Types.ValidatorIndex(_config.LocalValidatorId)
                    .IsProposerFor(slot, (int)Math.Max(1UL, _config.InitialValidatorCount));
            _store.TickInterval(slot, intervalInSlot, hasProposal);
            RefreshSnapshot();
        }

        var snap = _snapshot;
        LeanMetrics.SetCurrentSlot(slot);
        LeanMetrics.SetHeadSlot(snap.HeadSlot);
        LeanMetrics.SetJustifiedSlot(snap.JustifiedSlot);
        LeanMetrics.SetFinalizedSlot(snap.FinalizedSlot);

        if (snap.FinalizedSlot > (ulong)Interlocked.Read(ref _lastPrunedFinalizedSlot))
        {
            Interlocked.Exchange(ref _lastPrunedFinalizedSlot, (long)snap.FinalizedSlot);

            // Snapshot valid keys under store lock to avoid concurrent mutation.
            HashSet<string> validKeys;
            lock (_storeLock) { validKeys = _store.ProtoArray.GetAllKeys(); }
            _chainStateCache.PruneExcept(validKeys);

            if (_stateStore is not null)
            {
                try
                {
                    var headState = new ConsensusHeadState(
                        snap.HeadSlot,
                        snap.HeadRoot.AsSpan(),
                        snap.JustifiedSlot,
                        snap.JustifiedRoot.AsSpan(),
                        snap.FinalizedSlot,
                        snap.FinalizedRoot.AsSpan(),
                        _store.SafeTarget.AsSpan().Length == 32 ? snap.HeadSlot : 0UL,
                        _store.SafeTarget.AsSpan());

                    if (_chainStateCache.TryGet(ChainStateCache.RootKey(snap.HeadRoot), out var chainState))
                        _stateStore.Save(headState, chainState);
                    else
                        _stateStore.Save(headState);

                    _logger.LogInformation(
                        "Saved checkpoint state. HeadSlot={HeadSlot}, FinalizedSlot={FinalizedSlot}",
                        snap.HeadSlot, snap.FinalizedSlot);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save checkpoint state.");
                }
            }
        }

        if (intervalInSlot == ProtoArrayForkChoiceStore.IntervalsPerSlot - 1)
        {
            _logger.LogInformation(
                "Tick head election. Slot: {Slot}, HeadSlot: {HeadSlot}, JustifiedSlot: {JustifiedSlot}, FinalizedSlot: {FinalizedSlot}",
                slot, snap.HeadSlot, snap.JustifiedSlot, snap.FinalizedSlot);
        }

        if (intervalInSlot == 0 && _networkService is not null)
        {
            TriggerPeerStatusProbe();
        }

        if (intervalInSlot == 0 && _syncService is not null)
        {
            _syncService.TrySyncFromBestPeer();
        }

        if (_dutyTarget is not null)
        {
            _ = _dutyTarget.OnIntervalAsync(slot, intervalInSlot);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _blocksByRootRpcRouter?.SetHandler(ResolveBlockByRootAsync);
            if (_statusRpcRouter is not null)
            {
                _statusRpcRouter.SetHandler(ResolveStatusAsync);
                _statusRpcRouter.SetPeerStatusHandler(HandlePeerStatusAsync);
                _statusRpcRouter.SetPeerConnectedHandler(OnNetworkPeerConnected);
                _statusRpcRouter.SetPeerDisconnectedHandler(OnNetworkPeerDisconnected);
            }

            if (_networkService is not null && _config.EnableGossipProcessing)
            {
                await SubscribeTopicAsync(_gossipTopics.BlockTopic, _cts.Token);
                await SubscribeTopicAsync(_gossipTopics.AggregateTopic, _cts.Token);

                // All nodes subscribe to the attestation subnet so they can publish
                // their own individual attestation. Non-aggregators use storeSignature=false
                // (see ProcessGossipAttestationFromInbox) so they don't accumulate signatures.
                foreach (var subnetTopic in _attestationSubnetTopics)
                {
                    await SubscribeTopicAsync(subnetTopic, _cts.Token);
                }
            }

            // Use LongRunning so the gossip consumer gets its own thread
            // instead of occupying a ThreadPool worker for the node's lifetime.
            _inboxTask = Task.Factory.StartNew(
                () => ConsumeInboxAsync(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            _runTask = _chainService.RunAsync(_cts.Token);
            if (_syncService is not null)
                await _syncService.StartAsync(_cts.Token);

            // Don't probe peer status immediately at startup — remote peers may
            // not have registered their status handler yet. The periodic probe
            // at OnTick(intervalInSlot == 0) will handle it within one slot.
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            ClearRpcHandlers();

            // Cancel and await any tasks that may have started before disposing CTS.
            try { _cts?.Cancel(); } catch { /* best-effort */ }
            _inbox.Writer.TryComplete();

            try { if (_inboxTask is not null) await _inboxTask; }
            catch { /* swallow cancellation/errors from partial startup */ }

            try { if (_runTask is not null) await _runTask; }
            catch { /* swallow cancellation/errors from partial startup */ }

            _inboxTask = null;
            _runTask = null;
            _cts?.Dispose();
            _cts = null;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;

        ClearRpcHandlers();

        if (_cts is null)
            return;

        await _cts.CancelAsync();
        _inbox.Writer.TryComplete();

        if (_syncService is not null)
        {
            try
            {
                await _syncService.StopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown path.
            }
        }

        if (_inboxTask is not null)
        {
            try
            {
                await _inboxTask;
            }
            catch (OperationCanceledException)
            {
                // Shutdown path.
            }
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
                // Shutdown path.
            }
        }

        _cts.Dispose();
        _cts = null;
        _runTask = null;
        _inboxTask = null;
        Interlocked.Exchange(ref _statusProbeInFlight, 0);
    }

    private void RefreshSnapshot()
    {
        _snapshot = new ConsensusSnapshot(
            _store.HeadRoot,
            _store.HeadSlot,
            _store.JustifiedRoot,
            _store.JustifiedSlot,
            _store.FinalizedRoot,
            _store.FinalizedSlot);
    }

    private async Task ConsumeInboxAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _inbox.Reader.ReadAllAsync(ct))
            {
                try
                {
                    switch (msg)
                    {
                        case GossipBlockMessage block:
                            ProcessGossipBlockFromInbox(block);
                            break;
                        case GossipAttestationMessage att:
                            ProcessGossipAttestationFromInbox(att);
                            break;
                        case GossipAggregatedAttestationMessage agg:
                            ProcessGossipAggregatedAttestationFromInbox(agg);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing inbox message: {Type}", msg.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
    }

    private void ProcessGossipBlockFromInbox(GossipBlockMessage msg)
    {
        var signedBlock = msg.Block;
        var block = signedBlock.Message.Block;
        // Compute blockRoot from the block itself — must match the key used by
        // the proto-array and chain state cache (not the gossip decoder's MessageRoot).
        var blockRoot = new Bytes32(block.HashTreeRoot());
        var parentRoot = block.ParentRoot;

        // Phase 1: Read parent state under lock (fast).
        State? parentState;
        lock (_storeLock)
        {
            if (!_store.ContainsBlock(parentRoot))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "HandleGossipBlock: slot={Slot}, blockRoot={Root}, unknown parent {Parent}",
                        block.Slot.Value, Convert.ToHexString(blockRoot.AsSpan())[..8],
                        Convert.ToHexString(parentRoot.AsSpan())[..8]);
                }
                if (_syncService is not null)
                    _ = _syncService.OnGossipBlockAsync(signedBlock, blockRoot, peerId: null);
                return;
            }

            if (!_chainStateCache.TryGet(ChainStateCache.RootKey(parentRoot), out parentState))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "HandleGossipBlock: slot={Slot}, blockRoot={Root}, missing parent state for {Parent}",
                        block.Slot.Value, Convert.ToHexString(blockRoot.AsSpan())[..8],
                        Convert.ToHexString(parentRoot.AsSpan())[..8]);
                }
                return;
            }
        }

        // Phase 2: State transition WITHOUT lock (expensive, ~50-100ms).
        // Tick and validator threads can proceed while this runs.
        if (!_chainStateTransition.TryComputeStateRoot(
                parentState,
                block,
                out var computedStateRoot,
                out var postState,
                out var reason))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "HandleGossipBlock: slot={Slot}, blockRoot={Root}, accepted=False, reason={Reason}",
                    block.Slot.Value, Convert.ToHexString(blockRoot.AsSpan())[..8], reason);
            }
            return;
        }

        if (!computedStateRoot.Equals(block.StateRoot))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "HandleGossipBlock state root mismatch. Slot={Slot}, BlockRoot={BlockRoot}",
                    block.Slot.Value, Convert.ToHexString(blockRoot.AsSpan())[..8]);
            }
            return;
        }

        // Phase 3: Apply to store under lock (fast).
        ForkChoiceApplyResult result;
        lock (_storeLock)
        {
            // Re-validate parent is still known (could have been pruned during phase 2).
            if (!_store.ContainsBlock(parentRoot))
                return;

            result = _store.OnBlock(signedBlock, postState.LatestJustified, postState.LatestFinalized, (ulong)postState.Validators.Count);
            if (result.Accepted)
            {
                _chainStateCache.Set(ChainStateCache.RootKey(blockRoot), postState);
                RefreshSnapshot();
                _blockStore.Save(blockRoot, SszEncoding.Encode(signedBlock));
            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "HandleGossipBlock: slot={Slot}, blockRoot={Root}, accepted={Accepted}, reason={Reason}",
                block.Slot.Value,
                Convert.ToHexString(blockRoot.AsSpan())[..8],
                result.Accepted,
                result.Reason);
        }

        if (result.Accepted && _syncService is not null)
        {
            _syncService.CascadeAcceptedBlock(blockRoot);
        }
    }

    private void ProcessGossipAttestationFromInbox(GossipAttestationMessage msg)
    {
        if (_syncService is not null)
        {
            _ = _syncService.OnGossipAttestationAsync(msg.Attestation);
            return;
        }

        lock (_storeLock) { _ = _store.TryOnAttestation(msg.Attestation, _config.IsAggregator, out _); }
    }

    private void ProcessGossipAggregatedAttestationFromInbox(GossipAggregatedAttestationMessage msg)
    {
        bool accepted;
        string reason;
        lock (_storeLock) { accepted = _store.TryOnGossipAggregatedAttestation(msg.Attestation, out reason); }

        if (!accepted)
        {
            _logger.LogDebug(
                "Dropped gossip aggregated attestation in V2 path. Reason: {Reason}",
                reason);
        }
    }

    private async Task SubscribeTopicAsync(string topic, CancellationToken cancellationToken)
    {
        if (_networkService is null || string.IsNullOrWhiteSpace(topic))
            return;

        await _networkService.SubscribeAsync(topic, payload => HandleGossipMessage(topic, payload), cancellationToken);
    }

    private void HandleGossipMessage(string topic, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.Equals(topic, _gossipTopics.BlockTopic, StringComparison.Ordinal))
        {
            HandleGossipBlock(payload);
            return;
        }

        if (string.Equals(topic, _gossipTopics.AggregateTopic, StringComparison.Ordinal))
        {
            HandleGossipAggregatedAttestation(payload);
            return;
        }

        if (IsAttestationSubnetTopic(topic))
        {
            HandleGossipAttestation(payload);
        }
    }

    private void HandleGossipBlock(byte[] payload)
    {
        _logger.LogDebug(
            "HandleGossipBlock: payloadLen={Len}",
            payload.Length);

        var decode = _blockDecoder.DecodeAndValidate(payload);
        if (!decode.IsSuccess || decode.SignedBlock is null)
        {
            _logger.LogWarning(
                "HandleGossipBlock decode failed: {Failure} - {Reason}, payloadLen={Len}",
                decode.Failure, decode.Reason, payload.Length);
            return;
        }

        var signedBlock = decode.SignedBlock;
        var blockRoot = decode.BlockMessageRoot ?? new Bytes32(signedBlock.Message.Block.HashTreeRoot());

        _inbox.Writer.TryWrite(new GossipBlockMessage(signedBlock, blockRoot));
    }

    private void HandleGossipAttestation(byte[] payload)
    {
        var decode = _attestationDecoder.DecodeAndValidate(payload);
        if (!decode.IsSuccess || decode.Attestation is null)
        {
            _logger.LogWarning(
                "Dropped gossip attestation. PayloadLen: {PayloadLen}, Failure: {Failure}, Reason: {Reason}",
                payload.Length, decode.Failure, decode.Reason);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Received gossip attestation. Validator: {ValidatorId}, Slot: {Slot}, HeadRoot: {HeadRoot}, TargetRoot: {TargetRoot}, SourceRoot: {SourceRoot}",
                decode.Attestation.ValidatorId, decode.Attestation.Message.Slot.Value,
                Convert.ToHexString(decode.Attestation.Message.Head.Root.AsSpan())[..8],
                Convert.ToHexString(decode.Attestation.Message.Target.Root.AsSpan())[..8],
                Convert.ToHexString(decode.Attestation.Message.Source.Root.AsSpan())[..8]);
        }

        _inbox.Writer.TryWrite(new GossipAttestationMessage(decode.Attestation));
    }

    private void HandleGossipAggregatedAttestation(byte[] payload)
    {
        var decode = _aggregatedAttestationDecoder.DecodeAndValidate(payload);
        if (!decode.IsSuccess || decode.Attestation is null)
            return;

        _inbox.Writer.TryWrite(new GossipAggregatedAttestationMessage(decode.Attestation));
    }

    private bool IsAttestationSubnetTopic(string topic)
    {
        for (var i = 0; i < _attestationSubnetTopics.Length; i++)
        {
            if (string.Equals(_attestationSubnetTopics[i], topic, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void TriggerPeerStatusProbe()
    {
        if (_networkService is null)
            return;

        if (Interlocked.CompareExchange(ref _statusProbeInFlight, 1, 0) != 0)
            return;

        _ = ProbePeerStatusesAsync();
    }

    private async Task ProbePeerStatusesAsync()
    {
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _networkService!.ProbePeerStatusesAsync(probeCts.Token);
        }
        catch
        {
            // Best-effort probe path.
        }
        finally
        {
            Interlocked.Exchange(ref _statusProbeInFlight, 0);
        }
    }

    private ValueTask<byte[]?> ResolveBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (blockRoot.Length != SszEncoding.Bytes32Length)
            return ValueTask.FromResult<byte[]?>(null);

        var root = new Bytes32(blockRoot.ToArray());
        return ValueTask.FromResult(_blockStore.TryLoad(root, out var payload) ? payload : null);
    }

    private ValueTask<LeanStatusMessage> ResolveStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snap = _snapshot;
        var status = new LeanStatusMessage(
            snap.FinalizedRoot.AsSpan(),
            snap.FinalizedSlot,
            snap.HeadRoot.AsSpan(),
            snap.HeadSlot);

        return ValueTask.FromResult(status);
    }

    private ValueTask HandlePeerStatusAsync(LeanStatusMessage status, string? peerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_syncService is null || string.IsNullOrWhiteSpace(peerKey))
            return ValueTask.CompletedTask;

        var normalizedPeer = peerKey.Trim();
        var headRoot = new Bytes32(status.HeadRoot);
        return new ValueTask(_syncService.OnPeerStatusAsync(normalizedPeer, status.HeadSlot, status.FinalizedSlot, headRoot));
    }

    private void OnNetworkPeerConnected(string peerKey)
    {
        _syncService?.OnPeerConnected(peerKey);
    }

    private void OnNetworkPeerDisconnected(string peerKey)
    {
        _syncService?.OnPeerDisconnected(peerKey);
    }

    private void ClearRpcHandlers()
    {
        _blocksByRootRpcRouter?.SetHandler(null);
        _statusRpcRouter?.SetHandler(null);
        _statusRpcRouter?.SetPeerStatusHandler(null);
        _statusRpcRouter?.SetPeerConnectedHandler(null);
        _statusRpcRouter?.SetPeerDisconnectedHandler(null);
    }

    private static string[] BuildAttestationSubnetTopics(IGossipTopicProvider gossipTopics, int attestationCommitteeCount, bool isAggregator, ulong localValidatorId)
    {
        // ethlambda/leanSpec: each validator subscribes only to its own subnet
        // (validator_id % committee_count). Non-validators subscribe to subnet 0.
        var committeeCount = Math.Max(1, attestationCommitteeCount);
        var subnetId = (int)(localValidatorId % (ulong)committeeCount);
        return [gossipTopics.AttestationSubnetTopic(subnetId)];
    }

    private abstract record ConsensusInboxMessage;
    private sealed record GossipBlockMessage(SignedBlockWithAttestation Block, Bytes32 BlockRoot) : ConsensusInboxMessage;
    private sealed record GossipAttestationMessage(SignedAttestation Attestation) : ConsensusInboxMessage;
    private sealed record GossipAggregatedAttestationMessage(SignedAggregatedAttestation Attestation) : ConsensusInboxMessage;

    private sealed record ConsensusSnapshot(
        Bytes32 HeadRoot,
        ulong HeadSlot,
        Bytes32 JustifiedRoot,
        ulong JustifiedSlot,
        Bytes32 FinalizedRoot,
        ulong FinalizedSlot);

    private sealed class NoOpBlockByRootStore : IBlockByRootStore
    {
        public static readonly NoOpBlockByRootStore Instance = new();

        public void Save(Bytes32 blockRoot, ReadOnlySpan<byte> payload)
        {
        }

        public bool TryLoad(Bytes32 blockRoot, [NotNullWhen(true)] out byte[]? payload)
        {
            payload = null;
            return false;
        }
    }
}
