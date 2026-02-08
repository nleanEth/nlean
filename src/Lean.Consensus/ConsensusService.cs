using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Types;
using Lean.Metrics;
using Lean.Network;
using Microsoft.Extensions.Logging;

namespace Lean.Consensus;

public sealed class ConsensusService : IConsensusService
{
    private readonly ILogger<ConsensusService> _logger;
    private readonly INetworkService _networkService;
    private readonly SignedBlockWithAttestationGossipDecoder _gossipDecoder;
    private readonly SignedAttestationGossipDecoder _attestationDecoder;
    private readonly IConsensusStateStore _stateStore;
    private readonly IBlockByRootStore _blockStore;
    private readonly IBlocksByRootRpcRouter _blocksByRootRpcRouter;
    private readonly ForkChoiceStore _forkChoice;
    private readonly ConsensusConfig _config;
    private readonly object _stateLock = new();
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _slotLoopCts;
    private Task? _slotLoopTask;
    private int _started;
    private long _currentSlot;
    private ulong _headSlot;
    private byte[] _headRoot = Array.Empty<byte>();
    private ConsensusHeadState? _lastPersistedState;
    private readonly Dictionary<string, List<SignedBlockWithAttestation>> _orphanBlocksByParent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _orphanBlockRoots = new(StringComparer.Ordinal);
    private int _orphanBlockCount;

    public ConsensusService(
        ILogger<ConsensusService> logger,
        INetworkService networkService,
        SignedBlockWithAttestationGossipDecoder gossipDecoder,
        SignedAttestationGossipDecoder attestationDecoder,
        IConsensusStateStore stateStore,
        ForkChoiceStore forkChoice,
        ConsensusConfig config,
        IBlockByRootStore? blockStore = null,
        IBlocksByRootRpcRouter? blocksByRootRpcRouter = null)
    {
        _logger = logger;
        _networkService = networkService;
        _gossipDecoder = gossipDecoder;
        _attestationDecoder = attestationDecoder;
        _stateStore = stateStore;
        _blockStore = blockStore ?? NoOpBlockByRootStore.Instance;
        _blocksByRootRpcRouter = blocksByRootRpcRouter ?? NoOpBlocksByRootRpcRouter.Instance;
        _forkChoice = forkChoice;
        _config = config;
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _blocksByRootRpcRouter.SetHandler(ResolveBlockByRootAsync);

        if (_stateStore.TryLoad(out var persistedState))
        {
            lock (_stateLock)
            {
                _forkChoice.InitializeHead(persistedState);
                _headSlot = _forkChoice.HeadSlot;
                _headRoot = _forkChoice.HeadRoot.AsSpan().ToArray();
                Interlocked.Exchange(ref _currentSlot, (long)_headSlot);
                _lastPersistedState = _forkChoice.CreateHeadState();
            }

            LeanMetrics.ConsensusHeadSlot.Set(_forkChoice.HeadSlot);
            LeanMetrics.ConsensusCurrentSlot.Set(CurrentSlot);
            LeanMetrics.ConsensusJustifiedSlot.Set(_forkChoice.LatestJustified.Slot.Value);
            LeanMetrics.ConsensusFinalizedSlot.Set(_forkChoice.LatestFinalized.Slot.Value);
            LeanMetrics.ConsensusSafeTargetSlot.Set(_forkChoice.SafeTargetSlot);
            _logger.LogInformation(
                "Loaded persisted consensus head state. HeadSlot: {HeadSlot}, HeadRoot: {HeadRoot}",
                _forkChoice.HeadSlot,
                Convert.ToHexString(_forkChoice.HeadRoot.AsSpan()));
        }

        if (_config.EnableGossipProcessing)
        {
            await _networkService.SubscribeAsync(
                GossipTopics.Blocks,
                payload => HandleGossipMessage(GossipTopics.Blocks, payload),
                cancellationToken);

            await _networkService.SubscribeAsync(
                GossipTopics.Attestations,
                payload => HandleGossipMessage(GossipTopics.Attestations, payload),
                cancellationToken);

            await _networkService.SubscribeAsync(
                GossipTopics.Aggregates,
                payload => HandleGossipMessage(GossipTopics.Aggregates, payload),
                cancellationToken);
        }

        lock (_lifecycleLock)
        {
            _slotLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _slotLoopTask = RunSlotTickerAsync(_slotLoopCts.Token);
        }

        _logger.LogInformation(
            "Consensus service started. SecondsPerSlot: {SecondsPerSlot}, GossipProcessing: {GossipProcessing}",
            _config.SecondsPerSlot,
            _config.EnableGossipProcessing);
        LeanMetrics.ConsensusOrphanBlocksPending.Set(_orphanBlockCount);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        _blocksByRootRpcRouter.SetHandler(null);

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
        var secondsPerSlot = Math.Max(1, _config.SecondsPerSlot);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(secondsPerSlot));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var slot = Interlocked.Increment(ref _currentSlot);
                LeanMetrics.ConsensusCurrentSlot.Set(slot);
                ForkChoiceApplyResult tickResult;
                ConsensusHeadState? stateToPersist = null;
                lock (_stateLock)
                {
                    tickResult = _forkChoice.OnSlotTick((ulong)slot);
                    if (tickResult.HeadChanged)
                    {
                        _headSlot = tickResult.HeadSlot;
                        _headRoot = tickResult.HeadRoot.AsSpan().ToArray();
                    }

                    var snapshot = _forkChoice.CreateHeadState();
                    if (ShouldPersistState(snapshot))
                    {
                        stateToPersist = snapshot;
                    }
                }

