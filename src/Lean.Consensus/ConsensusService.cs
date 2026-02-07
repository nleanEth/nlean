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
    private readonly IConsensusStateStore _stateStore;
    private readonly ConsensusConfig _config;
    private readonly object _stateLock = new();
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _slotLoopCts;
    private Task? _slotLoopTask;
    private int _started;
    private long _currentSlot;
    private ulong _headSlot;
    private byte[] _headRoot = Array.Empty<byte>();

    public ConsensusService(
        ILogger<ConsensusService> logger,
        INetworkService networkService,
        SignedBlockWithAttestationGossipDecoder gossipDecoder,
        IConsensusStateStore stateStore,
        ConsensusConfig config)
    {
        _logger = logger;
        _networkService = networkService;
        _gossipDecoder = gossipDecoder;
        _stateStore = stateStore;
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

        if (_stateStore.TryLoad(out var persistedState))
        {
            lock (_stateLock)
            {
                _headSlot = persistedState.HeadSlot;
                _headRoot = persistedState.HeadRoot;
            }

            LeanMetrics.ConsensusHeadSlot.Set(persistedState.HeadSlot);
            _logger.LogInformation(
                "Loaded persisted consensus head state. HeadSlot: {HeadSlot}, HeadRoot: {HeadRoot}",
                persistedState.HeadSlot,
                Convert.ToHexString(persistedState.HeadRoot));
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
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

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
                LeanMetrics.ConsensusAttestationsTotal.Inc();
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
        if (!decodeResult.IsSuccess || decodeResult.SignedBlock is null || decodeResult.BlockMessageRoot is null)
        {
            _logger.LogWarning(
                "Discarding invalid block gossip message. Failure: {Failure}, Reason: {Reason}",
                decodeResult.Failure,
                decodeResult.Reason);
            return;
        }

        var blockSlot = decodeResult.SignedBlock.Message.Block.Slot.Value;
        var root = decodeResult.BlockMessageRoot.Value.AsSpan().ToArray();

        ulong headSlot;
        lock (_stateLock)
        {
            _headSlot = blockSlot;
            _headRoot = root;
            headSlot = _headSlot;
        }

        _stateStore.Save(new ConsensusHeadState(headSlot, root));
        LeanMetrics.ConsensusBlocksTotal.Inc();
        LeanMetrics.ConsensusHeadSlot.Set(headSlot);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Processed block gossip message. Size: {PayloadSize}, HeadSlot: {HeadSlot}, HeadRoot: {HeadRoot}",
                payload.Length,
                headSlot,
                Convert.ToHexString(root));
        }
    }
}
