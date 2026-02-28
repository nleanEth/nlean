using System.Threading.Channels;
using Lean.Consensus.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lean.Consensus.Sync;

public sealed class BackfillSync : IBackfillTrigger
{
    public const int DefaultMaxBackfillDepth = 512;
    public const int MaxBlocksPerRequest = 10;
    private const int MaxRetries = 3;
    private const int BaseRetryDelayMs = 500;
    private const int PerRequestTimeoutMs = 30_000;
    private const int ChainTimeoutMs = 120_000;
    private const int QueueCapacity = 32;

    private readonly INetworkRequester _network;
    private readonly IBlockProcessor _processor;
    private readonly SyncPeerManager _peerManager;
    private readonly int _maxDepth;
    private readonly Action<Bytes32>? _onBlockAccepted;
    private readonly ILogger<BackfillSync> _logger;

    private readonly Channel<Bytes32> _queue = Channel.CreateBounded<Bytes32>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly object _pendingLock = new();
    private readonly HashSet<Bytes32> _pendingBackfills = new();
    private Task? _consumerTask;

    public BackfillSync(INetworkRequester network, IBlockProcessor processor,
        SyncPeerManager peerManager, int maxDepth = DefaultMaxBackfillDepth,
        Action<Bytes32>? onBlockAccepted = null,
        ILogger<BackfillSync>? logger = null)
    {
        _network = network;
        _processor = processor;
        _peerManager = peerManager;
        _maxDepth = maxDepth;
        _onBlockAccepted = onBlockAccepted;
        _logger = logger ?? NullLogger<BackfillSync>.Instance;
    }

    public void SetShutdownToken(CancellationToken ct)
    {
        // Start the single consumer when the sync service starts.
        // Use LongRunning to keep this off the ThreadPool — the consumer
        // loop runs for the entire node lifetime.
        _consumerTask = Task.Factory.StartNew(
            () => ConsumeAsync(ct),
            ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public void RequestBackfill(Bytes32 parentRoot)
    {
        lock (_pendingLock)
        {
            if (!_pendingBackfills.Add(parentRoot))
                return;
        }

        _logger.LogInformation("Backfill queued for root {Root}. PeerCount: {PeerCount}",
            parentRoot, _peerManager.PeerCount);

        // Non-blocking write; oldest entry dropped if queue is full.
        _queue.Writer.TryWrite(parentRoot);
    }

    public async Task RequestParentsAsync(List<Bytes32> roots, CancellationToken ct)
    {
        var pending = new Queue<Bytes32>(roots);
        var depth = 0;
        var collected = new List<(SignedBlockWithAttestation Block, Bytes32 Root)>();

        // Phase 1: Fetch blocks backwards until we reach known ancestors
        while (pending.Count > 0 && depth < _maxDepth)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new List<Bytes32>();
            while (pending.Count > 0 && batch.Count < MaxBlocksPerRequest)
            {
                var root = pending.Dequeue();
                if (!_processor.IsBlockKnown(root))
                    batch.Add(root);
            }

            if (batch.Count == 0)
                break;

            var fetched = await FetchWithRetryAsync(batch, ct);
            if (fetched is null || fetched.Count == 0)
                break;

            foreach (var block in fetched)
            {
                var blockRoot = new Bytes32(block.Message.Block.HashTreeRoot());
                collected.Add((block, blockRoot));

                var parentRoot = block.Message.Block.ParentRoot;
                if (!_processor.IsBlockKnown(parentRoot))
                    pending.Enqueue(parentRoot);
            }

            depth++;
        }

        if (collected.Count > 0)
        {
            _logger.LogInformation(
                "Backfill collected {Count} blocks over {Depth} iterations. Processing in forward order.",
                collected.Count, depth);
        }

        // Phase 2: Process collected blocks in forward slot order (oldest first)
        var accepted = 0;
        foreach (var (block, blockRoot) in collected.OrderBy(x => x.Block.Message.Block.Slot.Value))
        {
            var result = _processor.ProcessBlock(block);
            if (result.Accepted)
            {
                accepted++;
                _onBlockAccepted?.Invoke(blockRoot);
            }
        }

        if (collected.Count > 0)
        {
            _logger.LogInformation(
                "Backfill processed {Accepted}/{Total} blocks successfully.",
                accepted, collected.Count);
        }
    }

    /// <summary>
    /// Waits for the consumer task to complete. Call from StopAsync.
    /// </summary>
    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        if (_consumerTask is not null)
        {
            try { await _consumerTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var parentRoot in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    using var chainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    chainCts.CancelAfter(ChainTimeoutMs);
                    await RequestParentsAsync(new List<Bytes32> { parentRoot }, chainCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Backfill chain timed out for root {Root} after {TimeoutMs}ms",
                        parentRoot, ChainTimeoutMs);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Backfill failed for root {Root}", parentRoot);
                }
                finally
                {
                    lock (_pendingLock)
                    {
                        _pendingBackfills.Remove(parentRoot);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<IReadOnlyList<SignedBlockWithAttestation>?> FetchWithRetryAsync(
        List<Bytes32> batch, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                _peerManager.RecoverScores();
                await Task.Delay(BaseRetryDelayMs * (1 << attempt), ct);
            }

            var peerId = _peerManager.SelectPeerForRequest();
            if (peerId is null)
            {
                _logger.LogInformation(
                    "Backfill: no eligible peers (attempt {Attempt}/{MaxRetries}, peers: {PeerCount})",
                    attempt + 1, MaxRetries, _peerManager.PeerCount);
                continue;
            }

            _peerManager.IncrementInflight(peerId);
            try
            {
                var fetchTask = _network.RequestBlocksByRootAsync(peerId, batch, ct);
                var timeoutTask = Task.Delay(PerRequestTimeoutMs);
                var completed = await Task.WhenAny(fetchTask, timeoutTask);

                if (completed == timeoutTask || !fetchTask.IsCompletedSuccessfully)
                {
                    _peerManager.OnRequestFailure(peerId);
                    _logger.LogInformation(
                        "Backfill: request to {PeerId} timed out after {TimeoutMs}ms (attempt {Attempt}/{MaxRetries})",
                        peerId, PerRequestTimeoutMs, attempt + 1, MaxRetries);
                    continue;
                }

                var fetched = await fetchTask;
                if (fetched.Count > 0)
                {
                    _peerManager.OnRequestSuccess(peerId);
                    return fetched;
                }

                _peerManager.OnRequestFailure(peerId);
                _logger.LogInformation(
                    "Backfill: peer {PeerId} returned 0 blocks (attempt {Attempt}/{MaxRetries})",
                    peerId, attempt + 1, MaxRetries);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _peerManager.OnRequestFailure(peerId);
                _logger.LogInformation(
                    "Backfill: request to {PeerId} timed out (attempt {Attempt}/{MaxRetries})",
                    peerId, attempt + 1, MaxRetries);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _peerManager.OnRequestFailure(peerId);
                _logger.LogInformation(ex,
                    "Backfill: request to {PeerId} failed (attempt {Attempt}/{MaxRetries})",
                    peerId, attempt + 1, MaxRetries);
            }
            finally
            {
                _peerManager.DecrementInflight(peerId);
            }
        }

        return null;
    }
}
