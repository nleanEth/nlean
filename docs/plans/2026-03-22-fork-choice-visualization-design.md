# Fork Choice Visualization Design

## Overview

Add fork choice tree visualization to nlean, aligned with ethlambda/leanspec:
1. **JSON API** — `GET /lean/v0/fork_choice` returns tree nodes with weights
2. **ASCII log** — Per-slot tree rendering in terminal logs (interval 4)
3. **D3.js UI** — `GET /lean/v0/fork_choice/ui` serves browser visualizer

## Approach: Extend ApiSnapshot (Approach A)

nlean's `ProtoArrayForkChoiceStore` is not thread-safe. ethlambda can read its Store directly (Rust `Arc<RwLock>`), but nlean must use the existing snapshot mechanism. We extend `ApiSnapshot` with fork choice data, populated by `ConsensusServiceV2.GetApiSnapshot()`.

## Data Model

```csharp
// Extended ApiSnapshot
public sealed record ApiSnapshot(
    ulong JustifiedSlot, string JustifiedRoot,
    ulong FinalizedSlot, string FinalizedRoot,
    ForkChoiceSnapshot? ForkChoice);

public sealed record ForkChoiceSnapshot(
    IReadOnlyList<ForkChoiceNode> Nodes,
    string Head, string SafeTarget, ulong ValidatorCount);

public sealed record ForkChoiceNode(
    string Root, ulong Slot, string ParentRoot, long Weight);
```

## Components

### 1. ForkChoiceSnapshot collection (ConsensusServiceV2)

`GetApiSnapshot()` reads `_forkChoiceStore.ProtoArray` nodes and builds the snapshot. Weight comes from `ProtoNode.Weight` (already computed by `ApplyScoreChanges`). ProposerIndex is omitted (not stored in ProtoNode; ethlambda has it via block headers but it's not critical for visualization).

### 2. JSON API endpoint (LeanApiServer)

New case in `HandleRequest`:
- `/lean/v0/fork_choice` — Serialize `ForkChoiceSnapshot` to JSON matching ethlambda schema
- `/lean/v0/fork_choice/ui` — Return embedded HTML with `text/html` content type

JSON response schema (compatible with ethlambda/leanspec):
```json
{
  "nodes": [{"root": "hex", "slot": 0, "parent_root": "hex", "weight": 0}],
  "head": "hex",
  "justified": {"slot": 0, "root": "hex"},
  "finalized": {"slot": 0, "root": "hex"},
  "safe_target": "hex",
  "validator_count": 4
}
```

### 3. ASCII Tree Logger (ForkChoiceTreeFormatter)

C# port of ethlambda's `fork_choice_tree.rs`:
- `FormatForkChoiceTree()` static method
- Linear trunk rendering (no-fork chain segment)
- Unicode box-drawing branches (├──, └──, │)
- Missing slot indicators: `[ ]` (single gap), `[N]` (N gaps)
- Head marker: `*`, Weight annotation: `[w:N]` on leaves
- MAX_DISPLAY_DEPTH = 20, depth truncation with `...`
- Children sorted by weight descending

Called from `ConsensusServiceV2` at interval 4 (end of slot), after `UpdateHead`:
```csharp
if (intervalIndex == 4)
{
    var tree = ForkChoiceTreeFormatter.Format(...);
    _logger.LogInformation("\n{ForkChoiceTree}", tree);
}
```

### 4. D3.js HTML Page

Port ethlambda's `fork_choice.html` (self-contained single page):
- Polls `/lean/v0/fork_choice` every 2 seconds
- D3 tree layout: Y-axis = slot (time flows down), X-axis = fork spread
- Color coding: green (finalized), blue (justified), yellow (safe_target), orange (head)
- Circle radius scales with weight/validator_count
- Tooltips on hover showing root, slot, weight
- Auto-scroll to head node

Embedded as a const string in `LeanApiServer.cs` (avoiding raw string literals per project convention — use escaped string or resource file).

## Files to Create/Modify

| File | Action |
|------|--------|
| `src/Lean.Consensus/Api/LeanApiServer.cs` | Add fork_choice + fork_choice/ui endpoints, extend ApiSnapshot |
| `src/Lean.Consensus/ForkChoice/ForkChoiceTreeFormatter.cs` | **New** — ASCII tree renderer |
| `src/Lean.Consensus/ForkChoice/ProtoArrayForkChoiceStore.cs` | Add method to export snapshot data |
| `src/Lean.Consensus/ConsensusServiceV2.cs` | Build ForkChoiceSnapshot in GetApiSnapshot(), log tree at interval 4 |
| `src/Lean.Consensus/Api/ForkChoiceHtml.cs` | **New** — Embedded D3.js HTML content |
| `tests/Lean.Consensus.Tests/ForkChoice/ForkChoiceTreeFormatterTests.cs` | **New** — ASCII formatter tests |

## Testing

- Unit tests for `ForkChoiceTreeFormatter`: linear chain, fork, missing slots, depth truncation, nested fork
- Verify JSON API response schema in existing API test patterns
- Manual verification via browser at `http://localhost:5053/lean/v0/fork_choice/ui`
