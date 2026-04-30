# Spec-test harness Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace `ForkChoiceRunner` and `StateTransitionRunner`'s hand-rolled harness with `ConsensusTestHarness` that drives real `ConsensusServiceV2` + `ProtoArrayForkChoiceStore` + in-memory stores, mirroring lodestar/lighthouse/teku/prysm test patterns.

**Architecture:** Real production classes where consensus logic lives; in-memory implementations at I/O boundaries (state store, slot index, state-root index, block-by-root, time source). No `IHost`/`BackgroundService`. No network/sync/RPC. See `docs/plans/2026-04-23-spectest-harness-design.md`.

**Tech Stack:** .NET 10 / C# / NUnit. All paths relative to repo root `/Users/grapebaba/Documents/projects/lean/nlean/`.

---

## Phase 0 — Schema fix + unit-test fix (prerequisite)

### Task 0.1: Fix CreateCheckpointHeadState unit-test assertion

**Files:**
- Modify: `tests/Lean.Network.Tests/NodeAppBootstrapDefaultsTests.cs:203`

**Step 1: Open the test and locate the stale assertion**

Run: `grep -n "LatestFinalizedSlot" tests/Lean.Network.Tests/NodeAppBootstrapDefaultsTests.cs`
Expected: line 203 asserts `Is.EqualTo(169UL)`.

**Step 2: Replace the assertion**

Change line 203 from:
```csharp
Assert.That(headState.LatestFinalizedSlot, Is.EqualTo(169UL));
```
to:
```csharp
// leanSpec PR #677: anchor checkpoints are seeded from the anchor slot
// regardless of the state's embedded pre-anchor latest_finalized.slot.
Assert.That(headState.LatestFinalizedSlot, Is.EqualTo(182UL));
```

**Step 3: Run the failing test to confirm it now passes**

Run: `dotnet test tests/Lean.Network.Tests/Lean.Network.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~CreateCheckpointHeadState"`
Expected: `Passed! Failed: 0, Passed: 1`

**Step 4: Commit**

```bash
git add tests/Lean.Network.Tests/NodeAppBootstrapDefaultsTests.cs
git commit -m "test(node): update CreateCheckpointHeadState assertion for PR #677 semantics

The anchor-checkpoint change landed in commit 74547ae did not update
the corresponding assertion: LatestFinalizedSlot now takes the anchor
slot (182), not the pre-anchor latest_finalized.slot (169) that the
state happened to hold."
```

---

### Task 0.2: Update TestState.JustifiedSlots schema

**Files:**
- Modify: `tests/Lean.SpecTests/Types/FixtureTypes.cs` (wherever `TestState` / `JustifiedSlots` lives)

**Step 1: Locate the offending type**

Run: `grep -n "JustifiedSlots\|justifiedSlots\|justified_slots" tests/Lean.SpecTests/Types/FixtureTypes.cs`
Expected: find the `TestState.JustifiedSlots` property currently typed as something that maps to a list of numbers.

**Step 2: Write a failing runner-side test using the new schema**

Create: `tests/Lean.SpecTests/Runners/FixtureTypesTests.cs` (if not present) with a new `[Test]` method:

```csharp
using System.Text.Json;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

[TestFixture]
public sealed class FixtureTypesTests
{
    [Test]
    public void TestState_DeserializesNonGenesisAnchorJustifiedSlotsBitlist()
    {
        // Shape pulled from leanSpec's new non_genesis_anchor fixtures.
        const string json = """
        {
          "slot": 3,
          "config": {"genesisTime": 0},
          "latestJustified": {"slot": 3, "root": "0x00"},
          "latestFinalized": {"slot": 3, "root": "0x00"},
          "latestBlockHeader": {"slot": 3, "proposerIndex": 0, "parentRoot": "0x00", "stateRoot": "0x00", "bodyRoot": "0x00"},
          "historicalBlockHashes": {"data": []},
          "justifiedSlots":        {"data": [false, true, false, true]},
          "validators":            {"data": []},
          "justificationsRoots":   {"data": []},
          "justificationsValidators": {"data": []}
        }
        """;

        var state = JsonSerializer.Deserialize<TestState>(json, FixtureJson.Options);

        Assert.That(state, Is.Not.Null);
        Assert.That(state!.JustifiedSlots.Data, Is.EqualTo(new[] { false, true, false, true }));
    }
}
```