                LeanMetrics.ConsensusHeadSlot.Set(_forkChoice.HeadSlot);
                LeanMetrics.ConsensusJustifiedSlot.Set(_forkChoice.LatestJustified.Slot.Value);
                LeanMetrics.ConsensusFinalizedSlot.Set(_forkChoice.LatestFinalized.Slot.Value);
                LeanMetrics.ConsensusSafeTargetSlot.Set(_forkChoice.SafeTargetSlot);
                if (stateToPersist is not null)
                {
                    _stateStore.Save(stateToPersist);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
    }

    private void HandleGossipMessage(string topic, byte[] payload)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            return;
        }

        try
        {
            LeanMetrics.GossipMessagesTotal.WithLabels(topic).Inc();
            if (topic == GossipTopics.Blocks)
            {
                ProcessBlockGossip(payload);
                return;
            }

            if (topic == GossipTopics.Attestations)
            {
                ProcessAttestationGossip(payload);
                return;
            }

            if (topic == GossipTopics.Aggregates)
            {
                LeanMetrics.ConsensusAggregatesTotal.Inc();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process gossip payload for topic {Topic}.", topic);
        }
    }

    private void ProcessBlockGossip(byte[] payload)
    {
        var decodeResult = _gossipDecoder.DecodeAndValidate(payload);
        if (!decodeResult.IsSuccess || decodeResult.SignedBlock is null)
        {
            _logger.LogWarning(
                "Discarding invalid block gossip message. Failure: {Failure}, Reason: {Reason}",
                decodeResult.Failure,
                decodeResult.Reason);
            return;
        }

        if (!TryApplyBlock(
                decodeResult.SignedBlock,
                queueOnUnknownParent: true,
                payload.Length,
                payload,
                out var blockRoot))
        {
            return;
        }

        ProcessQueuedOrphans(blockRoot);
    }

    private bool TryApplyBlock(
        SignedBlockWithAttestation signedBlock,
        bool queueOnUnknownParent,
        int payloadSize,
        ReadOnlyMemory<byte> rawPayload,
        out Bytes32 appliedRoot)
    {
        appliedRoot = new Bytes32(signedBlock.Message.Block.HashTreeRoot());
        var blockRootKey = Convert.ToHexString(appliedRoot.AsSpan());
        ForkChoiceApplyResult applyResult;
        ConsensusHeadState? stateToPersist = null;
        var orphanQueued = false;
        var orphanPendingCount = -1;

        lock (_stateLock)
        {
            applyResult = _forkChoice.ApplyBlock(signedBlock, appliedRoot, CurrentSlot);
            if (applyResult.Accepted)
            {
                _headSlot = applyResult.HeadSlot;
                _headRoot = applyResult.HeadRoot.AsSpan().ToArray();
                var snapshot = _forkChoice.CreateHeadState();
                if (ShouldPersistState(snapshot))
                {
                    stateToPersist = snapshot;
                }
            }
            else if (queueOnUnknownParent && applyResult.RejectReason == ForkChoiceRejectReason.UnknownParent)
            {
                orphanQueued = QueueOrphanBlockLocked(signedBlock, blockRootKey);
                orphanPendingCount = _orphanBlockCount;
            }
        }

        if (orphanPendingCount >= 0)
        {
            LeanMetrics.ConsensusOrphanBlocksPending.Set(orphanPendingCount);
        }

        if (!applyResult.Accepted)
        {
            if (applyResult.RejectReason == ForkChoiceRejectReason.UnknownParent)
            {
                if (orphanQueued)
                {
                    LeanMetrics.ConsensusOrphanBlocksQueuedTotal.Inc();
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

                return false;
            }

            _logger.LogWarning(
                "Discarding block after fork-choice checks. RejectReason: {RejectReason}, Reason: {Reason}",
                applyResult.RejectReason,
                applyResult.Reason);
            return false;
        }

        if (stateToPersist is not null)
        {
            _stateStore.Save(stateToPersist);
        }

        if (rawPayload.IsEmpty)
        {
            _blockStore.Save(appliedRoot, SszEncoding.Encode(signedBlock));
        }
        else
        {
            _blockStore.Save(appliedRoot, rawPayload.Span);
        }

        LeanMetrics.ConsensusBlocksTotal.Inc();
        UpdateForkChoiceMetrics();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Processed block gossip message. Size: {PayloadSize}, HeadSlot: {HeadSlot}, HeadRoot: {HeadRoot}",
                payloadSize,
                _headSlot,
                Convert.ToHexString(_headRoot));
        }

        return true;
    }

    private void ProcessQueuedOrphans(Bytes32 parentRoot)
    {
        var pending = new Queue<SignedBlockWithAttestation>();
        EnqueueQueuedChildren(parentRoot, pending);

        var recovered = 0;
        while (pending.TryDequeue(out var orphan))
        {
            if (!TryApplyBlock(
                    orphan,
                    queueOnUnknownParent: true,
                    payloadSize: 0,
                    ReadOnlyMemory<byte>.Empty,
                    out var appliedRoot))
            {
                continue;
            }

            recovered++;
            EnqueueQueuedChildren(appliedRoot, pending);
        }

        if (recovered > 0)
        {
            LeanMetrics.ConsensusOrphanBlocksRecoveredTotal.Inc(recovered);
        }
    }

    private void EnqueueQueuedChildren(Bytes32 parentRoot, Queue<SignedBlockWithAttestation> pending)
    {
        List<SignedBlockWithAttestation>? children;
        var orphanPendingCount = -1;
        lock (_stateLock)
        {
            children = TakeQueuedChildrenLocked(parentRoot);
            if (children is not null)
            {
                orphanPendingCount = _orphanBlockCount;
            }
        }

        if (children is null)
        {
            return;
        }

        if (orphanPendingCount >= 0)
        {
            LeanMetrics.ConsensusOrphanBlocksPending.Set(orphanPendingCount);
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
        LeanMetrics.ConsensusHeadSlot.Set(_forkChoice.HeadSlot);
        LeanMetrics.ConsensusJustifiedSlot.Set(_forkChoice.LatestJustified.Slot.Value);
        LeanMetrics.ConsensusFinalizedSlot.Set(_forkChoice.LatestFinalized.Slot.Value);
        LeanMetrics.ConsensusSafeTargetSlot.Set(_forkChoice.SafeTargetSlot);
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

        ForkChoiceApplyResult applyResult;
        ConsensusHeadState? stateToPersist = null;
        lock (_stateLock)
        {
            applyResult = _forkChoice.ApplyGossipAttestation(decodeResult.Attestation, CurrentSlot);
            if (applyResult.Accepted)
            {
                var snapshot = _forkChoice.CreateHeadState();
                if (ShouldPersistState(snapshot))
                {
                    stateToPersist = snapshot;
                }
            }
        }

        if (!applyResult.Accepted)
        {
            _logger.LogWarning(
                "Discarding gossip attestation after fork-choice checks. RejectReason: {RejectReason}, Reason: {Reason}",
                applyResult.RejectReason,
                applyResult.Reason);
            return;
        }

        LeanMetrics.ConsensusAttestationsTotal.Inc();
        LeanMetrics.ConsensusSafeTargetSlot.Set(_forkChoice.SafeTargetSlot);
        if (stateToPersist is not null)
        {
            _stateStore.Save(stateToPersist);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Processed attestation gossip message. ValidatorId: {ValidatorId}, Slot: {Slot}, TargetSlot: {TargetSlot}",
                decodeResult.Attestation.ValidatorId,
                decodeResult.Attestation.Message.Slot.Value,
                decodeResult.Attestation.Message.Target.Slot.Value);
        }
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
}
