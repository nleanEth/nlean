using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

/// <summary>
/// Block processor adapter for SyncService that writes accepted blocks
/// into the blocks-by-root store for req/resp serving and computes
/// chain state snapshots required for block production.
/// </summary>
public sealed class ProtoArrayBlockProcessor : IBlockProcessor
{
    private readonly ProtoArrayForkChoiceStore _store;
    private readonly IBlockByRootStore _blockStore;
    private readonly ChainStateTransition _chainStateTransition;
    private readonly ChainStateCache _chainStateCache;

    public ProtoArrayBlockProcessor(
        ProtoArrayForkChoiceStore store,
        IBlockByRootStore blockStore,
        ConsensusConfig config,
        ChainStateCache chainStateCache)
    {
        _store = store;
        _blockStore = blockStore;
        _chainStateTransition = new ChainStateTransition(config);
        _chainStateCache = chainStateCache;
    }

    public ulong HeadSlot
    {
        get
        {
            lock (_store)
            {
                return _store.HeadSlot;
            }
        }
    }

    public bool IsBlockKnown(Bytes32 root)
    {
        lock (_store)
        {
            return _store.ContainsBlock(root);
        }
    }

    public ForkChoiceApplyResult ProcessBlock(SignedBlockWithAttestation signedBlock)
    {
        ArgumentNullException.ThrowIfNull(signedBlock);

        var block = signedBlock.Message.Block;
        var blockRoot = new Bytes32(block.HashTreeRoot());
        var parentKey = ChainStateCache.RootKey(block.ParentRoot);

        // Canonical state transition is required — reject if parent state
        // is missing from cache or if the transition itself fails.
        if (!_chainStateCache.TryGet(parentKey, out var parentState))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.UnknownParent,
                "Parent state not found in chain state cache.",
                0, Bytes32.Zero());
        }

        if (!_chainStateTransition.TryComputeStateRoot(
                parentState, block, out _, out var postState, out var stfReason))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.StateTransitionFailed,
                stfReason,
                0, Bytes32.Zero());
        }

        ForkChoiceApplyResult result;
        lock (_store)
        {
            result = _store.OnBlock(
                signedBlock,
                postState.LatestJustified,
                postState.LatestFinalized,
                (ulong)postState.Validators.Count);
        }

        if (result.Accepted)
        {
            _blockStore.Save(blockRoot, SszEncoding.Encode(signedBlock));
            _chainStateCache.Set(ChainStateCache.RootKey(blockRoot), postState);
        }

        return result;
    }
}
