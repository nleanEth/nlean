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
    private const int ChainTimeoutMs = 600_000;
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
    private readonly Dictionary<Bytes32, string?> _pendingBackfills = new();
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

    public void RequestBackfill(Bytes32 parentRoot, string? preferredPeerId = null)
    {
        lock (_pendingLock)
        {
            if (_pendingBackfills.ContainsKey(parentRoot))
            {
                if (!string.IsNullOrWhiteSpace(preferredPeerId))
                    _pendingBackfills[parentRoot] = preferredPeerId;
                return;
            }

            _pendingBackfills[parentRoot] = preferredPeerId;
        }

        _logger.LogInformation(
            "Backfill queued for root {Root}. PeerCount: {PeerCount}, PreferredPeerId: {PreferredPeerId}",
            Convert.ToHexString(parentRoot.AsSpan()),
            _peerManager.PeerCount,
            preferredPeerId ?? "(none)");

        // Non-blocking write; oldest entry dropped if queue is full.
        _queue.Writer.TryWrite(parentRoot);
    }

    public async Task RequestParentsAsync(
        List<Bytes32> roots,
        CancellationToken ct,
        string? preferredPeerId = null)
    {
        var pending = new Queue<Bytes32>(roots);
        var depth = 0;
        var unprocessed = new List<(SignedBlockWithAttestation Block, Bytes32 Root)>();
        var fetchedRoots = new HashSet<Bytes32>();
        var consecutiveFailures = 0;
        const int maxConsecutiveFailures = 10;
        var totalAccepted = 0;
        var totalFetched = 0;

        // Fetch blocks backwards and process incrementally as parents become known.
        // Unlike the old collect-all-then-process approach, this persists progress
        // even if the chain timeout fires mid-sync.
        while (pending.Count > 0 && depth < _maxDepth)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new List<Bytes32>();
            while (pending.Count > 0 && batch.Count < MaxBlocksPerRequest)
            {
                var root = pending.Dequeue();
                if (!_processor.IsBlockKnown(root) && !fetchedRoots.Contains(root))
                    batch.Add(root);
            }

            if (batch.Count == 0)
            {
                _logger.LogInformation(
                    "Backfill: empty batch after filtering known blocks. Pending: {Pending}, Depth: {Depth}",
                    pending.Count, depth);
                break;
            }

            var fetched = await FetchWithRetryAsync(batch, ct, preferredPeerId);
            if (fetched is null || fetched.Count == 0)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= maxConsecutiveFailures)
                {
                    _logger.LogInformation(
                        "Backfill giving up after {Failures} consecutive fetch failures. Fetched: {Fetched}, Accepted: {Accepted}",
                        consecutiveFailures, totalFetched, totalAccepted);
                    break;
                }

                foreach (var root in batch)
                    pending.Enqueue(root);

                await Task.Delay(BaseRetryDelayMs * (1 << Math.Min(consecutiveFailures, 4)), ct);
                continue;
            }

            consecutiveFailures = 0;

            foreach (var block in fetched)
            {
                var blockRoot = new Bytes32(block.Message.Block.HashTreeRoot());
                unprocessed.Add((block, blockRoot));
                fetchedRoots.Add(blockRoot);
                totalFetched++;

                var parentRoot = block.Message.Block.ParentRoot;
                if (!_processor.IsBlockKnown(parentRoot) && !fetchedRoots.Contains(parentRoot))
                    pending.Enqueue(parentRoot);
            }

            // Re-enqueue any batch roots that were not returned (partial response).
            foreach (var root in batch)
            {
                if (!fetchedRoots.Contains(root))
                    pending.Enqueue(root);
            }

            depth++;

            // Incremental processing: try to process any blocks whose parents
            // are now known in the store. Sort by slot (oldest first) so chains
            // build up from the known ancestor.
            var accepted = TryProcessReady(unprocessed);
            totalAccepted += accepted;

            if (accepted > 0)
            {
                _logger.LogInformation(
                    "Backfill incremental: accepted {Accepted} blocks (total fetched: {Fetched}, total accepted: {TotalAccepted}, remaining: {Remaining})",
                    accepted, totalFetched, totalAccepted, unprocessed.Count);
            }
        }

        // Final pass: process any remaining blocks that became ready
        // after the last fetch.
        if (unprocessed.Count > 0)
        {
            var finalAccepted = TryProcessReady(unprocessed);
            totalAccepted += finalAccepted;
        }

        if (totalFetched > 0)
        {
            _logger.LogInformation(
                "Backfill complete: accepted {Accepted}/{Fetched} blocks over {Depth} iterations.",
                totalAccepted, totalFetched, depth);
        }
        else
        {
            _logger.LogInformation(
                "Backfill finished with 0 blocks fetched. Depth: {Depth}, Pending: {Pending}",
                depth, pending.Count);
        }
    }

    /// <summary>
    /// Tries to process blocks from the unprocessed list whose parents are
    /// already known in the store. Removes accepted blocks from the list.
    /// May loop multiple times as each accepted block can unblock its children.
    /// </summary>
    private int TryProcessReady(List<(SignedBlockWithAttestation Block, Bytes32 Root)> unprocessed)
    {
        var accepted = 0;
        bool madeProgress;

        // Sort by slot descending so the reverse-iteration loop (which uses
        // RemoveAt safely) processes oldest first (parents before children).
        unprocessed.Sort((a, b) =>
            b.Block.Message.Block.Slot.Value.CompareTo(a.Block.Message.Block.Slot.Value));

        do
        {
            madeProgress = false;

            for (var i = unprocessed.Count - 1; i >= 0; i--)
            {
                var (block, blockRoot) = unprocessed[i];
                var parentRoot = block.Message.Block.ParentRoot;

                if (!_processor.IsBlockKnown(parentRoot) || !_processor.HasState(parentRoot))
                    continue;

                var result = _processor.ProcessBlock(block);
                if (result.Accepted)
                {
                    accepted++;
                    _onBlockAccepted?.Invoke(blockRoot);
                    madeProgress = true;
                }
                else
                {
                    _logger.LogInformation(
                        "Backfill: block rejected. Slot: {Slot}, Root: {Root}, ParentRoot: {ParentRoot}, Reason: {Reason}",
                        block.Message.Block.Slot.Value,
                        Convert.ToHexString(blockRoot.AsSpan()),
                        Convert.ToHexString(block.Message.Block.ParentRoot.AsSpan()),
                        result.Reason);
                }

                // Remove whether accepted or rejected — no point retrying.
                unprocessed.RemoveAt(i);
            }
        } while (madeProgress && unprocessed.Count > 0);

        return accepted;
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
                    string? preferredPeerId;
                    lock (_pendingLock)
                    {
                        preferredPeerId = _pendingBackfills.TryGetValue(parentRoot, out var hint)
                            ? hint
                            : null;
                    }

                    using var chainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    chainCts.CancelAfter(ChainTimeoutMs);
                    await RequestParentsAsync(
                        new List<Bytes32> { parentRoot },
                        chainCts.Token,
                        preferredPeerId);
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
                    _logger.LogWarning(
                        ex,
                        "Backfill failed for root {Root}. ExceptionType={ExceptionType}, Message={Message}",
                        Convert.ToHexString(parentRoot.AsSpan()),
                        ex.GetType().FullName,
                        ex.Message);
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
        List<Bytes32> batch,
        CancellationToken ct,
        string? preferredPeerId = null)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                _peerManager.RecoverScores();
                await Task.Delay(BaseRetryDelayMs * (1 << attempt), ct);
            }

            var peerId = _peerManager.SelectPeerForRequest(
                attempt == 0 ? preferredPeerId : null);
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
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerRequestTimeoutMs);

                List<SignedBlockWithAttestation> fetched;
                try
                {
                    fetched = await _network.RequestBlocksByRootAsync(peerId, batch, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _peerManager.OnRequestFailure(peerId);
                    _logger.LogInformation(
                        "Backfill: request to {PeerId} timed out after {TimeoutMs}ms (attempt {Attempt}/{MaxRetries})",
                        peerId, PerRequestTimeoutMs, attempt + 1, MaxRetries);
                    continue;
                }
                if (fetched.Count > 0)
                {
                    _peerManager.OnRequestSuccess(peerId);
                    _logger.LogInformation(
                        "Backfill: fetched {Count} blocks from {PeerId} (attempt {Attempt}/{MaxRetries})",
                        fetched.Count, peerId, attempt + 1, MaxRetries);
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
