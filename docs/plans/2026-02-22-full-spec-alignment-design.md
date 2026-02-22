# Full leanSpec Alignment Design

## Context

nlean is a Lean consensus client in C# (.NET 10+). The current implementation has architectural divergences from the leanSpec reference and lacks production optimizations found in battle-tested clients (Lighthouse, Prysm, Teku, Lodestar).

This design rewrites core modules to align with leanSpec while incorporating performance patterns from production Ethereum consensus clients.

## Design Principles

1. **Spec-aligned interfaces** — match leanSpec naming and data flow
2. **Proto-array fork choice** — O(n) propagation, not O(n^2) recomputation (Lighthouse/Prysm)
3. **Keep Rust FFI for crypto** — XMSS via existing Lean.Crypto bindings
4. **Leverage libp2p gossipsub peer scoring** — use Nethermind.Libp2p built-in scoring
5. **In-memory caches** — hot state cache, LRU block cache
6. **Reactive gossip-driven sync** — spec pattern: gossip blocks arrive, backfill missing parents

## Data Flow

```
Network Events -> NetworkService -> SyncService -> ForkChoiceStore -> ChainService (ticks) -> ValidatorService -> Network
```

## Module Design

### 1. ForkChoice — Proto-Array (Lean.Consensus/ForkChoice/)

Replaces current full-recomputation ForkChoiceStore.

#### Files
- `ProtoArray.cs` — Flat array of ProtoNodes, O(1) lookup, O(n) weight propagation
- `ProtoNode.cs` — root, slot, parent_index, weight, best_child, best_descendant, justified/finalized epochs
- `ForkChoiceStore.cs` — Wraps ProtoArray + checkpoint tracking + justified/finalized state

#### Proto-Array Algorithm (from Lighthouse)
- Nodes stored in `List<ProtoNode>` indexed by insertion order
- `Dictionary<Bytes32, int>` maps root to array index for O(1) lookup
- `ApplyScoreChanges(deltas)` propagates weight changes bottom-up in single pass
- `FindHead()` walks best_child/best_descendant chain from justified root
- Pruning: remove finalized ancestors on each finalization

#### Key Constants
- `INTERVALS_PER_SLOT = 5`
- `JUSTIFICATION_LOOKBACK_SLOTS = 3`

### 2. Sync Subsystem (Lean.Consensus/Sync/)

Full rewrite following leanSpec sync module.

#### Files
- `SyncState.cs` — State machine: Idle -> Syncing -> Synced
- `SyncService.cs` — Central coordinator
- `HeadSync.cs` — Gossip block processing with descendant cascade
- `BackfillSync.cs` — Recursive parent fetching
- `BlockCache.cs` — Pending blocks with parent-child index, FIFO eviction
- `PeerManager.cs` — Sync-layer peer selection wrapping libp2p scoring
- `CheckpointSync.cs` — HTTP SSZ finalized state download
- `SyncProgress.cs` — Monitoring snapshot

#### SyncState Transitions
```
Idle -> Syncing    : peers connected, need to sync
Syncing -> Synced  : head >= network_finalized AND orphan_count == 0
Synced -> Syncing  : gap detected or fell behind
Any -> Idle        : no connected peers or shutdown
```

#### SyncService Interface
```csharp
public interface ISyncService
{
    SyncState State { get; }
    Task OnGossipBlockAsync(SignedBlockWithAttestation block, string? peerId);
    Task OnGossipAttestationAsync(SignedAttestation attestation);
    Task OnPeerStatusAsync(string peerId, LeanStatusMessage status);
    SyncProgress GetProgress();
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

#### HeadSync Algorithm
```
block arrives ->
  already in store? -> skip
  parent in store?
    YES -> process block, cascade cached descendants
    NO  -> cache block, mark orphan, trigger backfill
```

#### BackfillSync
- Recursive parent fetching via BlocksByRoot RPC
- Depth limit: MAX_BACKFILL_DEPTH = 512
- Batch size: MAX_BLOCKS_PER_REQUEST = 10
- Peer selection via PeerManager weighted random

#### BlockCache
- Capacity: MAX_CACHED_BLOCKS = 1024
- FIFO eviction via LinkedList or OrderedDictionary pattern
- Parent-child index: Dictionary<Bytes32, HashSet<Bytes32>>
- Orphan tracking: HashSet<Bytes32>
- Methods: Add, Get, Remove, MarkOrphan, GetChildren, GetProcessable

#### PeerManager
- Wraps Nethermind.Libp2p gossipsub peer scoring (not custom scoring)
- Adds sync-specific: per-peer request throttling (max 2 concurrent)
- Weighted random selection among available peers
- Tracks peer status (head_slot, finalized_slot)
- Methods: AddPeer, RemovePeer, UpdateStatus, SelectPeerForRequest, OnRequestSuccess/Failure

#### CheckpointSync
- HTTP GET `{url}/lean/v0/states/finalized`
- Accept: application/octet-stream
- SSZ deserialize State
- Structural validation: validator count > 0, <= VALIDATOR_REGISTRY_LIMIT, hash_tree_root integrity

### 3. Chain & Clock (Lean.Consensus/Chain/)

Extracted from ConsensusService.

#### Files
- `SlotClock.cs` — genesis_time -> current slot/interval conversion
- `ChainService.cs` — Drives interval ticks on ForkChoiceStore

#### SlotClock
```csharp
public class SlotClock
{
    ulong CurrentSlot { get; }
    ulong CurrentInterval { get; }
    ulong TotalIntervals { get; }
    TimeSpan SecondsUntilNextInterval { get; }
}
```

- Injectable time source for testing
- Wall-clock mode (GenesisTimeUnix > 0) or fixed-interval mode
- 5 intervals per slot, 4 seconds per slot = 800ms per interval

#### ChainService
- Runs periodic timer at interval granularity
- Calls ForkChoiceStore.OnIntervalTick(slot, intervalInSlot)
- Notifies SyncService of state changes

### 4. ConsensusService (Lean.Consensus/)

Slimmed from ~1550 lines to ~200 lines. Thin wrapper that:
- Wires SyncService + ChainService + ForkChoiceStore
- Exposes IConsensusService interface for ValidatorService
- Handles lifecycle (start/stop)
- Delegates gossip to SyncService
- Delegates ticking to ChainService

### 5. Networking (Lean.Network/)

Keep existing Nethermind.Libp2p stack. Enhancements:

#### New Interface
```csharp
public interface INetworkRequester
{
    Task<List<SignedBlockWithAttestation>> RequestBlocksByRootAsync(
        string peerId, List<Bytes32> roots, CancellationToken ct);
}
```

#### Peer Scoring Integration
- Expose existing libp2p gossipsub peer scores to PeerManager
- No custom scoring — use gossipsub's built-in score function

### 6. Storage (Lean.Storage/)

Add structured database protocol matching leanSpec.

#### Interface
```csharp
public interface IDatabase
{
    // Blocks
    byte[]? GetBlock(Bytes32 root);
    void PutBlock(Bytes32 root, ReadOnlySpan<byte> sszPayload);
    bool HasBlock(Bytes32 root);

