using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using Lean.Consensus.Types;
using Lean.Metrics;
using Lean.Network;
using Microsoft.Extensions.Logging;

namespace Lean.Consensus;

public sealed class ConsensusService : IConsensusService
{
    private const int MaxPendingGossipAttestations = 4096;
    private const ulong PendingAttestationRetentionSlots = 64;
    private const int IntervalsPerSlot = ForkChoiceStore.IntervalsPerSlot;

    private readonly ILogger<ConsensusService> _logger;
    private readonly INetworkService _networkService;
    private readonly SignedBlockWithAttestationGossipDecoder _gossipDecoder;
    private readonly SignedAttestationGossipDecoder _attestationDecoder;
    private readonly IConsensusStateStore _stateStore;
    private readonly IBlockByRootStore _blockStore;
    private readonly IBlocksByRootRpcRouter _blocksByRootRpcRouter;
    private readonly IStatusRpcRouter _statusRpcRouter;
    private readonly IGossipTopicProvider _gossipTopics;
    private readonly ForkChoiceStore _forkChoice;
    private readonly ConsensusConfig _config;
    private readonly object _stateLock = new();
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _slotLoopCts;
    private Task? _slotLoopTask;
    private int _started;
    private long _currentSlot;
    private long _currentInterval;
    private ulong _headSlot;
    private byte[] _headRoot = Array.Empty<byte>();
    private ConsensusHeadState? _lastPersistedState;
    private readonly Dictionary<string, List<SignedBlockWithAttestation>> _orphanBlocksByParent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _orphanBlockRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingGossipAttestation> _pendingGossipAttestations = new(StringComparer.Ordinal);
    private int _orphanBlockCount;
    private bool _statusStartupSyncTriggered;
    private ulong _lastStatusSyncTriggerPeerHeadSlot;
    private DateTimeOffset _lastStatusSyncAttemptUtc = DateTimeOffset.MinValue;
    private Bytes32 _lastStatusSyncTargetRoot = Bytes32.Zero();
    private string _lastStatusSyncTargetPeerKey = string.Empty;
    private int _missingHeadChainStatePersistWarningLogged;

    public ConsensusService(
        ILogger<ConsensusService> logger,
        INetworkService networkService,
        SignedBlockWithAttestationGossipDecoder gossipDecoder,
        SignedAttestationGossipDecoder attestationDecoder,
        IConsensusStateStore stateStore,
        ForkChoiceStore forkChoice,
        ConsensusConfig config,
        IBlockByRootStore? blockStore = null,
        IBlocksByRootRpcRouter? blocksByRootRpcRouter = null,
        IStatusRpcRouter? statusRpcRouter = null,
        IGossipTopicProvider? gossipTopics = null)
    {
        _logger = logger;
        _networkService = networkService;
        _gossipDecoder = gossipDecoder;
        _attestationDecoder = attestationDecoder;
        _stateStore = stateStore;
        _blockStore = blockStore ?? NoOpBlockByRootStore.Instance;
        _blocksByRootRpcRouter = blocksByRootRpcRouter ?? NoOpBlocksByRootRpcRouter.Instance;
        _statusRpcRouter = statusRpcRouter ?? NoOpStatusRpcRouter.Instance;
        _gossipTopics = gossipTopics ?? new GossipTopicProvider(GossipTopics.DefaultNetwork);
        _forkChoice = forkChoice;
        _config = config;
        _headSlot = _forkChoice.HeadSlot;
        _headRoot = _forkChoice.HeadRoot.AsSpan().ToArray();
        Interlocked.Exchange(ref _currentSlot, (long)_headSlot);
        Interlocked.Exchange(ref _currentInterval, (long)_headSlot * IntervalsPerSlot);
    }

    public ulong CurrentSlot => (ulong)Math.Max(0, Interlocked.Read(ref _currentSlot));

    public ulong HeadSlot
    {
        get
        {
            lock (_stateLock)
            {
                return _headSlot;
            }
        }
    }

    public ulong JustifiedSlot
    {
        get
        {
            lock (_stateLock)
            {
                return _forkChoice.LatestJustified.Slot.Value;
            }
        }
    }

    public ulong FinalizedSlot
    {
        get
        {
            lock (_stateLock)
            {
                return _forkChoice.LatestFinalized.Slot.Value;
            }
        }
    }

    public byte[] HeadRoot
    {
        get
        {
            lock (_stateLock)
            {
                return _headRoot.ToArray();
            }
        }
    }

    public byte[] GetProposalHeadRoot()
    {
        ConsensusHeadState? stateToPersist = null;
        State? headChainStateToPersist = null;
        byte[] headRoot;
        lock (_stateLock)
        {
            var proposalTickResult = _forkChoice.PrepareProposalHead();
            if (proposalTickResult.HeadChanged)
            {
                _headSlot = proposalTickResult.HeadSlot;
                _headRoot = proposalTickResult.HeadRoot.AsSpan().ToArray();
            }

            headRoot = _headRoot.ToArray();
            var snapshot = _forkChoice.CreateHeadState();
            if (ShouldPersistState(snapshot))
            {
                stateToPersist = snapshot;
                _forkChoice.TryGetHeadChainState(out headChainStateToPersist);
            }
        }

        LeanMetrics.SetHeadSlot(_forkChoice.HeadSlot);
        LeanMetrics.SetJustifiedSlot(_forkChoice.LatestJustified.Slot.Value);
        LeanMetrics.SetFinalizedSlot(_forkChoice.LatestFinalized.Slot.Value);
        LeanMetrics.SetSafeTargetSlot(_forkChoice.SafeTargetSlot);
        if (stateToPersist is not null)
        {
            PersistState(stateToPersist, headChainStateToPersist);
        }

        return headRoot;
    }

    public AttestationData CreateAttestationData(ulong slot)
    {
        lock (_stateLock)
        {
            return _forkChoice.CreateAttestationData(slot, _config.AttestationTargetLookbackSlots);
        }
    }