(If the existing `FixtureTypes.cs` uses an exact-match anchor name for the outer list wrapper, mirror it — the key is the inner element type is `bool`, not `ulong`.)

**Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo --filter "FullyQualifiedName~TestState_DeserializesNonGenesis"`
Expected: FAIL with `System.Text.Json.JsonException: ... could not be converted to System.UInt64 ... justifiedSlots.data[0]`.

**Step 4: Change `TestState.JustifiedSlots` element type**

Modify the `List<ulong>` (or equivalent) under `JustifiedSlots.Data` to `List<bool>`. Keep the outer wrapper record unchanged. If any other runner references these values as numbers, update them to read bools.

**Step 5: Rerun the test**

Run: `dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo --filter "FullyQualifiedName~TestState_DeserializesNonGenesis"`
Expected: PASS.

**Step 6: Also rerun every runner that may consume `JustifiedSlots`**

Run: `dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo --filter "FullyQualifiedName~state_transition|FullyQualifiedName~justifiability"`
Expected: no regressions.

**Step 7: Commit**

```bash
git add tests/Lean.SpecTests/Types/FixtureTypes.cs tests/Lean.SpecTests/Runners/FixtureTypesTests.cs
# include any downstream call-site fixups that had to ripple
git commit -m "test(spec): migrate TestState.JustifiedSlots to bool bitlist

leanSpec's non_genesis_anchor fixture family ships justifiedSlots as a
list of booleans (ssz.Bitlist), which the previous List<ulong> binding
refused to deserialize."
```

---

## Phase 1 — In-memory stores

Six tiny TDD tasks; each writes an interface implementation + one smoke unit test.

### Task 1.1: InMemoryStateByRootStore

**Files:**
- Create: `tests/Lean.SpecTests/Harness/InMemoryStateByRootStore.cs`
- Create: `tests/Lean.SpecTests/Harness/InMemoryStateByRootStoreTests.cs`

**Step 1: Write the failing unit test**

```csharp
using Lean.Consensus;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Harness;

[TestFixture]
public sealed class InMemoryStateByRootStoreTests
{
    [Test]
    public void SaveThenTryLoad_ReturnsSavedState()
    {
        var store = new InMemoryStateByRootStore();
        var root = new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var state = TestStates.MinimalGenesis();  // helper seen in existing tests

        store.Save(root, state);

        Assert.That(store.TryLoad(root, out var loaded), Is.True);
        Assert.That(loaded!.Slot.Value, Is.EqualTo(state.Slot.Value));
    }

    [Test]
    public void Delete_RemovesEntry()
    {
        var store = new InMemoryStateByRootStore();
        var root = new Bytes32(Enumerable.Repeat((byte)0x22, 32).ToArray());
        store.Save(root, TestStates.MinimalGenesis());

        store.Delete(root);

        Assert.That(store.TryLoad(root, out _), Is.False);
    }
}
```

(If `TestStates.MinimalGenesis()` doesn't already exist, check `ForkChoiceRunner.BuildConfigFromAnchor` + `ChainStateTransition.CreateGenesisState(1)` inline for a minimal seed.)

**Step 2: Run, confirm failure**

Run: `dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo --filter "FullyQualifiedName~InMemoryStateByRootStoreTests"`
Expected: FAIL (no class `InMemoryStateByRootStore`).

**Step 3: Write the implementation**

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Lean.Consensus;
using Lean.Consensus.Types;

namespace Lean.SpecTests.Harness;

public sealed class InMemoryStateByRootStore : IStateByRootStore
{
    private readonly ConcurrentDictionary<Bytes32, State> _map = new();

    public void Save(Bytes32 blockRoot, State state) => _map[blockRoot] = state;

    public bool TryLoad(Bytes32 blockRoot, [NotNullWhen(true)] out State? state) =>
        _map.TryGetValue(blockRoot, out state);

    public void Delete(Bytes32 blockRoot) => _map.TryRemove(blockRoot, out _);
}
```

**Step 4: Rerun the test — confirm PASS**

