using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public sealed class HeadSync
{
    private readonly IBlockProcessor _processor;
    private readonly NewBlockCache _cache;
    private readonly IBackfillTrigger _backfill;

    public HeadSync(IBlockProcessor processor, NewBlockCache cache, IBackfillTrigger backfill)
    {
        _processor = processor;
        _cache = cache;
        _backfill = backfill;
    }

    public void OnGossipBlock(SignedBlockWithAttestation signedBlock, Bytes32 blockRoot, string? peerId)
    {
        if (_processor.IsBlockKnown(blockRoot))
            return;

        var parentRoot = signedBlock.Message.Block.ParentRoot;
        var slot = signedBlock.Message.Block.Slot.Value;

        if (_processor.IsBlockKnown(parentRoot))
        {
            ProcessAndCascade(signedBlock, blockRoot);
        }
        else
        {
            _cache.Add(new PendingBlock(blockRoot, parentRoot, slot, peerId, signedBlock));
            _cache.MarkOrphan(parentRoot);
            _backfill.RequestBackfill(parentRoot);
        }
    }

    private void ProcessAndCascade(SignedBlockWithAttestation signedBlock, Bytes32 blockRoot)
    {
        var result = _processor.ProcessBlock(signedBlock);
        if (!result.Accepted)
            return;

        _cache.Remove(blockRoot);
        _cache.UnmarkOrphan(blockRoot);

        // Cascade: process any cached children whose parent is now known
        var children = _cache.GetChildren(blockRoot);
        foreach (var child in children)
        {
            if (child.SignedBlock is not null)
                ProcessAndCascade(child.SignedBlock, child.Root);
        }
    }
}
