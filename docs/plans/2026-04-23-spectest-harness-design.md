# Spec-test harness: exercise production code path

Date: 2026-04-23
Status: Approved — ready for implementation planning
Scope: `tests/Lean.SpecTests` (ForkChoice + StateTransition runners only)

## Motivation

Current spec runners build their own harness around consensus primitives:
`ForkChoiceRunner` constructs `ProtoArrayForkChoiceStore`, maintains a
local `stateByRoot` dictionary, and hand-walks `ChainStateTransition`
and `store.OnBlock` without going through `ConsensusServiceV2`. Every
new leanSpec scenario (e.g. the `non_genesis_anchor` fixtures in the
current leanSpec release, the `api_endpoint/test_*_at_slot_3` cases)
forces a runner-side change instead of just producing the right
on-chain state and letting the service emit a conformant response.

The four mature Ethereum consensus clients handle this the same way:

| Client | Real production class | Stubbed boundaries |
|---|---|---|
| Lodestar | `BeaconChain`, `ForkChoice` | mocked beacon DB, `ClockStopped`, `ExecutionEngineMockBackend` |
| Lighthouse | `BeaconChainHarness<EphemeralHarnessType>` | ephemeral in-memory store, `mock_execution_layer` |
| Teku | `ForkChoice`, `RecentChainData` | `InMemoryStorageSystemBuilder`, `ExecutionLayerChannelStub`, `StubMetricsSystem`, `InlineEventThread` |
| Prysm | `blockchain.Service`, `doublylinkedtree.New()` | `testDB.SetupDB(t)`, `engineMock`, `mock.MockStateNotifier` |

All four: real orchestrator class + in-memory / stub at I/O boundaries,
tests drive service-level APIs (`processBlock` / `ReceiveBlock` /
`process_attestation`).

`ConsensusServiceV2` is already harness-friendly — only `store`,
`clock`, `config` are required constructor parameters and every public
method used by spec tests (`ProcessBlock`, `OnTick`, `GetApiSnapshot`)
is synchronous.

## Scope

**In scope** for the first PR:

- `ForkChoiceRunner` — switch to harness
- `StateTransitionRunner` — switch to harness
- 6 in-memory store / time-source implementations
- `FixtureTypes.TestState.JustifiedSlots` schema fix (leanSpec bool list)
- Unit test assertion fix from the PR #677 port

**Out of scope** (tracked as follow-ups):

- `ApiEndpointRunner` — needs harness to drive slot-advanced snapshots
  for `test_*_at_slot_3`, plus a new `/metrics` endpoint
- `JustifiabilityRunner`, `NetworkingCodecRunner`, `SszRunner`,
  `VerifySignaturesRunner`, `SlotClockRunner` — these runners are pure
  logic / codec tests and do not build a store or chain service

## Architecture

### Harness

New type `Lean.SpecTests.Harness.ConsensusTestHarness`:

```csharp
public sealed class ConsensusTestHarness
{
    public ConsensusServiceV2 Service { get; }
    public ProtoArrayForkChoiceStore Store { get; }
    public SlotClock Clock { get; }
    public InMemoryTimeSource TimeSource { get; }
    public ConsensusConfig Config { get; }
    public IStateByRootStore StateByRoot { get; }
    public ChainStateTransition ChainTransition { get; }

    public static ConsensusTestHarness FromAnchor(TestState anchor);

    public ForkChoiceApplyResult ProcessBlock(SignedBlock block);
    public bool TryOnAttestation(SignedAttestation att, out ForkChoiceRejectReason? reason);
    public void TickTo(ulong unixSeconds);
    public bool TryApplyBlock(Block block, out string reason);
    public ApiSnapshot Snapshot();
}
```

`FromAnchor` wires up the real `ConsensusServiceV2` with in-memory
stores, seeds the genesis state into the cache and state-by-root store,
and returns the harness. Spec runners only touch the high-level API.

### In-memory boundaries

Six small `.cs` files under `tests/Lean.SpecTests/Harness/`:

| Interface | Storage |
|---|---|
| `IConsensusStateStore` | `(State Head, State Finalized)?` field |
| `IStateByRootStore` | `Dictionary<Bytes32, State>` |
| `ISlotIndexStore` | `SortedDictionary<ulong, Bytes32>` |
| `IStateRootIndexStore` | `Dictionary<Bytes32, Bytes32>` |
| `IBlockByRootStore` | reuse `NoOpBlockByRootStore` |
| `ITimeSource` | `InMemoryTimeSource` with `SetUnixTime(ulong)` |

Network / sync / RPC dependencies stay null — they are not exercised
by fork-choice or state-transition fixtures.

### Runner changes

`ForkChoiceRunner`: 200 → ~70 lines. The step switch stays (block /
tick / attestation), but each handler becomes a single harness call.
Check validation reads from `harness.Snapshot()` instead of poking the
store's internal fields.

`StateTransitionRunner`: keeps its `AdvanceStateToSlot` prologue (to
handle fixtures that pre-advance `state.slot` via
`pre_state.process_slots(n)`), then loops blocks through
`harness.TryApplyBlock`. Terminal post-state check moves into the
harness.

### New fixture compatibility (what this enables later)

- `fork_choice/test_*_non_genesis_anchor` (4) — pass automatically
  once `JustifiedSlots` schema is fixed and the harness sets up the
  store via `ConsensusServiceV2` (rather than our old direct
  `ProtoArrayForkChoiceStore` seeding).
- `api_endpoint/test_*_at_slot_3` (3) and
  `test_metrics_endpoint_scrape_contract` (1) — deferred. Will build
  on the same harness in a follow-up PR by calling `harness.TickTo` +
  `harness.ProcessBlock` to walk the anchor to the required slot, then
  serving `harness.Snapshot()` through `LeanApiServer`.

## Implementation order

1. **Phase 0** — bundle existing unit test fix (`CreateCheckpointHeadState`
   assertion: 169 → 182) with the `JustifiedSlots` schema change.
2. **Phase 1** — 6 in-memory stores + 1 tiny unit test each.
3. **Phase 2** — `ConsensusTestHarness` plus 3–5 unit tests covering
   `FromAnchor`, `ProcessBlock`, `TickTo`, `TryOnAttestation`,
   `Snapshot`.
4. **Phase 3** — migrate `ForkChoiceRunner` and `StateTransitionRunner`.
   All pre-existing fork_choice and state_transition fixtures must
   still pass.
5. **Phase 4** — verify the four `non_genesis_anchor` fork_choice
   fixtures now pass. Debug if not.

`ApiEndpointRunner` and the remaining runners move to the harness in
a later PR.

## Testing strategy

- Regression gate: every currently-passing spec test keeps passing.
- Harness unit tests live in a new `Lean.SpecTests.Tests` project or
  in the existing test runner as `[TestFixture]` classes — exact
  location TBD during implementation.
- Local smoke: `dotnet test tests/Lean.SpecTests -c Release` must be
  green before the PR is pushed.
- CI gate: spec-tests + build-test jobs must turn green; integration
  tests are orthogonal to this change.

## YAGNI — explicitly not done

- No `INetworkService` / `ISyncService` in-memory implementations.
- No attestation publish / gossip loop (spec tests only observe
  `Store` state).
- No `IHost` / `BackgroundService` startup (lodestar / teku /
  lighthouse / prysm all bypass this).
- No RPC router mock — status and blocksByRoot are outside the
  fork-choice / state-transition scope.
- No changes to `ApiEndpointRunner`, `JustifiabilityRunner`,
  `NetworkingCodecRunner`, `SszRunner`, `VerifySignaturesRunner`,
  `SlotClockRunner`.

## PR size

- New: ~400 lines (harness + 6 in-memory stores + comments).
- Deleted: ~200 lines (hand-rolled `stateByRoot` / manual check
  validation inside the two runners).
- Modified: ~30 lines (`FixtureTypes.JustifiedSlots`, unit test
  assertion).

Single reviewable PR.