    // States
    byte[]? GetState(Bytes32 root);
    void PutState(Bytes32 root, ReadOnlySpan<byte> sszPayload);

    // Checkpoints
    Checkpoint? GetJustifiedCheckpoint();
    void PutJustifiedCheckpoint(Checkpoint checkpoint);
    Checkpoint? GetFinalizedCheckpoint();
    void PutFinalizedCheckpoint(Checkpoint checkpoint);
    Bytes32? GetHeadRoot();
    void PutHeadRoot(Bytes32 root);

    // Attestations
    AttestationData? GetLatestAttestation(ulong validatorId);
    void PutLatestAttestation(ulong validatorId, AttestationData data);

    // Slot index
    Bytes32? GetBlockRootBySlot(ulong slot);
    void PutBlockRootBySlot(ulong slot, Bytes32 root);
}
```

Implementations: RocksDB (production), InMemory (testing)

### 7. API Server (Lean.Api/)

New project.

#### Endpoints
- `GET /lean/v0/health` — node health check
- `GET /lean/v0/checkpoints/justified` — current justified checkpoint
- `GET /lean/v0/checkpoints/finalized` — current finalized checkpoint
- `GET /lean/v0/states/finalized` — SSZ-encoded finalized state (for checkpoint sync)

#### Implementation
- ASP.NET Core minimal API or Kestrel
- Configurable host/port/enabled
- SSZ response support (application/octet-stream)

### 8. Validator (Lean.Validator/)

Rewrite aligned with leanSpec ValidatorService.

#### Key Changes
- Use SlotClock for duty timing
- Integrate with SyncService for sync-awareness (pause duties while syncing)
- Keep existing Rust FFI crypto (Lean.Crypto)
- Block proposal: `store.produce_block_with_signatures()`
- Attestation: `store.produce_attestation_data()`

### 9. Metrics (Lean.Metrics/)

Add sync-specific metrics:
- `lean_sync_state` — gauge (0=idle, 1=syncing, 2=synced)
- `lean_sync_blocks_processed` — counter
- `lean_sync_peers_connected` — gauge
- `lean_sync_cache_size` — gauge
- `lean_sync_orphan_count` — gauge
- `lean_proto_array_nodes` — gauge (fork choice size)

## What We Keep vs Rewrite

| Component | Action | Reason |
|---|---|---|
| Lean.Crypto (Rust FFI) | KEEP | Production crypto, not in spec's Python |
| Lean.Network (Nethermind.Libp2p) | KEEP + enhance | Working stack, add peer scoring exposure |
| Lean.Metrics (Prometheus) | KEEP + extend | Add sync metrics |
| Lean.Node (DI host) | KEEP + wire new services | Infrastructure unchanged |
| ForkChoiceStore | REWRITE | Proto-array replaces full recomputation |
| ConsensusService | REWRITE | Slim to ~200 lines |
| Sync/* | REWRITE | Full spec-aligned subsystem |
| ValidatorService | REWRITE | Align with spec |
| Storage | ENHANCE | Add structured IDatabase |
| API Server | NEW | Spec requirement |
| Chain/Clock | NEW | Extracted from ConsensusService |

## Implementation Phases

### Phase 1: Core Foundation
1. Proto-array ForkChoiceStore
2. SlotClock + ChainService
3. Slim ConsensusService

### Phase 2: Sync Subsystem
4. SyncState + SyncService
5. BlockCache + HeadSync
6. BackfillSync
7. PeerManager (wrapping libp2p)
8. CheckpointSync

### Phase 3: Infrastructure
9. Structured IDatabase + RocksDB impl
10. API Server
11. Enhanced Metrics

### Phase 4: Validator & Integration
12. Rewritten ValidatorService
13. Node wiring
14. Integration testing

## References

- [leanSpec](https://github.com/lean-spec) — reference Python implementation
- [Lighthouse proto-array](https://github.com/sigp/lighthouse) — Rust fork choice optimization
- [Prysm doubly-linked-tree](https://github.com/prysmaticlabs/prysm) — Go fork choice
- [ethlambda](https://github.com/lambdaclass/ethlambda) — Rust lean client (<5k LOC)
- [Zeam](https://github.com/blockblaz/zeam) — Zig lean client
- [Ream](https://github.com/ReamLabs/ream) — Rust lean client
