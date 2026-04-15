using System.Diagnostics;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using Lean.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ISlotIndexStore? _slotIndexStore;
    private readonly IStateRootIndexStore? _stateRootIndexStore;
    private readonly IStateByRootStore? _stateByRootStore;
    private readonly ChainStateTransition _chainStateTransition;
    private readonly ChainStateCache _chainStateCache;
    private readonly ILogger<ProtoArrayBlockProcessor> _logger;

    public ProtoArrayBlockProcessor(
        ProtoArrayForkChoiceStore store,
        IBlockByRootStore blockStore,
        ConsensusConfig config,
        ChainStateCache chainStateCache,
        ISlotIndexStore? slotIndexStore = null,
        IStateRootIndexStore? stateRootIndexStore = null,
        IStateByRootStore? stateByRootStore = null,
        ILogger<ProtoArrayBlockProcessor>? logger = null)
    {
        _store = store;
        _blockStore = blockStore;
        _chainStateTransition = new ChainStateTransition(config);
        _chainStateCache = chainStateCache;
        _slotIndexStore = slotIndexStore;
        _stateRootIndexStore = stateRootIndexStore;
        _stateByRootStore = stateByRootStore;
        _logger = logger ?? NullLogger<ProtoArrayBlockProcessor>.Instance;
    }

    public ulong HeadSlot
    {
        get
        {
            lock (_store.SyncRoot)
            {
                return _store.HeadSlot;
            }
        }
    }

    public ulong FinalizedSlot
    {
        get
        {
            lock (_store.SyncRoot)
            {
                return _store.FinalizedSlot;
            }
        }
    }

    public bool IsBlockKnown(Bytes32 root)
    {
        lock (_store.SyncRoot)
        {
            return _store.ContainsBlock(root);
        }
    }

    public bool HasState(Bytes32 root)
    {
        return _chainStateCache.TryGet(ChainStateCache.RootKey(root), out _);
    }

    public ForkChoiceApplyResult ProcessBlock(SignedBlock signedBlock)
    {
        ArgumentNullException.ThrowIfNull(signedBlock);
        var forkChoiceTimer = Stopwatch.StartNew();

        var block = signedBlock.Block;
        var blockRoot = new Bytes32(block.HashTreeRoot());
        var parentKey = ChainStateCache.RootKey(block.ParentRoot);

        // Canonical state transition is required — reject if parent state
        // is missing from cache or if the transition itself fails.
        if (!_chainStateCache.TryGet(parentKey, out var parentState))
        {
            var hasPersistedParentBlock = _blockStore.TryLoad(block.ParentRoot, out _);
            var hasPersistedParentState = _stateByRootStore?.TryLoad(block.ParentRoot, out _) == true;
            _logger.LogInformation(
                "ProtoArrayBlockProcessor cache miss. Slot={Slot}, BlockRoot={BlockRoot}, ParentRoot={ParentRoot}, PersistedParentBlock={PersistedParentBlock}, PersistedParentState={PersistedParentState}, CacheCount={CacheCount}",
                block.Slot.Value,
                Convert.ToHexString(blockRoot.AsSpan()),
                Convert.ToHexString(block.ParentRoot.AsSpan()),
                hasPersistedParentBlock,
                hasPersistedParentState,
                _chainStateCache.Count);
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
        lock (_store.SyncRoot)
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
            _slotIndexStore?.Save(block.Slot.Value, blockRoot);
            _stateRootIndexStore?.Save(block.StateRoot, blockRoot);
            _stateByRootStore?.Save(blockRoot, postState);
            _chainStateCache.Set(ChainStateCache.RootKey(blockRoot), postState);
            _logger.LogInformation(
                "ProtoArrayBlockProcessor accepted. Slot={Slot}, BlockRoot={BlockRoot}, ParentRoot={ParentRoot}, CacheCount={CacheCount}",
                block.Slot.Value,
                Convert.ToHexString(blockRoot.AsSpan()),
                Convert.ToHexString(block.ParentRoot.AsSpan()),
                _chainStateCache.Count);
        }
        else
        {
            _logger.LogInformation(
                "ProtoArrayBlockProcessor rejected. Slot={Slot}, BlockRoot={BlockRoot}, ParentRoot={ParentRoot}, Reason={Reason}",
                block.Slot.Value,
                Convert.ToHexString(blockRoot.AsSpan()),
                Convert.ToHexString(block.ParentRoot.AsSpan()),
                result.Reason);
        }

        forkChoiceTimer.Stop();
        LeanMetrics.RecordForkChoiceBlockProcessing(forkChoiceTimer.Elapsed);
        return result;
    }
}