Run: `dotnet test ... --filter "FullyQualifiedName~InMemoryStateByRootStoreTests"`
Expected: PASS, 2 tests.

**Step 5: Commit**

```bash
git add tests/Lean.SpecTests/Harness/InMemoryStateByRootStore.cs tests/Lean.SpecTests/Harness/InMemoryStateByRootStoreTests.cs
git commit -m "test(spec): add InMemoryStateByRootStore for spec harness"
```

---

### Task 1.2: InMemoryConsensusStateStore

**Files:**
- Create: `tests/Lean.SpecTests/Harness/InMemoryConsensusStateStore.cs`
- Create: `tests/Lean.SpecTests/Harness/InMemoryConsensusStateStoreTests.cs`

Mirror Task 1.1 structure. The interface has two `Save` / `TryLoad` overloads (head-only and head+chain-state). Implementation holds a `(ConsensusHeadState head, State? headChain)?` tuple.

Commit message: `test(spec): add InMemoryConsensusStateStore for spec harness`

---

### Task 1.3: InMemorySlotIndexStore

**Files:**
- Create: `tests/Lean.SpecTests/Harness/InMemorySlotIndexStore.cs`
- Create: `tests/Lean.SpecTests/Harness/InMemorySlotIndexStoreTests.cs`

Back with `SortedDictionary<ulong, Bytes32>`; implement `Save`, `TryLoad`, `DeleteBelow`, `GetEntriesBelow`. Test covers `Save → TryLoad`, `DeleteBelow` prunes, `GetEntriesBelow` returns in sorted order.

Commit message: `test(spec): add InMemorySlotIndexStore for spec harness`

---

### Task 1.4: InMemoryStateRootIndexStore

**Files:**
- Create: `tests/Lean.SpecTests/Harness/InMemoryStateRootIndexStore.cs`
- Create: `tests/Lean.SpecTests/Harness/InMemoryStateRootIndexStoreTests.cs`

`Dictionary<Bytes32, Bytes32>`. Interface has `Save(stateRoot, blockRoot)` and `TryLoad(stateRoot, out blockRoot)`. One round-trip test.

Commit message: `test(spec): add InMemoryStateRootIndexStore for spec harness`

---

### Task 1.5: InMemoryTimeSource

**Files:**
- Create: `tests/Lean.SpecTests/Harness/InMemoryTimeSource.cs`
- Create: `tests/Lean.SpecTests/Harness/InMemoryTimeSourceTests.cs`

```csharp
public sealed class InMemoryTimeSource : ITimeSource
{
    private long _unix;
    public InMemoryTimeSource(ulong startUnix) => _unix = (long)startUnix;
    public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeSeconds(_unix);
    public void SetUnixTime(ulong unix) => _unix = (long)unix;
}
```

Test: `SetUnixTime(1000)` then `UtcNow.ToUnixTimeSeconds() == 1000`.

Commit message: `test(spec): add InMemoryTimeSource for spec harness`

---

### Task 1.6: Confirm reuse of NoOpBlockByRootStore

**Files:** none created — just verify.

**Step 1:** Run `grep -rn "class NoOpBlockByRootStore" src/`. Expected: lives in `src/Lean.Consensus/BlockByRootStore.cs` or similar; already public and spec tests can reference it.

**Step 2:** If it's internal, change to public + add `InternalsVisibleTo` for tests. Otherwise skip.

No commit unless a visibility change was required.

---

## Phase 2 — ConsensusTestHarness

### Task 2.1: Harness skeleton with FromAnchor

**Files:**
- Create: `tests/Lean.SpecTests/Harness/ConsensusTestHarness.cs`
- Create: `tests/Lean.SpecTests/Harness/ConsensusTestHarnessTests.cs`

**Step 1: Write the failing test**

```csharp
using Lean.Consensus;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Harness;

[TestFixture]
public sealed class ConsensusTestHarnessTests
{
    [Test]
    public void FromAnchor_SeedsStoreAndCacheAtGenesis()
    {
        var anchor = FixtureAnchors.Minimal(numValidators: 4, genesisTime: 1_000_000);

        var harness = ConsensusTestHarness.FromAnchor(anchor);

        var snap = harness.Snapshot();
        Assert.That(snap.FinalizedSlot, Is.EqualTo(0UL));
        Assert.That(snap.JustifiedSlot, Is.EqualTo(0UL));
        // Store's finalized root must be present in the state-by-root store.
        Assert.That(harness.StateByRoot.TryLoad(harness.Store.FinalizedRoot, out _), Is.True);
    }
}
```

