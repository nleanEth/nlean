using Lean.Consensus.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lean.Consensus.Sync;

public sealed class HeadSync
{
    private readonly IBlockProcessor _processor;
    private readonly NewBlockCache _cache;
    private readonly IBackfillTrigger _backfill;
    private readonly ILogger<HeadSync> _logger;

    public HeadSync(IBlockProcessor processor, NewBlockCache cache, IBackfillTrigger backfill, ILogger<HeadSync>? logger = null)
    {
        _processor = processor;
        _cache = cache;
        _backfill = backfill;
        _logger = logger ?? NullLogger<HeadSync>.Instance;
    }

    public void OnGossipBlock(SignedBlock signedBlock, Bytes32 blockRoot, string? peerId)
    {
        var slot = signedBlock.Block.Slot.Value;
        var parentRoot = signedBlock.Block.ParentRoot;

        if (_processor.IsBlockKnown(blockRoot))
        {
            _logger.LogDebug(
                "HeadSync skip already known: slot={Slot}, blockRoot={Root}",
                slot, blockRoot);
            return;
        }

        if (_processor.IsBlockKnown(parentRoot) && _processor.HasState(parentRoot))
        {
            _logger.LogDebug(
                "HeadSync process parent known: slot={Slot}, blockRoot={Root}, parentRoot={Parent}",
                slot, blockRoot, parentRoot);
            ProcessAndCascade(signedBlock, blockRoot);
        }
        else
        {
            _logger.LogDebug(
                "HeadSync orphan parent unknown: slot={Slot}, blockRoot={Root}, parentRoot={Parent}, cacheSize={CacheSize}",
                slot, blockRoot, parentRoot, _cache.Count);
            _cache.Add(new PendingBlock(blockRoot, parentRoot, slot, peerId, signedBlock));
            _cache.MarkOrphan(parentRoot);
            _backfill.RequestBackfill(parentRoot, peerId);
        }
    }

    /// <summary>
    /// Called by BackfillSync after a block is accepted via backfill,
    /// so that cached children can be cascaded.
    /// </summary>
    public void CascadeChildren(Bytes32 acceptedRoot)
    {
        var children = _cache.GetChildren(acceptedRoot);
        foreach (var child in children)
        {
            if (child.SignedBlock is not null)
            {
                try
                {
                    ProcessAndCascade(child.SignedBlock, child.Root);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to process cached descendant: root={Root}, slot={Slot}", child.Root, child.SignedBlock.Block.Slot.Value);
                }
            }
        }
    }

    private void ProcessAndCascade(SignedBlock signedBlock, Bytes32 blockRoot)
    {
        var result = _processor.ProcessBlock(signedBlock);
        var slot = signedBlock.Block.Slot.Value;

        _logger.LogDebug(
            "HeadSync ProcessAndCascade: slot={Slot}, accepted={Accepted}, reason={Reason}",
            slot, result.Accepted, result.Reason);

        // Always clean up regardless of acceptance to prevent orphan leaks.
        // A rejected block must not linger in the cache or keep an orphan marker alive,
        // otherwise OrphanCount stays > 0 and SyncService remains in Syncing state.
        _cache.Remove(blockRoot);
        _cache.UnmarkOrphan(signedBlock.Block.ParentRoot);

        if (!result.Accepted)
            return;

        // Cascade: process any cached children whose parent is now known
        var children = _cache.GetChildren(blockRoot);
        foreach (var child in children)
        {
            if (child.SignedBlock is not null)
            {
                try
                {
                    ProcessAndCascade(child.SignedBlock, child.Root);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to process cached descendant: root={Root}, slot={Slot}", child.Root, child.SignedBlock.Block.Slot.Value);
                }
            }
        }
    }
}