    public bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out string reason)
    {
        ArgumentNullException.ThrowIfNull(candidateBlock);
        lock (_stateLock)
        {
            return _forkChoice.TryComputeBlockStateRoot(candidateBlock, out stateRoot, out reason);
        }
    }

    public bool TryApplyLocalBlock(SignedBlockWithAttestation signedBlock, out string reason)
    {
        ArgumentNullException.ThrowIfNull(signedBlock);
        reason = string.Empty;

        if (Volatile.Read(ref _started) == 0)
        {
            reason = "Consensus service is not started.";
            return false;
        }

        var payload = SszEncoding.Encode(signedBlock);
        var outcome = TryApplyBlock(
            signedBlock,
            queueOnUnknownParent: false,
            payload.Length,
            payload,
            out var appliedRoot,
            out var missingParentRoot);

        if (outcome == BlockApplyOutcome.Accepted)
        {
            ProcessQueuedOrphans(appliedRoot);
            return true;
        }

        if (outcome == BlockApplyOutcome.QueuedUnknownParent)
        {
            reason = $"Unknown parent root {Convert.ToHexString(missingParentRoot.AsSpan())}.";
            return false;
        }

        var block = signedBlock.Message.Block;
        reason = $"Block rejected locally. slot={block.Slot.Value} proposer={block.ProposerIndex}";
        return false;
    }

    public bool TryApplyLocalAttestation(SignedAttestation signedAttestation, out string reason)
    {
        ArgumentNullException.ThrowIfNull(signedAttestation);
        reason = string.Empty;

        if (Volatile.Read(ref _started) == 0)
        {
            reason = "Consensus service is not started.";
            return false;
        }

        return TryApplyAttestation(signedAttestation, AttestationApplyMode.Local, out reason);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        Interlocked.Exchange(ref _missingHeadChainStatePersistWarningLogged, 0);

        lock (_stateLock)
        {
            _statusStartupSyncTriggered = false;
            _lastStatusSyncTriggerPeerHeadSlot = 0;
            _lastStatusSyncAttemptUtc = DateTimeOffset.MinValue;
            _lastStatusSyncTargetRoot = Bytes32.Zero();
            _lastStatusSyncTargetPeerKey = string.Empty;
            _pendingGossipAttestations.Clear();
        }

        _blocksByRootRpcRouter.SetHandler(ResolveBlockByRootAsync);
        _statusRpcRouter.SetHandler(ResolveStatusAsync);
        _statusRpcRouter.SetPeerStatusHandler(HandlePeerStatusAsync);

        if (_stateStore.TryLoad(out var persistedState, out var persistedHeadChainState))
        {
            lock (_stateLock)
            {
                _forkChoice.InitializeHead(persistedState, persistedHeadChainState);
                _headSlot = _forkChoice.HeadSlot;
                _headRoot = _forkChoice.HeadRoot.AsSpan().ToArray();
                Interlocked.Exchange(ref _currentSlot, (long)_headSlot);
                Interlocked.Exchange(ref _currentInterval, (long)_headSlot * IntervalsPerSlot);
                _lastPersistedState = _forkChoice.CreateHeadState();
            }

            LeanMetrics.SetHeadSlot(_forkChoice.HeadSlot);
            LeanMetrics.SetCurrentSlot(CurrentSlot);
            LeanMetrics.SetJustifiedSlot(_forkChoice.LatestJustified.Slot.Value);
            LeanMetrics.SetFinalizedSlot(_forkChoice.LatestFinalized.Slot.Value);
            LeanMetrics.SetSafeTargetSlot(_forkChoice.SafeTargetSlot);
            _logger.LogInformation(
                "Loaded persisted consensus head state. HeadSlot: {HeadSlot}, HeadRoot: {HeadRoot}",
                _forkChoice.HeadSlot,
                Convert.ToHexString(_forkChoice.HeadRoot.AsSpan()));
            if (persistedState.HeadSlot > 0 && persistedHeadChainState is null)
            {
                _logger.LogWarning(
                    "Persisted head state at slot {HeadSlot} was loaded without a chain-state snapshot. Signature verification may reject new blocks until a new head snapshot is persisted.",
                    persistedState.HeadSlot);
            }
        }

        if (_config.GenesisTimeUnix > 0)
        {
            var secondsPerInterval = Math.Max(1, Math.Max(1, _config.SecondsPerSlot) / IntervalsPerSlot);
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var elapsedSeconds = Math.Max(0L, nowUnix - (long)_config.GenesisTimeUnix);
            var chainInterval = elapsedSeconds / secondsPerInterval;
            var minimumInterval = Math.Max((long)_forkChoice.HeadSlot * IntervalsPerSlot, chainInterval);
            var minimumSlot = minimumInterval / IntervalsPerSlot;

            Interlocked.Exchange(ref _currentInterval, minimumInterval);
            Interlocked.Exchange(ref _currentSlot, minimumSlot);
            LeanMetrics.SetCurrentSlot(minimumSlot);
        }

        if (_config.EnableGossipProcessing)
        {
            await SubscribeTopicAsync(_gossipTopics.BlockTopic, cancellationToken);
            await SubscribeTopicAsync(_gossipTopics.AttestationTopic, cancellationToken);
            await SubscribeTopicAsync(_gossipTopics.AggregateTopic, cancellationToken);
        }

        try
        {
            await RunStartupStatusProbeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Initial proactive status probe timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Initial proactive status probe failed.");
        }

        lock (_lifecycleLock)
        {
            _slotLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _slotLoopTask = RunSlotTickerAsync(_slotLoopCts.Token);
        }

        _logger.LogInformation(
            "Consensus service started. SecondsPerSlot: {SecondsPerSlot}, GenesisTimeUnix: {GenesisTimeUnix}, GossipProcessing: {GossipProcessing}",
            _config.SecondsPerSlot,
            _config.GenesisTimeUnix,
            _config.EnableGossipProcessing);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        _blocksByRootRpcRouter.SetHandler(null);
        _statusRpcRouter.SetHandler(null);
        _statusRpcRouter.SetPeerStatusHandler(null);

        CancellationTokenSource? slotLoopCts;
        Task? slotLoopTask;
        lock (_lifecycleLock)
        {
            slotLoopCts = _slotLoopCts;
            slotLoopTask = _slotLoopTask;
            _slotLoopCts = null;
            _slotLoopTask = null;
        }

        if (slotLoopCts is not null)
        {
            slotLoopCts.Cancel();
            slotLoopCts.Dispose();
        }

        if (slotLoopTask is not null)
        {
            try
            {
                await slotLoopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown path.
            }
        }

        _logger.LogInformation("Consensus service stopped.");
    }

    private async Task RunSlotTickerAsync(CancellationToken cancellationToken)
    {
        if (_config.GenesisTimeUnix > 0)
        {
            await WaitForGenesisAsync(cancellationToken);
            await RunWallClockSlotTickerAsync(cancellationToken);
            return;
        }

        await RunFixedIntervalSlotTickerAsync(cancellationToken);
    }

    private async Task RunStartupStatusProbeAsync(CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(Math.Max(5, _config.SecondsPerSlot * 2), 5, 20);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeTask = _networkService.ProbePeerStatusesAsync(probeCts.Token);
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completedTask = await Task.WhenAny(probeTask, timeoutTask);
        if (completedTask == probeTask)
        {
            await probeTask;
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        probeCts.Cancel();

        _logger.LogInformation(
            "Initial proactive status probe exceeded startup timeout ({TimeoutSeconds}s); continuing startup.",
            timeout.TotalSeconds);

        _ = ObserveStartupStatusProbeAsync(probeTask);
    }

    private async Task ObserveStartupStatusProbeAsync(Task probeTask)
    {
        try
        {
            await probeTask;
        }
        catch (OperationCanceledException)
        {
            // Best-effort timeout path.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deferred startup status probe task failed.");
        }
    }

    private async Task RunFixedIntervalSlotTickerAsync(CancellationToken cancellationToken)
    {
        var secondsPerSlot = Math.Max(1, _config.SecondsPerSlot);
        var secondsPerInterval = Math.Max(0.25d, secondsPerSlot / (double)IntervalsPerSlot);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(secondsPerInterval));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var interval = (ulong)Interlocked.Increment(ref _currentInterval);
                ApplyIntervalTick(interval);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
    }

    private async Task RunWallClockSlotTickerAsync(CancellationToken cancellationToken)
    {
        var secondsPerInterval = Math.Max(1, Math.Max(1, _config.SecondsPerSlot) / IntervalsPerSlot);
        // Poll faster than one second so nodes that start at different wall-clock phases
        // converge on the same interval quickly and avoid persistent "future slot" gossip rejects.
        var pollMilliseconds = Math.Clamp(secondsPerInterval * 250, 100, 500);

        void AdvanceIntervalsToWallClock()
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var elapsedSeconds = Math.Max(0L, nowUnix - (long)_config.GenesisTimeUnix);
            var targetInterval = elapsedSeconds / secondsPerInterval;
            while (Interlocked.Read(ref _currentInterval) < targetInterval)
            {
                var interval = (ulong)Interlocked.Increment(ref _currentInterval);
                ApplyIntervalTick(interval);
            }
        }

        AdvanceIntervalsToWallClock();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(pollMilliseconds));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                AdvanceIntervalsToWallClock();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
    }

    private void ApplyIntervalTick(ulong interval)
    {
        var slot = interval / IntervalsPerSlot;
        Interlocked.Exchange(ref _currentSlot, (long)slot);
        LeanMetrics.SetCurrentSlot(slot);
        var intervalInSlot = (int)(interval % IntervalsPerSlot);
        ConsensusHeadState? stateToPersist = null;
        State? headChainStateToPersist = null;
        Bytes32 retryStatusSyncRoot = Bytes32.Zero();
        string? retryStatusSyncPeer = null;
        lock (_stateLock)
        {
            var tickResult = _forkChoice.OnIntervalTick(slot, intervalInSlot);
            if (tickResult.HeadChanged)
            {
                _headSlot = tickResult.HeadSlot;
                _headRoot = tickResult.HeadRoot.AsSpan().ToArray();
            }

            var snapshot = _forkChoice.CreateHeadState();
            if (ShouldPersistState(snapshot))
            {
                stateToPersist = snapshot;
                _forkChoice.TryGetHeadChainState(out headChainStateToPersist);
            }

            if (!_lastStatusSyncTargetRoot.Equals(Bytes32.Zero()) &&
                !_forkChoice.ContainsBlock(_lastStatusSyncTargetRoot))
            {
                var retryInterval = TimeSpan.FromSeconds(Math.Max(1, _config.SecondsPerSlot));
                if (DateTimeOffset.UtcNow - _lastStatusSyncAttemptUtc >= retryInterval)
                {
                    retryStatusSyncRoot = _lastStatusSyncTargetRoot;
                    retryStatusSyncPeer = string.IsNullOrWhiteSpace(_lastStatusSyncTargetPeerKey)
                        ? null
                        : _lastStatusSyncTargetPeerKey;
                    _lastStatusSyncAttemptUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        LeanMetrics.SetHeadSlot(_forkChoice.HeadSlot);
        LeanMetrics.SetJustifiedSlot(_forkChoice.LatestJustified.Slot.Value);
        LeanMetrics.SetFinalizedSlot(_forkChoice.LatestFinalized.Slot.Value);
        LeanMetrics.SetSafeTargetSlot(_forkChoice.SafeTargetSlot);
        if (stateToPersist is not null)
        {
            PersistState(stateToPersist, headChainStateToPersist);
        }

        if (!retryStatusSyncRoot.Equals(Bytes32.Zero()))
        {
            var pendingRoot = retryStatusSyncRoot;
            var preferredPeerKey = retryStatusSyncPeer;
            _logger.LogInformation(
                "Retrying status-driven block sync. TargetHeadRoot: {TargetHeadRoot}, PreferredPeer: {PreferredPeer}",
                Convert.ToHexString(pendingRoot.AsSpan()),
                preferredPeerKey ?? "none");
            _ = Task.Run(() => TryRecoverMissingParents(pendingRoot, preferredPeerKey), CancellationToken.None);
        }
    }

    private async Task WaitForGenesisAsync(CancellationToken cancellationToken)
    {
        var genesis = (long)_config.GenesisTimeUnix;
        if (genesis <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now >= genesis)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(genesis - now), cancellationToken);
    }

    private ulong GetCurrentSlotFromClock()
    {
        var genesis = (long)_config.GenesisTimeUnix;
        if (genesis <= 0)
        {
            return CurrentSlot;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now <= genesis)
        {
            return 0;
        }

        var secondsPerSlot = Math.Max(1, _config.SecondsPerSlot);
        return (ulong)((now - genesis) / secondsPerSlot);
    }

    private void HandleGossipMessage(string topic, byte[] payload)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            return;
        }

        try
        {
            if (string.Equals(topic, _gossipTopics.BlockTopic, StringComparison.Ordinal))
            {
                ProcessBlockGossip(payload);
                return;
            }

            if (string.Equals(topic, _gossipTopics.AttestationTopic, StringComparison.Ordinal))
            {
                ProcessAttestationGossip(payload);
                return;
            }

            if (string.Equals(topic, _gossipTopics.AggregateTopic, StringComparison.Ordinal))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process gossip payload for topic {Topic}.", topic);
        }
    }

    private void ProcessBlockGossip(byte[] payload)
    {
        _logger.LogInformation("Received block gossip payload. Size: {PayloadSize}", payload.Length);
        var decodeResult = _gossipDecoder.DecodeAndValidate(payload);
        if (!decodeResult.IsSuccess || decodeResult.SignedBlock is null)
        {
            _logger.LogWarning(
                "Discarding invalid block gossip message. Failure: {Failure}, Reason: {Reason}",
                decodeResult.Failure,
                decodeResult.Reason);
            return;
        }

        var outcome = TryApplyBlock(
            decodeResult.SignedBlock,
            queueOnUnknownParent: true,
            payload.Length,
            payload,
            out var blockRoot,
            out var missingParentRoot);

        if (outcome == BlockApplyOutcome.Accepted)
        {
            ProcessQueuedOrphans(blockRoot);
            return;
        }

        if (outcome == BlockApplyOutcome.QueuedUnknownParent)
        {
            _ = TryRecoverMissingParents(missingParentRoot);
            return;
        }
    }

    private BlockApplyOutcome TryApplyBlock(
        SignedBlockWithAttestation signedBlock,
        bool queueOnUnknownParent,
        int payloadSize,
        ReadOnlyMemory<byte> rawPayload,
        out Bytes32 appliedRoot,
        out Bytes32 missingParentRoot,
        bool attemptAttestationRecoveryRetry = true)
    {
        appliedRoot = new Bytes32(signedBlock.Message.Block.HashTreeRoot());
        missingParentRoot = Bytes32.Zero();
        var blockRootKey = Convert.ToHexString(appliedRoot.AsSpan());
        ForkChoiceApplyResult applyResult;
        ConsensusHeadState? stateToPersist = null;
        State? headChainStateToPersist = null;
        Bytes32[] missingAttestationRoots = Array.Empty<Bytes32>();
        var orphanQueued = false;
        var orphanPendingCount = -1;

        lock (_stateLock)
        {
            var blockProcessingStopwatch = Stopwatch.StartNew();
            applyResult = _forkChoice.ApplyBlock(signedBlock, appliedRoot, CurrentSlot);
            blockProcessingStopwatch.Stop();
            LeanMetrics.RecordForkChoiceBlockProcessing(blockProcessingStopwatch.Elapsed);
            if (applyResult.Accepted)
            {
                _headSlot = applyResult.HeadSlot;
                _headRoot = applyResult.HeadRoot.AsSpan().ToArray();
                var snapshot = _forkChoice.CreateHeadState();
                if (ShouldPersistState(snapshot))
                {
                    stateToPersist = snapshot;
                    _forkChoice.TryGetHeadChainState(out headChainStateToPersist);
                }

                missingAttestationRoots = CollectMissingAttestationRootsLocked(signedBlock);
            }
            else if (queueOnUnknownParent && applyResult.RejectReason == ForkChoiceRejectReason.UnknownParent)
            {
                orphanQueued = QueueOrphanBlockLocked(signedBlock, blockRootKey);
                orphanPendingCount = _orphanBlockCount;
            }
            else if (applyResult.RejectReason == ForkChoiceRejectReason.InvalidAttestation)
            {
                // If attestation roots are missing locally, attempt recovery before dropping the block.
                missingAttestationRoots = CollectMissingAttestationRootsLocked(signedBlock);
            }
        }

        if (!applyResult.Accepted)
        {
            if (applyResult.RejectReason == ForkChoiceRejectReason.UnknownParent)
            {
                missingParentRoot = signedBlock.Message.Block.ParentRoot;
                if (orphanQueued)
                {
                    _logger.LogInformation(
                        "Queued orphan block. ParentRoot: {ParentRoot}, BlockRoot: {BlockRoot}, Pending: {Pending}",
                        Convert.ToHexString(signedBlock.Message.Block.ParentRoot.AsSpan()),
                        blockRootKey,
                        orphanPendingCount);
                }
                else if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Dropped orphan block (duplicate or queue full). ParentRoot: {ParentRoot}, BlockRoot: {BlockRoot}.",
                        Convert.ToHexString(signedBlock.Message.Block.ParentRoot.AsSpan()),
                        blockRootKey);
                }

                return BlockApplyOutcome.QueuedUnknownParent;
            }

            if (attemptAttestationRecoveryRetry &&
                applyResult.RejectReason == ForkChoiceRejectReason.InvalidAttestation &&
                missingAttestationRoots.Length > 0)
            {
                var recoveredAnyRoot = false;
                foreach (var missingRoot in missingAttestationRoots)
                {
                    recoveredAnyRoot |= TryRecoverMissingParents(missingRoot);
                }

                if (recoveredAnyRoot)
                {
                    return TryApplyBlock(
                        signedBlock,
                        queueOnUnknownParent,
                        payloadSize,
                        rawPayload,
                        out appliedRoot,
                        out missingParentRoot,
                        attemptAttestationRecoveryRetry: false);
                }
            }

            _logger.LogWarning(
                "Discarding block after fork-choice checks. RejectReason: {RejectReason}, Reason: {Reason}",
                applyResult.RejectReason,
                applyResult.Reason);

            foreach (var missingRoot in missingAttestationRoots)
            {
                var pendingRoot = missingRoot;
                _ = Task.Run(() => TryRecoverMissingParents(pendingRoot), CancellationToken.None);
            }
            return BlockApplyOutcome.Rejected;
        }

        if (stateToPersist is not null)
        {
            PersistState(stateToPersist, headChainStateToPersist);
        }

        if (rawPayload.IsEmpty)
        {
            _blockStore.Save(appliedRoot, SszEncoding.Encode(signedBlock));
        }
        else
        {
            _blockStore.Save(appliedRoot, rawPayload.Span);
        }

        UpdateForkChoiceMetrics();
        ReplayPendingGossipAttestations();
        _logger.LogInformation(
            "Accepted block. BlockSlot: {BlockSlot}, HeadSlot: {HeadSlot}, HeadRoot: {HeadRoot}, JustifiedSlot: {JustifiedSlot}, FinalizedSlot: {FinalizedSlot}, SafeTargetSlot: {SafeTargetSlot}",
            signedBlock.Message.Block.Slot.Value,
            _headSlot,
            Convert.ToHexString(_headRoot),
            _forkChoice.LatestJustified.Slot.Value,
            _forkChoice.LatestFinalized.Slot.Value,
            _forkChoice.SafeTargetSlot);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Processed block gossip message. Size: {PayloadSize}, HeadSlot: {HeadSlot}, HeadRoot: {HeadRoot}",
                payloadSize,
                _headSlot,
                Convert.ToHexString(_headRoot));
        }

        foreach (var missingRoot in missingAttestationRoots)
        {
            var pendingRoot = missingRoot;
            _ = Task.Run(() => TryRecoverMissingParents(pendingRoot), CancellationToken.None);
        }

        return BlockApplyOutcome.Accepted;
    }

    private Bytes32[] CollectMissingAttestationRootsLocked(SignedBlockWithAttestation signedBlock)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<Bytes32>();

        AddMissingAttestationRootsLocked(signedBlock.Message.ProposerAttestation.Data, seen, missing);
        foreach (var aggregated in signedBlock.Message.Block.Body.Attestations)
        {
            AddMissingAttestationRootsLocked(aggregated.Data, seen, missing);
        }

        return missing.ToArray();
    }

    private void AddMissingAttestationRootsLocked(
        AttestationData data,
        HashSet<string> seen,
        List<Bytes32> missing)
    {
        AddMissingRootIfUnknownLocked(data.Source.Root, seen, missing);
        AddMissingRootIfUnknownLocked(data.Target.Root, seen, missing);
        AddMissingRootIfUnknownLocked(data.Head.Root, seen, missing);
    }

    private void AddMissingRootIfUnknownLocked(Bytes32 root, HashSet<string> seen, List<Bytes32> missing)
    {
        if (_forkChoice.ContainsBlock(root))
        {
            return;
        }

        var key = Convert.ToHexString(root.AsSpan());
        if (!seen.Add(key))
        {
            return;
        }

        missing.Add(root);
    }

    private bool TryRecoverMissingParents(Bytes32 missingParentRoot, string? preferredPeerKey = null)
    {
        var pending = new Queue<Bytes32>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(missingParentRoot);

        var recovered = false;
        while (pending.TryDequeue(out var candidateRoot))
        {
            var candidateKey = Convert.ToHexString(candidateRoot.AsSpan());
            if (!seen.Add(candidateKey))
            {
                continue;
            }

            byte[]? payload;
            try
            {
                var rootBytes = candidateRoot.AsSpan().ToArray();
                payload = string.IsNullOrWhiteSpace(preferredPeerKey)
                    ? _networkService.RequestBlockByRootAsync(rootBytes).GetAwaiter().GetResult()
                    : _networkService.RequestBlockByRootAsync(rootBytes, preferredPeerKey!).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Blocks-by-root request failed for {BlockRoot}.", candidateKey);
                continue;
            }

            if (payload is null || payload.Length == 0)
            {
                continue;
            }

            var decodeResult = _gossipDecoder.DecodeAndValidate(payload);
            if (!decodeResult.IsSuccess || decodeResult.SignedBlock is null)
            {
                _logger.LogWarning(
                    "Discarding invalid blocks-by-root response. Failure: {Failure}, Reason: {Reason}, BlockRoot: {BlockRoot}",
                    decodeResult.Failure,
                    decodeResult.Reason,
                    candidateKey);
                continue;
            }

            var outcome = TryApplyBlock(
                decodeResult.SignedBlock,
                queueOnUnknownParent: true,
                payload.Length,
                payload,
                out var appliedRoot,
                out var missingParent);
            if (outcome == BlockApplyOutcome.Accepted)
            {
                recovered = true;
                ProcessQueuedOrphans(appliedRoot);
                continue;
            }

            if (outcome == BlockApplyOutcome.QueuedUnknownParent)
            {
                pending.Enqueue(missingParent);
            }
        }

        return recovered;
    }

    private void ProcessQueuedOrphans(Bytes32 parentRoot)
    {
        var pending = new Queue<SignedBlockWithAttestation>();
        EnqueueQueuedChildren(parentRoot, pending);

        while (pending.TryDequeue(out var orphan))
        {
            if (TryApplyBlock(
                    orphan,
                    queueOnUnknownParent: true,
                    payloadSize: 0,
                    ReadOnlyMemory<byte>.Empty,
                    out var appliedRoot,
                    out _) != BlockApplyOutcome.Accepted)
            {
                continue;
            }

            EnqueueQueuedChildren(appliedRoot, pending);
        }
    }

    private void EnqueueQueuedChildren(Bytes32 parentRoot, Queue<SignedBlockWithAttestation> pending)
    {
        List<SignedBlockWithAttestation>? children;
        lock (_stateLock)
        {
            children = TakeQueuedChildrenLocked(parentRoot);
        }

        if (children is null)
        {
            return;
        }

        foreach (var child in children.OrderBy(block => block.Message.Block.Slot.Value))
        {
            pending.Enqueue(child);
        }
    }

    private bool QueueOrphanBlockLocked(SignedBlockWithAttestation signedBlock, string blockRootKey)
    {
        if (_orphanBlockRoots.Contains(blockRootKey))
        {
            return false;
        }

        var maxOrphans = Math.Max(1, _config.MaxOrphanBlocks);
        if (_orphanBlockCount >= maxOrphans)
        {
            return false;
        }

        var parentKey = Convert.ToHexString(signedBlock.Message.Block.ParentRoot.AsSpan());
        if (!_orphanBlocksByParent.TryGetValue(parentKey, out var siblings))
        {
            siblings = new List<SignedBlockWithAttestation>();
            _orphanBlocksByParent[parentKey] = siblings;
        }

        siblings.Add(signedBlock);
        _orphanBlockRoots.Add(blockRootKey);
        _orphanBlockCount++;
        return true;
    }

    private List<SignedBlockWithAttestation>? TakeQueuedChildrenLocked(Bytes32 parentRoot)
    {
        var parentKey = Convert.ToHexString(parentRoot.AsSpan());
        if (!_orphanBlocksByParent.TryGetValue(parentKey, out var children))
        {
            return null;
        }

        _orphanBlocksByParent.Remove(parentKey);
        _orphanBlockCount -= children.Count;
        foreach (var child in children)
        {
            var childRoot = Convert.ToHexString(child.Message.Block.HashTreeRoot());
            _orphanBlockRoots.Remove(childRoot);
        }

        return children;
    }

    private void UpdateForkChoiceMetrics()
    {
        LeanMetrics.SetHeadSlot(_forkChoice.HeadSlot);
        LeanMetrics.SetJustifiedSlot(_forkChoice.LatestJustified.Slot.Value);
        LeanMetrics.SetFinalizedSlot(_forkChoice.LatestFinalized.Slot.Value);
        LeanMetrics.SetSafeTargetSlot(_forkChoice.SafeTargetSlot);
    }

    private void ProcessAttestationGossip(byte[] payload)
    {
        var decodeResult = _attestationDecoder.DecodeAndValidate(payload);
        if (!decodeResult.IsSuccess || decodeResult.Attestation is null)
        {
            _logger.LogWarning(
                "Discarding invalid attestation gossip message. Failure: {Failure}, Reason: {Reason}",
                decodeResult.Failure,
                decodeResult.Reason);
            return;
        }

        var signedAttestation = decodeResult.Attestation;
        var missingRoots = CollectMissingAttestationRoots(signedAttestation.Message);
        if (missingRoots.Count > 0)
        {
            EnqueuePendingGossipAttestation(signedAttestation);
            RecoverMissingAttestationRoots(missingRoots);
            return;
        }

        _ = TryApplyGossipAttestation(signedAttestation, replayed: false);
    }

    private void RecoverMissingAttestationRoots(IReadOnlyList<Bytes32> missingRoots)
    {
        foreach (var root in missingRoots)
        {
            var pendingRoot = root;
            _ = Task.Run(() => TryRecoverMissingParents(pendingRoot), CancellationToken.None);
        }
    }

    private List<Bytes32> CollectMissingAttestationRoots(AttestationData data)
    {
        var missingRoots = new List<Bytes32>(3);
        var seenRoots = new HashSet<string>(StringComparer.Ordinal);

        lock (_stateLock)
        {
            AddMissingAttestationRootLocked(data.Source.Root, seenRoots, missingRoots);
            AddMissingAttestationRootLocked(data.Target.Root, seenRoots, missingRoots);
            AddMissingAttestationRootLocked(data.Head.Root, seenRoots, missingRoots);
        }

        return missingRoots;
    }

    private void AddMissingAttestationRootLocked(Bytes32 root, HashSet<string> seenRoots, List<Bytes32> missingRoots)
    {
        var key = Convert.ToHexString(root.AsSpan());
        if (!seenRoots.Add(key))
        {
            return;
        }

        if (_forkChoice.ContainsBlock(root))
        {
            return;
        }

        missingRoots.Add(root);
    }

    private void EnqueuePendingGossipAttestation(SignedAttestation attestation)
    {
        var attestationKey = BuildPendingAttestationKey(attestation);
        var enqueuedAt = DateTimeOffset.UtcNow;
        lock (_stateLock)
        {
            PrunePendingGossipAttestationsLocked(CurrentSlot);
            _pendingGossipAttestations[attestationKey] = new PendingGossipAttestation(attestation, enqueuedAt);
            if (_pendingGossipAttestations.Count <= MaxPendingGossipAttestations)
            {
                return;
            }

            var dropKey = _pendingGossipAttestations
                .OrderBy(pair => pair.Value.Attestation.Message.Slot.Value)
                .ThenBy(pair => pair.Value.EnqueuedAt)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(dropKey))
            {
                _pendingGossipAttestations.Remove(dropKey);
            }
        }
    }

    private void ReplayPendingGossipAttestations()
    {
        List<SignedAttestation>? readyAttestations = null;
        lock (_stateLock)
        {
            if (_pendingGossipAttestations.Count == 0)
            {
                return;
            }

            PrunePendingGossipAttestationsLocked(CurrentSlot);
            if (_pendingGossipAttestations.Count == 0)
            {
                return;
            }

            var readyKeys = _pendingGossipAttestations
                .Where(pair => AreAttestationRootsKnownLocked(pair.Value.Attestation.Message))
                .Select(pair => pair.Key)
                .ToArray();
            if (readyKeys.Length == 0)
            {
                return;
            }

            readyAttestations = new List<SignedAttestation>(readyKeys.Length);
            foreach (var readyKey in readyKeys)
            {
                if (!_pendingGossipAttestations.Remove(readyKey, out var pending))
                {
                    continue;
                }

                readyAttestations.Add(pending.Attestation);
            }
        }

        foreach (var attestation in readyAttestations)
        {
            _ = TryApplyGossipAttestation(attestation, replayed: true);
        }
    }

    private bool TryApplyGossipAttestation(SignedAttestation attestation, bool replayed)
    {
        return TryApplyAttestation(
            attestation,
            replayed ? AttestationApplyMode.ReplayedGossip : AttestationApplyMode.LiveGossip,
            out _);
    }

    private bool TryApplyAttestation(
        SignedAttestation attestation,
        AttestationApplyMode mode,
        out string reason)
    {
        ForkChoiceApplyResult applyResult;
        ConsensusHeadState? stateToPersist = null;
        State? headChainStateToPersist = null;
        ulong safeTargetSlot;
        var attestationValidationStopwatch = Stopwatch.StartNew();
        lock (_stateLock)
        {
            applyResult = _forkChoice.ApplyGossipAttestation(attestation, CurrentSlot);
            if (applyResult.Accepted)
            {
                var snapshot = _forkChoice.CreateHeadState();
                if (ShouldPersistState(snapshot))
                {
                    stateToPersist = snapshot;
                    _forkChoice.TryGetHeadChainState(out headChainStateToPersist);
                }
            }

            safeTargetSlot = _forkChoice.SafeTargetSlot;
        }
        attestationValidationStopwatch.Stop();
        LeanMetrics.RecordAttestationValidation("gossip", applyResult.Accepted, attestationValidationStopwatch.Elapsed);

        if (!applyResult.Accepted)
        {
            reason = applyResult.Reason;
            if (mode == AttestationApplyMode.Local)
            {
                _logger.LogWarning(
                    "Local attestation rejected after fork-choice checks. RejectReason: {RejectReason}, Reason: {Reason}",
                    applyResult.RejectReason,
                    applyResult.Reason);
            }
            else
            {
                _logger.LogWarning(
                    "Discarding {Mode} gossip attestation after fork-choice checks. RejectReason: {RejectReason}, Reason: {Reason}",
                    ToModeLabel(mode),
                    applyResult.RejectReason,
                    applyResult.Reason);
            }

            return false;
        }

        reason = string.Empty;
        LeanMetrics.SetSafeTargetSlot(safeTargetSlot);
        if (stateToPersist is not null)
        {
            PersistState(stateToPersist, headChainStateToPersist);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Processed {Mode} attestation message. ValidatorId: {ValidatorId}, Slot: {Slot}, TargetSlot: {TargetSlot}",
                ToModeLabel(mode),
                attestation.ValidatorId,
                attestation.Message.Slot.Value,
                attestation.Message.Target.Slot.Value);
        }

        return true;
    }

    private static string ToModeLabel(AttestationApplyMode mode)
    {
        return mode switch
        {
            AttestationApplyMode.Local => "local",
            AttestationApplyMode.ReplayedGossip => "replayed",
            _ => "live",
        };
    }

    private bool AreAttestationRootsKnownLocked(AttestationData data)
    {
        return _forkChoice.ContainsBlock(data.Source.Root) &&
               _forkChoice.ContainsBlock(data.Target.Root) &&
               _forkChoice.ContainsBlock(data.Head.Root);
    }

    private void PrunePendingGossipAttestationsLocked(ulong currentSlot)
    {
        if (_pendingGossipAttestations.Count == 0)
        {
            return;
        }

        var retainFrom = currentSlot > PendingAttestationRetentionSlots
            ? currentSlot - PendingAttestationRetentionSlots
            : 0;
        var staleKeys = _pendingGossipAttestations
            .Where(pair => pair.Value.Attestation.Message.Slot.Value < retainFrom)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var staleKey in staleKeys)
        {
            _pendingGossipAttestations.Remove(staleKey);
        }
    }

    private static string BuildPendingAttestationKey(SignedAttestation attestation)
    {
        return $"{attestation.ValidatorId}:{Convert.ToHexString(attestation.Message.HashTreeRoot())}";
    }

    private void PersistState(ConsensusHeadState snapshot, State? headChainState)
    {
        if (headChainState is not null)
        {
            _stateStore.Save(snapshot, headChainState);
            return;
        }

        if (snapshot.HeadSlot > 0 &&
            Interlocked.Exchange(ref _missingHeadChainStatePersistWarningLogged, 1) == 0)
        {
            _logger.LogWarning(
                "Persisting head state without a chain-state snapshot. HeadSlot: {HeadSlot}, HeadRoot: {HeadRoot}",
                snapshot.HeadSlot,
                Convert.ToHexString(snapshot.HeadRoot));
        }

        _stateStore.Save(snapshot);
    }

    private bool ShouldPersistState(ConsensusHeadState snapshot)
    {
        if (_lastPersistedState is null)
        {
            _lastPersistedState = snapshot;
            return true;
        }

        if (StatesEqual(_lastPersistedState, snapshot))
        {
            return false;
        }

        _lastPersistedState = snapshot;
        return true;
    }

    private static bool StatesEqual(ConsensusHeadState left, ConsensusHeadState right)
    {
        return left.HeadSlot == right.HeadSlot &&
               left.LatestJustifiedSlot == right.LatestJustifiedSlot &&
               left.LatestFinalizedSlot == right.LatestFinalizedSlot &&
               left.SafeTargetSlot == right.SafeTargetSlot &&
               left.HeadRoot.AsSpan().SequenceEqual(right.HeadRoot) &&
               left.LatestJustifiedRoot.AsSpan().SequenceEqual(right.LatestJustifiedRoot) &&
               left.LatestFinalizedRoot.AsSpan().SequenceEqual(right.LatestFinalizedRoot) &&
               left.SafeTargetRoot.AsSpan().SequenceEqual(right.SafeTargetRoot);
    }

    private async Task SubscribeTopicAsync(string topic, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(topic))
        {
            await _networkService.SubscribeAsync(
                topic,
                payload => HandleGossipMessage(topic, payload),
                cancellationToken);
        }
    }

    private ValueTask<byte[]?> ResolveBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken)
    {
        if (blockRoot.Length != SszEncoding.Bytes32Length)
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var root = new Bytes32(blockRoot.ToArray());
        return ValueTask.FromResult(_blockStore.TryLoad(root, out var payload) ? payload : null);
    }

    private ValueTask<LeanStatusMessage> ResolveStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            return ValueTask.FromResult(
                new LeanStatusMessage(
                    _forkChoice.LatestFinalized.Root.AsSpan(),
                    _forkChoice.LatestFinalized.Slot.Value,
                    _headRoot,
                    _headSlot));
        }
    }

    private ValueTask HandlePeerStatusAsync(LeanStatusMessage status, string? peerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(status);

        Bytes32? syncHeadRoot = null;
        ulong syncHeadSlot = 0;
        string? syncPeerKey = null;
        string syncTriggerReason = string.Empty;
        var normalizedPeerKey = NormalizePeerKey(peerKey);
        lock (_stateLock)
        {
            if (TryParseNonZeroRoot(status.HeadRoot, out var peerHeadRoot))
            {
                var peerHeadKnown = _forkChoice.ContainsBlock(peerHeadRoot);
                if ((status.HeadSlot > _headSlot || !peerHeadKnown) &&
                    !peerHeadRoot.AsSpan().SequenceEqual(_headRoot) &&
                    TryGetStatusSyncTriggerReasonLocked(status.HeadSlot, peerHeadKnown, out syncTriggerReason))
                {
                    if (string.Equals(syncTriggerReason, "missing-target-retry", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(_lastStatusSyncTargetPeerKey))
                    {
                        syncPeerKey = _lastStatusSyncTargetPeerKey;
                    }
                    else if (!string.IsNullOrWhiteSpace(normalizedPeerKey))
                    {
                        syncPeerKey = normalizedPeerKey;
                    }
                    else if (peerHeadRoot.Equals(_lastStatusSyncTargetRoot) &&
                             !string.IsNullOrWhiteSpace(_lastStatusSyncTargetPeerKey))
                    {
                        syncPeerKey = _lastStatusSyncTargetPeerKey;
                    }

                    syncHeadRoot = peerHeadRoot;
                    syncHeadSlot = status.HeadSlot;
                    _statusStartupSyncTriggered = true;
                    _lastStatusSyncTriggerPeerHeadSlot = Math.Max(_lastStatusSyncTriggerPeerHeadSlot, status.HeadSlot);
                    _lastStatusSyncTargetRoot = peerHeadRoot;
                    _lastStatusSyncAttemptUtc = DateTimeOffset.UtcNow;
                    _lastStatusSyncTargetPeerKey = syncPeerKey ?? string.Empty;
                }
            }
        }

        if (syncHeadRoot is Bytes32 pendingHeadRoot)
        {
            _logger.LogInformation(
                "Attempting status-driven block sync ({TriggerReason}). TargetHeadSlot: {TargetHeadSlot}, TargetHeadRoot: {TargetHeadRoot}, PreferredPeer: {PreferredPeer}",
                syncTriggerReason,
                syncHeadSlot,
                Convert.ToHexString(pendingHeadRoot.AsSpan()),
                syncPeerKey ?? "none");
            var preferredPeerKey = syncPeerKey;
            _ = Task.Run(() => TryRecoverMissingParents(pendingHeadRoot, preferredPeerKey), CancellationToken.None);
        }

        return ValueTask.CompletedTask;
    }

    private bool TryGetStatusSyncTriggerReasonLocked(ulong peerHeadSlot, bool peerHeadKnown, out string reason)
    {
        if (!_statusStartupSyncTriggered)
        {
            reason = "startup";
            return true;
        }

        if (!_lastStatusSyncTargetRoot.Equals(Bytes32.Zero()) && !_forkChoice.ContainsBlock(_lastStatusSyncTargetRoot))
        {
            var retryInterval = TimeSpan.FromSeconds(Math.Max(1, _config.SecondsPerSlot));
            if (DateTimeOffset.UtcNow - _lastStatusSyncAttemptUtc >= retryInterval)
            {
                reason = "missing-target-retry";
                return true;
            }
        }

        var majorHeadAdvanceSlots = GetStatusSyncMajorHeadAdvanceSlots();
        if (!peerHeadKnown &&
            IsAheadByAtLeast(peerHeadSlot, _headSlot, majorHeadAdvanceSlots))
        {
            reason = "unknown-head-root";
            return true;
        }

        if (IsAheadByAtLeast(peerHeadSlot, _headSlot, majorHeadAdvanceSlots))
        {
            reason = "behind-peer-head";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private ulong GetStatusSyncMajorHeadAdvanceSlots()
    {
        var slotsPerEpoch = Math.Max(1UL, _config.SlotsPerEpoch);
        return Math.Max(2UL, slotsPerEpoch);
    }

    private static bool IsAheadByAtLeast(ulong candidateSlot, ulong baselineSlot, ulong minimumDelta)
    {
        if (minimumDelta == 0)
        {
            return candidateSlot >= baselineSlot;
        }

        if (candidateSlot <= baselineSlot)
        {
            return false;
        }

        return candidateSlot - baselineSlot >= minimumDelta;
    }

    private static bool TryParseNonZeroRoot(byte[] rootBytes, out Bytes32 root)
    {
        root = Bytes32.Zero();
        if (rootBytes.Length != SszEncoding.Bytes32Length)
        {
            return false;
        }

        var parsed = new Bytes32(rootBytes);
        if (parsed.Equals(Bytes32.Zero()))
        {
            return false;
        }

        root = parsed;
        return true;
    }

    private static string? NormalizePeerKey(string? peerKey)
    {
        return string.IsNullOrWhiteSpace(peerKey) ? null : peerKey.Trim();
    }

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

    private sealed class NoOpBlocksByRootRpcRouter : IBlocksByRootRpcRouter
    {
        public static readonly NoOpBlocksByRootRpcRouter Instance = new();

        public void SetHandler(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]?>>? handler)
        {
        }

        public ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<byte[]?>(null);
        }
    }

    private sealed class NoOpStatusRpcRouter : IStatusRpcRouter
    {
        public static readonly NoOpStatusRpcRouter Instance = new();

        public void SetHandler(Func<CancellationToken, ValueTask<LeanStatusMessage>>? handler)
        {
        }

        public void SetPeerStatusHandler(Func<LeanStatusMessage, string?, CancellationToken, ValueTask>? handler)
        {
        }

        public ValueTask HandlePeerStatusAsync(LeanStatusMessage status, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask HandlePeerStatusAsync(LeanStatusMessage status, string? peerKey, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<LeanStatusMessage> ResolveAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(LeanStatusMessage.Zero());
        }
    }

    private sealed record PendingGossipAttestation(
        SignedAttestation Attestation,
        DateTimeOffset EnqueuedAt);

    private enum AttestationApplyMode
    {
        Local,
        LiveGossip,
        ReplayedGossip
    }

    private enum BlockApplyOutcome
    {
        Accepted = 0,
        QueuedUnknownParent = 1,
        Rejected = 2
    }
}