Helper `FixtureAnchors.Minimal(n, genesisTime)` returns a `TestState` with the smallest valid shape (pull logic from `ForkChoiceRunner.BuildConfigFromAnchor`).

**Step 2: Run — confirm FAIL** (class missing).

Run: `dotnet test ... --filter "FullyQualifiedName~ConsensusTestHarnessTests"`

**Step 3: Write the minimal implementation**

```csharp
using Lean.Consensus;
using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using Lean.SpecTests.Types;

namespace Lean.SpecTests.Harness;

public sealed class ConsensusTestHarness
{
    public ConsensusServiceV2 Service { get; }
    public ProtoArrayForkChoiceStore Store { get; }
    public SlotClock Clock { get; }
    public InMemoryTimeSource TimeSource { get; }
    public ConsensusConfig Config { get; }
    public IStateByRootStore StateByRoot { get; }
    public ChainStateTransition ChainTransition { get; }

    private ConsensusTestHarness(
        ConsensusServiceV2 service,
        ProtoArrayForkChoiceStore store,
        SlotClock clock,
        InMemoryTimeSource timeSource,
        ConsensusConfig config,
        IStateByRootStore stateByRoot,
        ChainStateTransition chainTransition)
    {
        Service = service;
        Store = store;
        Clock = clock;
        TimeSource = timeSource;
        Config = config;
        StateByRoot = stateByRoot;
        ChainTransition = chainTransition;
    }

    public static ConsensusTestHarness FromAnchor(TestState anchor)
    {
        var config = BuildConfig(anchor);
        var timeSource = new InMemoryTimeSource(config.GenesisTimeUnix);
        var clock = new SlotClock(config, timeSource);
        var store = new ProtoArrayForkChoiceStore(config);

        var stateStore = new InMemoryConsensusStateStore();
        var stateByRoot = new InMemoryStateByRootStore();
        var slotIndex = new InMemorySlotIndexStore();
        var stateRootIndex = new InMemoryStateRootIndexStore();
        var blockCache = new ChainStateCache();

        var chainTransition = new ChainStateTransition(config);
        var anchorState = chainTransition.CreateGenesisState(config.InitialValidatorCount);
        blockCache.Set(ChainStateCache.RootKey(store.FinalizedRoot), anchorState);
        stateByRoot.Save(store.FinalizedRoot, anchorState);

        var service = new ConsensusServiceV2(
            store, clock, config,
            chainStateCache: blockCache,
            stateStore: stateStore,
            stateByRootStore: stateByRoot,
            slotIndexStore: slotIndex,
            stateRootIndexStore: stateRootIndex);

        return new ConsensusTestHarness(
            service, store, clock, timeSource, config, stateByRoot, chainTransition);
    }

    public Lean.Consensus.Api.ApiSnapshot Snapshot() => Service.GetApiSnapshot();

    private static ConsensusConfig BuildConfig(TestState anchor)
    {
        // Duplicate of ForkChoiceRunner.BuildConfigFromAnchor, lifted verbatim.
        // The runner version will delete once the harness is adopted.
        var validators = anchor.Validators?.Data ?? new List<TestValidator>();
        return new ConsensusConfig
        {
            InitialValidatorCount = (ulong)Math.Max(1, validators.Count),
            GenesisTimeUnix = anchor.Config.GenesisTime,
            GenesisValidatorKeys = validators.Select(v => (v.AttestationKeyHex, v.ProposalKeyHex)).ToList(),
        };
    }
}
```

**Step 4: Rerun — confirm PASS**

Run: `dotnet test ... --filter "FullyQualifiedName~FromAnchor_SeedsStoreAndCacheAtGenesis"`

**Step 5: Commit**

