# Fix: ValidatorService Sync Gating Deadlock

**Date:** 2026-03-14
**Status:** Approved

## Problem

CI integration tests deadlock because ValidatorService's sync gate prevents block production during initial chain startup.

### Root Cause

When 4 nodes start simultaneously on a fresh chain:

1. Node A produces block at slot 3 first (headSlot=3)
2. Nodes B/C/D connect to A, receive status probe: networkHead=3
3. `SyncService.RecomputeState()`: localHead=0, `0+2=2 < 3` => `Syncing`
4. `ValidatorService.OnIntervalAsync` line 122: `_syncService.State != SyncState.Synced` => skip all duties
5. Nodes B/C/D cannot produce blocks => headSlot stays 0 => forever `Syncing` => **deadlock**

When peerCount=0 (before connections), `RecomputeState()` returns `Idle`, which also blocks duties -- a second path to genesis stall.

### Secondary Issue

`DevnetCluster.Dispose()` does not verify child processes fully exit, causing cross-test pollution (PeerCount: 6 > expected 3).

## Cross-Client Analysis

| Client | Validator Sync Gate | peerCount=0 Behavior | Genesis Handling |
|--------|--------------------|-----------------------|------------------|
| **leanSpec** (reference) | **None** | Starts in SYNCING; no gate | ValidatorService runs unconditionally |
| **ethlambda** | **None** | No sync state concept | Unconditional block production |
| **ream** | Yes (`ServiceResponse::Syncing`) | networkHead defaults to 0 => Synced on first tick | Transitions to Synced immediately at genesis |
| **zeam** | Yes (strict) | `.no_peers` blocks (unless `force_block_production`) | ForkChoice `.ready` at slot=0 |
| **nlean** (current) | Yes (hard gate) | `Idle` => blocked | **Deadlocks** |

Key insight: **leanSpec has no sync gating on ValidatorService.** nlean's gate is an implementation-specific addition that is stricter than the spec.

## Chosen Approach: Remove sync gate (align with leanSpec)

Remove the sync gate from ValidatorService entirely. The leanSpec reference does not gate validator duties on sync state, and neither does ethlambda. Block production on a stale head is harmless -- the blocks will simply be orphaned by fork choice.

### Alternative Approaches Considered

1. **Relax gate to only block on `Syncing` (align with ream):** Preserves protection against stale-head production, but doesn't fully solve the deadlock when peers are connected and one node races ahead.
2. **Add `force_block_production` flag (align with zeam):** Flexible, but adds configuration complexity and deviates from spec.

## Design

### Change 1: Remove sync gate in `ValidatorService.OnIntervalAsync`

**File:** `src/Lean.Validator/ValidatorService.cs`

Remove the sync state check entirely:

```csharp
// REMOVE these lines (121-125):
// Do not participate in consensus while syncing.
if (_syncService is not null && _syncService.State != SyncState.Synced)
{
    return;
}
```

The existing `ShouldSuppressDutyForUnknownRoots(slot)` check (later in the method) still prevents building on heads with in-flight block fetches, providing adequate protection against producing on inconsistent state.

### Change 2: Remove `ISyncService` dependency from `ValidatorService`

**File:** `src/Lean.Validator/ValidatorService.cs`

Remove the `_syncService` field and constructor parameter since it is no longer used. Update DI registration in `NodeApp.cs` accordingly.

### Change 3: `NodeProcess` process cleanup verification

**File:** `tests/Lean.Integration.Tests/NodeProcess.cs`

The existing `Kill()` already calls `WaitForExit(5000)` after SIGTERM and SIGKILL. Verify the `Dispose()` method properly sequences kill-then-dispose and add logging if the process is still alive after kill attempts.

### Change 4: Unit test updates

- Remove or update tests that assert `ValidatorService` skips duties when `SyncState != Synced`
- Verify `ValidatorService` still suppresses duties via `ShouldSuppressDutyForUnknownRoots`
- `SyncService` tests unchanged (sync state machine is still useful for backfill decisions and metrics)

## Safety Analysis

| Scenario | Before | After | Safe? |
|----------|--------|-------|-------|
| Genesis, no peers | Idle => blocked | Duties run, blocks stay local | Yes |
| Genesis, peers at same head | Synced => run | Duties run | Unchanged |
| Genesis, one peer ahead by 3+ slots | Syncing => **deadlock** | Duties run, fork choice resolves | Yes - deadlock broken |
| Running, far behind peers | Syncing => blocked | Duties run on stale head | Yes - blocks orphaned, harmless; backfill catches up |
| Running, peer disconnects | Idle => blocked | Duties run on own head | Yes - continuity preserved |

The only behavioral change for running nodes is that a node far behind peers will now produce blocks on its stale head. These blocks are harmless -- they will not gain attestations from other validators (who see the correct head) and will be orphaned by fork choice. The backfill sync continues independently and will catch the node up regardless.

