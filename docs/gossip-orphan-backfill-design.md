# Gossip Orphan Backfill Optimization

## Problem

When a gossip block arrives with an unknown parent, nlean **immediately** fires a `BlocksByRoot` RPC via `HeadSync.OnGossipBlock` -> `BackfillSync.RequestBackfill`. Two issues:

1. **No future-slot guard**: A block from slot N+2 (clock skew) gets classified as orphan because the node hasn't ticked to slot N+1 yet. This triggers a totally unnecessary RPC.
2. **No grace period for near-head orphans**: Even on a healthy network where the parent is propagating via gossip (arrives in 100-300ms), nlean fires an immediate `BlocksByRoot` RPC that wastes bandwidth and peer resources.

## Cross-Client Comparison

### Beacon Clients (production, mainnet)

| Client | Language | Unknown Parent Strategy | Future-Slot Guard | Grace/Delay |
|--------|----------|------------------------|-------------------|-------------|
| **Lighthouse** | Rust | Immediate single-block lookup. Chain depth cap at 32 -> falls back to range sync. Dedup by block_root. `ignored_chains` LRU (60s) for dropped chains. | 500ms (`MAXIMUM_GOSSIP_CLOCK_DISPARITY`). Future blocks **dropped** entirely. | None (immediate RPC) |
| **Prysm** | Go | **Periodic batch processing** (~3x/slot, every ~4s). Orphans buffered in `slotToPendingBlocks` cache. Batch `BlocksByRoot` RPC with dedup + up to 5 retries with random peer selection. | 2-slot early tolerance (24s) for queueing; 500ms for immediate processing. Future blocks **queued**, not dropped. | Implicit 4s delay via periodic processing |
| **Teku** | Java | Event-driven via `PendingPool` -> `RecentBlocksFetchService`. Max 3 concurrent fetch requests. Exponential retry backoff (5s base, 5min cap). | 500ms + 2-slot `FutureItems` pool. Future blocks **queued**. | Concurrency cap (3) acts as natural rate limiter |

### Lean Clients

| Client | Language | Unknown Parent Strategy | Future-Slot Guard | Grace/Delay |
|--------|----------|------------------------|-------------------|-------------|
| **leanSpec** | Python | Same as nlean: immediate `backfill.fill_missing()` on unknown parent. | **None**. 1-slot tolerance for attestations only, not blocks. | None |
| **zeam** | Zig | Buffer + immediate `fetchBlockByRoots`. `pending_blocks` queue for future-slot timing. | Future-slot: buffer in `pending_blocks`, replay on next tick. | None for missing parents |
| **ream** | Rust | **GossipPreferred**: check gossip cache before RPC. Dual-peer fanout. Near-head bridge detection. A/B-testable strategies via env vars. | Adaptive timeouts (750ms near head, 4s far). | **250ms hedge delay** (`BACKFILL_HEDGE_DELAY`). Gossip cache check before RPC. |
| **qlean** | C++ | Tree-structured cache with parent-child multimap. Immediate `requestBlock` for deepest missing ancestor. BFS cascade on parent arrival. | None explicit. | Retry throttle (`kRetryRequestBlock` interval) |
| **nlean** (before) | C# | Immediate `RequestBackfill` via HeadSync. Channel-based consumer with `_pendingBackfills` dedup. | **None** | **None** |

### Key Takeaways

1. **All production beacon clients** have a future-slot guard (500ms - 2 slots).
2. **Prysm** (most widely deployed) intentionally does NOT fire immediate RPCs -- it batches orphans every ~4 seconds.
3. **ream** is the most sophisticated lean client -- gossip-preferred strategy avoids unnecessary RPCs near head.
4. **leanSpec** and nlean shared the same deficiency: no timing validation, no deferral.

## Solution

### 1. Future-Slot Guard

**File**: `src/Lean.Consensus/ConsensusServiceV2.cs` -- `ProcessGossipBlockFromInbox`

```csharp
var currentSlot = _clock.CurrentSlot;
if (block.Slot.Value > currentSlot + 1)
{
    _logger.LogDebug("HandleGossipBlock: rejected future block. BlockSlot={BlockSlot}, CurrentSlot={CurrentSlot}",
        block.Slot.Value, currentSlot);
    return;
}
```

- `_clock.CurrentSlot` is cheap (stateless wall-clock computation, no lock)
- **+1 slot tolerance** handles normal proposer clock skew (matches Lighthouse)
- Blocks > `currentSlot + 1` are dropped silently; they'll be fetched via backfill when needed

### 2. Grace Period Deferral in BackfillSync

**File**: `src/Lean.Consensus/Sync/BackfillSync.cs`

When the node is synced (near head), `RequestBackfill` defers the channel enqueue by 500ms:

```csharp
if (_shouldDeferBackfill?.Invoke() == true)
    _ = DeferredEnqueueAsync(parentRoot);
else
    _queue.Writer.TryWrite(parentRoot);
```

`DeferredEnqueueAsync` waits 500ms, then checks if the parent arrived via gossip:

```csharp
private async Task DeferredEnqueueAsync(Bytes32 parentRoot)
{
    await Task.Delay(GracePeriodMs, _shutdownCt);

    if (_processor.IsBlockKnown(parentRoot))
    {
        // Parent arrived via gossip -- skip RPC
        lock (_pendingLock) { _pendingBackfills.Remove(parentRoot); }
        return;
    }

    // Parent still missing -- proceed with RPC
    _queue.Writer.TryWrite(parentRoot);
}
```

### 3. Sync State Condition

**File**: `src/Lean.Consensus/Sync/SyncService.cs`

The deferral only activates when the node is synced:

```csharp
shouldDeferBackfill: () => _state == SyncState.Synced
```

- **Synced** (near head, `localHead + 2 >= networkHead`): defer 500ms, parent likely arriving via gossip
- **Syncing** (far behind): immediate backfill, no delay penalty

## Design Decisions

| Decision | Rationale |
|----------|-----------|
| **500ms grace period** | Gossip propagation is 100-300ms on healthy networks. 500ms provides comfortable margin, matches Lighthouse's clock disparity. |
| **Check-after-wait (not active cancellation)** | Simpler than registering cancellation callbacks. Bounded 500ms delay is acceptable. |
| **Fire-and-forget `DeferredEnqueueAsync`** | Runs independently on ThreadPool. Does not block the consumer loop or callers. |
| **`_pendingBackfills` dedup still active** | Multiple children with same unknown parent only trigger one deferred task. |
| **No new interfaces** | `IBackfillTrigger` unchanged. `Func<bool>? shouldDeferBackfill` injected via constructor. |
| **+1 slot future tolerance (not +2)** | More conservative than Prysm/Teku (+2 slots). Blocks from slot N+2 are rare on healthy networks and recoverable via backfill. |

## Edge Cases

| Case | Handling |
|------|----------|
| Multiple children with same unknown parent | `_pendingBackfills` dedup -- only one deferred task fires |
| Parent arrives after deferred enqueue but before `ConsumeAsync` processes | `RequestParentsAsync` filters known blocks (line 111) -- no-op |
| `RetryOrphanBackfills` fires during grace period | `_pendingBackfills` dedup catches -- no duplicate |
| Node transitions Synced -> Syncing during grace | In-flight 500ms delay completes, then proceeds -- bounded cost |
| Shutdown during grace period | `_shutdownCt` cancels `Task.Delay`, cleanup runs |
| Block at `currentSlot + 1` with unknown parent | Passes future-slot check; grace period gives gossip time |

## Flow Diagram

### Before (immediate backfill)

```
Gossip block B (slot N+1, parent=A) arrives
  -> ProcessGossipBlockFromInbox
     -> parent A not in store
     -> SyncService.OnGossipBlockAsync(B)
        -> HeadSync: cache B, mark A orphan
        -> BackfillSync.RequestBackfill(A)      <-- IMMEDIATE
           -> channel enqueue -> ConsumeAsync
           -> BlocksByRoot RPC                   <-- UNNECESSARY
              (parent A arrives via gossip 200ms later)
```

### After (deferred backfill)

```
Gossip block B (slot N+1, parent=A) arrives
  -> ProcessGossipBlockFromInbox
     -> FUTURE-SLOT CHECK: slot <= currentSlot + 1? YES
     -> parent A not in store
     -> SyncService.OnGossipBlockAsync(B)
        -> HeadSync: cache B, mark A orphan
        -> BackfillSync.RequestBackfill(A)
           -> SyncState == Synced?
              YES -> DeferredEnqueueAsync(A):
                     -> await Task.Delay(500ms)
                     -> _processor.IsBlockKnown(A)?
                        YES -> skip RPC (parent arrived via gossip)
                        NO  -> channel enqueue -> RPC
              NO  -> immediate channel enqueue -> RPC
```

## Tests

5 new tests in `tests/Lean.Consensus.Tests/Sync/BackfillSyncTests.cs`:

1. `RequestBackfill_WhenSynced_DefersEnqueue` -- verifies 500ms delay before processing
2. `RequestBackfill_WhenSyncing_EnqueuesImmediately` -- no delay when syncing
3. `RequestBackfill_Deferred_ParentArrivesViaGossip_SkipsRpc` -- gossip delivery cancels RPC
4. `RequestBackfill_Deferred_ParentDoesNotArrive_ProceedsWithRpc` -- falls through to RPC
5. `RequestBackfill_Deferred_Shutdown_CleansUp` -- graceful shutdown mid-grace

All 394 unit tests pass (315 Consensus + 40 Network + 34 Validator + 5 Crypto).