```bash
git add tests/Lean.SpecTests/Harness/ConsensusTestHarness.cs tests/Lean.SpecTests/Harness/ConsensusTestHarnessTests.cs
git commit -m "test(spec): add ConsensusTestHarness.FromAnchor"
```

---

### Task 2.2: Harness ProcessBlock pass-through

**Files:**
- Modify: `tests/Lean.SpecTests/Harness/ConsensusTestHarness.cs` (add method)
- Modify: `tests/Lean.SpecTests/Harness/ConsensusTestHarnessTests.cs` (add test)

**Step 1: Failing test — construct a malformed block, expect rejection with a reason**

```csharp
[Test]
public void ProcessBlock_RejectsUnknownParent()
{
    var harness = ConsensusTestHarness.FromAnchor(FixtureAnchors.Minimal(4, 1_000_000));
    var bogusParent = new Bytes32(Enumerable.Repeat((byte)0xAB, 32).ToArray());
    var block = new Block(new Slot(1), 0, bogusParent, Bytes32.Zero(), new BlockBody(Array.Empty<AggregatedAttestation>()));
    var signed = new SignedBlock(block, new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), XmssSignature.Empty()));

    var result = harness.ProcessBlock(signed);

    Assert.That(result.Accepted, Is.False);
    Assert.That(result.RejectReason, Is.Not.Null);
}
```

**Step 2: Run — expect compile fail (no `ProcessBlock` method).**

**Step 3: Add the passthrough**

```csharp
public ForkChoiceApplyResult ProcessBlock(SignedBlock block) => Service.ProcessBlock(block);
```

**Step 4: Rerun — PASS.**

**Step 5: Commit**

```bash
git commit -am "test(spec): harness exposes ProcessBlock passthrough"
```

---

### Task 2.3: Harness TickTo / TryOnAttestation / TryApplyBlock

Same TDD loop for each, one test per method:

- `TickTo(unix)` → assert `harness.Clock.CurrentSlot` matches expectation.
- `TryOnAttestation(att, out reason)` → use `FixtureAttestations.ForGenesis(...)`, assert accepted or rejected consistently with `store.TryOnAttestation`.
- `TryApplyBlock(block, out reason)` → used by `StateTransitionRunner`; walks `ChainTransition.TryComputeStateRoot(parentState, block)` + persists into `StateByRoot`. Test: two blocks in sequence where the second references the first's root.

Commit after each method lands and its test passes.

Suggested grouping: one commit for all three if the harness test fixture can share setup; otherwise split.

```bash
git commit -am "test(spec): harness adds TickTo, TryOnAttestation, TryApplyBlock"
```

---

## Phase 3 — Migrate runners

### Task 3.1: Rewrite ForkChoiceRunner onto the harness

**Files:**
- Modify: `tests/Lean.SpecTests/Runners/ForkChoiceRunner.cs`

**Step 1: Build a snapshot of the current passing fork_choice fixtures**