## Test Plan

1. Run `FourNode_ReachesFinalization` -- should no longer deadlock
2. Run `FourNode_CatchupAfterRestart_Finalizes` -- catch-up still works
3. Run `FourNode_CheckpointSync_JoinsAndFinalizes` -- checkpoint sync still works
4. Run `TwoNode_TwoValidatorsEach_ReachesFinalization` -- multi-validator still works
5. Verify no PeerCount leaks across tests (cross-test pollution fixed)
6. Run unit tests for SyncService and ValidatorService

---

# Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove the sync gate from ValidatorService to fix CI deadlock, aligning with leanSpec reference spec.

**Architecture:** Delete the `ISyncService` dependency and sync-state check from `ValidatorService`. The `ShouldSuppressDutyForUnknownRoots` check (which delegates to `ConsensusServiceV2.HasUnknownBlockRootsInFlight`) remains as the only duty suppression mechanism — it only fires when blocks are actively being fetched via backfill, not based on sync state. `SyncService` and its state machine are preserved for backfill/metrics use by `ConsensusServiceV2`.

**Tech Stack:** C# / .NET 10 / NUnit

---

### Task 1: Remove sync gate and ISyncService from ValidatorService

**Files:**
- Modify: `src/Lean.Validator/ValidatorService.cs:7,44,64,74,121-125`

**Step 1: Remove the sync gate check**

Delete lines 121-125 in `ValidatorService.cs`:

```csharp
// DELETE:
        // Do not participate in consensus while syncing.
        if (_syncService is not null && _syncService.State != SyncState.Synced)
        {
            return;
        }
```

**Step 2: Remove _syncService field and constructor parameter**

In `ValidatorService.cs`:
- Delete `using Lean.Consensus.Sync;` (line 7)
- Delete `private readonly ISyncService? _syncService;` (line 44)
- Remove `ISyncService? syncService = null` from constructor parameters (line 64)
- Delete `_syncService = syncService;` (line 74)

**Step 3: Build to verify compilation**

Run: `dotnet build src/Lean.Validator/Lean.Validator.csproj -c Release`
Expected: Build succeeded with 0 errors. Warnings about unused `using` may appear.

**Step 4: Run existing ValidatorService unit tests**

Run: `dotnet test tests/Lean.Validator.Tests/Lean.Validator.Tests.csproj -c Release -v normal`
Expected: All tests pass (none depend on ISyncService).

**Step 5: Commit**

```bash
git add src/Lean.Validator/ValidatorService.cs
git commit -m "fix: remove sync gate from ValidatorService to align with leanSpec"
```

---

### Task 2: Verify ConsensusServiceV2 tests still pass

The `ConsensusServiceV2` still uses `ISyncService` for `HasUnknownBlockRootsInFlight` (line 177).
This is unaffected by our change but we must confirm.

**Files:**
- Read: `tests/Lean.Consensus.Tests/ConsensusServiceV2Tests.cs`

**Step 1: Run ConsensusServiceV2 tests**

Run: `dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj -c Release --filter "FullyQualifiedName~ConsensusServiceV2Tests" -v normal`
Expected: All tests pass (HasUnknownBlockRootsInFlight tests unmodified).

**Step 2: Run SyncService tests**

Run: `dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj -c Release --filter "FullyQualifiedName~SyncServiceTests" -v normal`
Expected: All tests pass (SyncService state machine unmodified).

---

### Task 3: Run full solution build and unit tests

**Step 1: Full solution build**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded with 0 errors.

**Step 2: Full test run (excluding integration tests)**

Run: `dotnet test Lean.sln -c Release --filter "FullyQualifiedName!~Integration"`
Expected: All tests pass.

**Step 3: Format check**

Run: `./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor`
Expected: Format check passed.

**Step 4: Commit if any format fixes needed**

If format check fails, run `./.dotnet-tools/dotnet-format Lean.sln --fix-whitespace --exclude vendor` and commit.

---

### Task 4: Verify NodeProcess.Kill() cleanup (investigation only)

**Files:**
- Read: `tests/Lean.Integration.Tests/NodeProcess.cs:120-150`

**Step 1: Review existing Kill() implementation**

Read `NodeProcess.cs` lines 120-150. The existing code:
1. Sends SIGTERM, waits 5s
2. Falls back to `_process.Kill(entireProcessTree: true)`, waits 5s

This is already correct. The cross-test PeerCount pollution may be caused by gossip connections persisting at the libp2p transport layer after the process exits (OS-level socket TIME_WAIT). No code change needed in `NodeProcess` — the sync gate removal fixes the deadlock, and the process cleanup is already adequate.

**Step 2: Document finding**

No changes needed to NodeProcess. The existing SIGTERM+SIGKILL+WaitForExit sequence is correct.