Run: `dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo --filter "FullyQualifiedName~fork_choice" -- NUnit.TestOutputXml=./trx-before`
Expected: green baseline recorded (or the current skipped / failed set, so we don't regress passes).

**Step 2: Delete the hand-rolled primitives**

Remove:
- `var store = new ProtoArrayForkChoiceStore(...)` block
- `var stateByRoot = new Dictionary<...>`
- Manual `TryComputeStateRoot` + `store.OnBlock` walk inside `ProcessBlockStep`

**Step 3: Drive the harness**

Restructure the step loop:

```csharp
var harness = ConsensusTestHarness.FromAnchor(test.AnchorState);
var blockRegistry = new Dictionary<string, Bytes32>(StringComparer.Ordinal)
{
    ["genesis"] = harness.Store.FinalizedRoot,
};

foreach (var (step, idx) in test.Steps.Select((s, i) => (s, i)))
{
    switch (step.StepType)
    {
        case "block":
            var block = ConvertSignedBlock(step.Block!.ResolveBlock());
            var blockRoot = new Bytes32(block.Block.HashTreeRoot());
            if (!string.IsNullOrEmpty(step.Block.BlockRootLabel))
                blockRegistry[step.Block.BlockRootLabel] = blockRoot;
            var result = harness.ProcessBlock(block);
            AssertBlockStepResult(step, result, idx);
            break;

        case "tick":
            if (step.Time.HasValue)
                harness.TickTo(step.Time.Value);
            break;

        case "attestation":
            harness.TryOnAttestation(ConvertAttestation(step.Attestation!), out _);
            break;
    }

    if (step.Checks is not null)
        ValidateChecks(harness.Snapshot(), harness.Store, step.Checks, idx, blockRegistry);
}
```

**Step 4: Run all fork_choice tests**

Run: `dotnet test ... --filter "FullyQualifiedName~fork_choice"`
Expected: every test that was passing before still passes. Net pass count must not drop.

**Step 5: Diff / investigate any new failure**

If a test regresses, restore the old logic for that path and log a TODO. Do not merge with regressions.

**Step 6: Commit**

```bash
git add tests/Lean.SpecTests/Runners/ForkChoiceRunner.cs
git commit -m "test(spec): ForkChoiceRunner drives ConsensusServiceV2 through ConsensusTestHarness"
```

---

### Task 3.2: Rewrite StateTransitionRunner onto the harness

**Files:**
- Modify: `tests/Lean.SpecTests/Runners/StateTransitionRunner.cs`

**Step 1: Baseline**

Run: `dotnet test ... --filter "FullyQualifiedName~state_transition"`
Record current pass count.

**Step 2: Replace manual plumbing**

Keep the `AdvanceStateToSlot` prologue but delegate to the harness:

```csharp
var harness = ConsensusTestHarness.FromAnchor(test.Pre);
harness.AdvanceStateToSlot(test.Pre.Slot);

var sawFailure = false;
string? failureReason = null;
foreach (var (fixtureBlock, i) in test.Blocks.Select((b, i) => (b, i)))
{
    var block = ConvertBlock(fixtureBlock);
    if (!harness.TryApplyBlock(block, out var reason))
    {
        sawFailure = true;
        failureReason ??= $"block {i} (slot {block.Slot.Value}): {reason}";
    }
}

if (test.Post is null)
    Assert.That(sawFailure, Is.True, failureReason ?? "expected failure but no block was rejected");
else
    AssertStateEquals(test.Post, harness.CurrentState);
```

Add `AdvanceStateToSlot`, `CurrentState`, and `TryApplyBlock` helpers to the harness (Task 2.3 covered these; if not, add now with matching tests).

**Step 3: Run**

Run: `dotnet test ... --filter "FullyQualifiedName~state_transition"`
Expected: no regressions; `test_block_with_wrong_slot` still skipped.

**Step 4: Commit**

```bash
git commit -am "test(spec): StateTransitionRunner drives ConsensusTestHarness"
```

---

## Phase 4 — Validate new fixtures

### Task 4.1: Run the four non_genesis_anchor fork_choice fixtures

Run: `dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo --filter "FullyQualifiedName~non_genesis_anchor"`
Expected: all 4 pass. If any still fails, inspect the failure — often it's a ssz container shape mismatch between the new fixture and the existing `ConvertBlock` helper. Add the narrowest shim needed, re-run, commit.

### Task 4.2: Full spec-test smoke

Run: `dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo`
Expected: all previously passing + the 4 new fork_choice fixtures pass. `api_endpoint/test_*_at_slot_3` and `test_metrics_endpoint_scrape_contract` stay failing (out of scope; document in PR description).

---

## Phase 5 — Finalization

### Task 5.1: Local integration smoke

Publish + run integration suite to make sure no runtime change leaked into the main service path:

```bash
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false
dotnet test tests/Lean.Integration.Tests/Lean.Integration.Tests.csproj -c Release --no-build --nologo
```

Expected: 6/6 pass (known flaky test may need a rerun).

### Task 5.2: Push + tag

```bash
git push origin main
git checkout devnet4 && git merge --no-ff main -m "Merge branch 'main' into devnet4"
git tag -a v0.4.6-devnet4 -m "spec-tests: ConsensusTestHarness + anchor-slot assertion fix"
git push origin devnet4 --tags
git checkout main
```

---

## Reference skills

- @superpowers:test-driven-development — strictly red/green/commit per step
- @superpowers:executing-plans — run this plan task by task
- @superpowers:requesting-code-review — before pushing Phase 3 rewrites
