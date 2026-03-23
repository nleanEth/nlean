# Fork Choice Visualization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add fork choice tree visualization to nlean — JSON API, ASCII log, and D3.js browser UI — aligned with ethlambda/leanspec.

**Architecture:** Extend the existing ApiSnapshot with ForkChoiceSnapshot data. ProtoArray exports node snapshots via a new GetAllNodes() method. LeanApiServer gains two new endpoints. A new ForkChoiceTreeFormatter class renders ASCII trees in logs at interval 4.

**Tech Stack:** C# / .NET 10, D3.js v7, existing HttpListener API server

---

### Task 1: Add ProtoArray.GetAllNodes() to export snapshot data

**Files:**
- Modify: `src/Lean.Consensus/ForkChoice/ProtoArray.cs:207-211`
- Test: `tests/Lean.Consensus.Tests/ForkChoice/ProtoArrayForkChoiceStoreTests.cs`

**Step 1: Add GetAllNodes method to ProtoArray**

Add after the existing `GetAllBlocks()` method at line 211:

```csharp
/// <summary>
/// Returns all nodes as (root, slot, parentRoot, weight) tuples for snapshot export.
/// </summary>
public IReadOnlyList<(Bytes32 Root, ulong Slot, Bytes32 ParentRoot, long Weight)> GetAllNodes()
{
    var result = new List<(Bytes32, ulong, Bytes32, long)>(_nodes.Count);
    foreach (var node in _nodes)
        result.Add((node.Root, node.Slot, node.ParentRoot, node.Weight));
    return result;
}
```

**Step 2: Verify build**

Run: `dotnet build src/Lean.Consensus/Lean.Consensus.csproj -c Release`
Expected: Build succeeded

**Step 3: Commit**

```
feat(fork-choice): add ProtoArray.GetAllNodes() for snapshot export
```

---

### Task 2: Extend ApiSnapshot with ForkChoiceSnapshot

**Files:**
- Modify: `src/Lean.Consensus/Api/LeanApiServer.cs:137-139`
- Modify: `src/Lean.Consensus/ConsensusServiceV2.cs:133-141`

**Step 1: Add ForkChoiceSnapshot types and extend ApiSnapshot**

In `LeanApiServer.cs`, replace the ApiSnapshot record at line 137-139:

```csharp
public sealed record ForkChoiceNode(
    string Root, ulong Slot, string ParentRoot, long Weight);

public sealed record ForkChoiceSnapshot(
    IReadOnlyList<ForkChoiceNode> Nodes,
    string Head, string SafeTarget, ulong ValidatorCount);

public sealed record ApiSnapshot(
    ulong JustifiedSlot, string JustifiedRoot,
    ulong FinalizedSlot, string FinalizedRoot,
    ForkChoiceSnapshot? ForkChoice = null);
```

**Step 2: Update ConsensusServiceV2.GetApiSnapshot() to build ForkChoiceSnapshot**

In `ConsensusServiceV2.cs`, update `GetApiSnapshot()` at line 133-141:

```csharp
public ApiSnapshot GetApiSnapshot()
{
    var snap = _snapshot;
    ForkChoiceSnapshot? forkChoice = null;

    lock (_storeLock)
    {
        var nodes = _store.ProtoArray.GetAllNodes();
        var fcNodes = new List<ForkChoiceNode>(nodes.Count);
        foreach (var (root, slot, parentRoot, weight) in nodes)
        {
            fcNodes.Add(new ForkChoiceNode(
                Convert.ToHexString(root.AsSpan()),
                slot,
                Convert.ToHexString(parentRoot.AsSpan()),
                weight));
        }

        forkChoice = new ForkChoiceSnapshot(
            fcNodes,
            Convert.ToHexString(snap.HeadRoot.AsSpan()),
            Convert.ToHexString(_store.SafeTarget.AsSpan()),
            _store.ValidatorCount);
    }

    return new ApiSnapshot(
        snap.JustifiedSlot,
        Convert.ToHexString(snap.JustifiedRoot.AsSpan()),
        snap.FinalizedSlot,
        Convert.ToHexString(snap.FinalizedRoot.AsSpan()),
        forkChoice);
}
```

Note: `_store.ValidatorCount` — check if this is exposed. If not, use `_snapshot.ValidatorCount` or the `_validatorCount` field. The `ProtoArrayForkChoiceStore` has `_validatorCount` as private; expose it as a public property if needed.

**Step 3: Fix any compilation errors from ApiSnapshot callers**

Tests that construct `ApiSnapshot` directly may need updating with the new optional parameter. Since `ForkChoice` has a default of `null`, existing call sites should work.

**Step 4: Verify build**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded

**Step 5: Commit**

```
feat(api): extend ApiSnapshot with ForkChoiceSnapshot data
```

---

### Task 3: Add /lean/v0/fork_choice JSON endpoint

**Files:**
- Modify: `src/Lean.Consensus/Api/LeanApiServer.cs:73-115` (HandleRequest switch)

**Step 1: Add fork_choice case to HandleRequest**

Add before the `default:` case:

```csharp
case "/lean/v0/fork_choice":
    var fcSnap = _getSnapshot();
    if (fcSnap.ForkChoice is null)
    {
        WriteJson(response, 503, "{\"error\":\"fork choice not available\"}");
        break;
    }
    var fc = fcSnap.ForkChoice;
    var sb = new System.Text.StringBuilder(4096);
    sb.Append("{\"nodes\":[");
    for (int i = 0; i < fc.Nodes.Count; i++)
    {
        if (i > 0) sb.Append(',');
        var n = fc.Nodes[i];
        sb.Append($"{{\"root\":\"0x{n.Root}\",\"slot\":{n.Slot},\"parent_root\":\"0x{n.ParentRoot}\",\"weight\":{n.Weight}}}");
    }
    sb.Append($"],\"head\":\"0x{fc.Head}\",\"justified\":{{\"slot\":{fcSnap.JustifiedSlot},\"root\":\"0x{fcSnap.JustifiedRoot}\"}},\"finalized\":{{\"slot\":{fcSnap.FinalizedSlot},\"root\":\"0x{fcSnap.FinalizedRoot}\"}},\"safe_target\":\"0x{fc.SafeTarget}\",\"validator_count\":{fc.ValidatorCount}}}");
    WriteJson(response, 200, sb.ToString());
    break;
```

**Step 2: Verify build and manual test**

Run: `dotnet build src/Lean.Consensus/Lean.Consensus.csproj -c Release`
Expected: Build succeeded

**Step 3: Commit**

```
feat(api): add /lean/v0/fork_choice JSON endpoint
```

---

### Task 4: Add D3.js fork choice UI page

**Files:**
- Create: `src/Lean.Consensus/Api/ForkChoiceHtml.cs`
- Modify: `src/Lean.Consensus/Api/LeanApiServer.cs` (add /fork_choice/ui case)

**Step 1: Create ForkChoiceHtml.cs with embedded HTML**

Port ethlambda's `fork_choice.html` as a const string. Change title to "nlean Fork Choice". Since the project uses dotnet-format v5.x which mangles raw string literals, store the HTML as a const field using escaped string or use a static method that builds the string.

Best approach: use a `.html` file as an embedded resource.

Add to `Lean.Consensus.csproj`:
```xml
<ItemGroup>
  <EmbeddedResource Include="Api/fork_choice.html" />
</ItemGroup>
```

Create `src/Lean.Consensus/Api/fork_choice.html` — copy from `vendor/ethlambda/crates/net/rpc/static/fork_choice.html` with title changed to "nlean Fork Choice".

Create `src/Lean.Consensus/Api/ForkChoiceHtml.cs`:
```csharp
using System.Reflection;

namespace Lean.Consensus.Api;

public static class ForkChoiceHtml
{
    private static readonly Lazy<string> _html = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Lean.Consensus.Api.fork_choice.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Content => _html.Value;
}
```

**Step 2: Add /fork_choice/ui endpoint to LeanApiServer**

Add before the `default:` case:

```csharp
case "/lean/v0/fork_choice/ui":
    var html = ForkChoiceHtml.Content;
    response.StatusCode = 200;
    response.ContentType = "text/html; charset=utf-8";
    var htmlBytes = System.Text.Encoding.UTF8.GetBytes(html);
    response.ContentLength64 = htmlBytes.Length;
    response.OutputStream.Write(htmlBytes);
    break;
```

**Step 3: Verify build**

Run: `dotnet build src/Lean.Consensus/Lean.Consensus.csproj -c Release`
Expected: Build succeeded

**Step 4: Commit**

```
feat(api): add /lean/v0/fork_choice/ui D3.js visualization page
```

---

### Task 5: Implement ForkChoiceTreeFormatter (ASCII tree)

**Files:**
- Create: `src/Lean.Consensus/ForkChoice/ForkChoiceTreeFormatter.cs`
- Create: `tests/Lean.Consensus.Tests/ForkChoice/ForkChoiceTreeFormatterTests.cs`

**Step 1: Write failing tests**

Create `tests/Lean.Consensus.Tests/ForkChoice/ForkChoiceTreeFormatterTests.cs`:

```csharp
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;

namespace Lean.Consensus.Tests.ForkChoice;

[TestFixture]
public class ForkChoiceTreeFormatterTests
{
    private static Bytes32 Root(byte b) => new(Enumerable.Repeat(b, 32).ToArray());

    [Test]
    public void EmptyBlocks_ReturnsEmptyMessage()
    {
        var result = ForkChoiceTreeFormatter.Format(
            Array.Empty<(Bytes32, ulong, Bytes32, long)>(),
            Root(1), Root(1), 0, Root(1), 0, Root(1));

        Assert.That(result, Does.Contain("Fork Choice Tree:"));
        Assert.That(result, Does.Contain("(empty)"));
    }

    [Test]
    public void LinearChain_ShowsAllNodes()
    {
        var root = Root(1);
        var a = Root(2);
        var b = Root(3);
        var nodes = new (Bytes32, ulong, Bytes32, long)[]
        {
            (root, 0, Bytes32.Zero(), 0),
            (a, 1, root, 0),
            (b, 2, a, 0),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, b, root, 0, root, 0, root);

        Assert.That(result, Does.Contain("(0)"));
        Assert.That(result, Does.Contain("(1)"));
        Assert.That(result, Does.Contain("(2)"));
        Assert.That(result, Does.Contain("*"));
    }

    [Test]
    public void Fork_ShowsBranches()
    {
        var root = Root(1);
        var a = Root(2);
        var b = Root(3);
        var c = Root(4);
        var d = Root(5);
        var nodes = new (Bytes32, ulong, Bytes32, long)[]
        {
            (root, 0, Bytes32.Zero(), 0),
            (a, 1, root, 0),
            (b, 2, a, 0),
            (c, 3, b, 3),
            (d, 3, b, 1),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, c, root, 0, root, 0, root);

        Assert.That(result, Does.Contain("2 branches"));
        Assert.That(result, Does.Contain("[w:3]"));
        Assert.That(result, Does.Contain("[w:1]"));
    }

    [Test]
    public void MissingSingleSlot_ShowsGapIndicator()
    {
        var root = Root(1);
        var a = Root(2);
        var nodes = new (Bytes32, ulong, Bytes32, long)[]
        {
            (root, 0, Bytes32.Zero(), 0),
            (a, 2, root, 0),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, a, root, 0, root, 0, root);

        Assert.That(result, Does.Contain("[ ]"));
    }

    [Test]
    public void MissingMultipleSlots_ShowsGapCount()
    {
        var root = Root(1);
        var a = Root(2);
        var nodes = new (Bytes32, ulong, Bytes32, long)[]
        {
            (root, 0, Bytes32.Zero(), 0),
            (a, 4, root, 0),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, a, root, 0, root, 0, root);

        Assert.That(result, Does.Contain("[3]"));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Lean.Consensus.Tests -c Release --filter "FullyQualifiedName~ForkChoiceTreeFormatterTests" -v minimal`
Expected: FAIL — ForkChoiceTreeFormatter does not exist

**Step 3: Implement ForkChoiceTreeFormatter**

Create `src/Lean.Consensus/ForkChoice/ForkChoiceTreeFormatter.cs`:

C# port of `vendor/ethlambda/crates/blockchain/src/fork_choice_tree.rs`.

Key method signature:
```csharp
public static class ForkChoiceTreeFormatter
{
    private const int MaxDisplayDepth = 20;

    public static string Format(
        IReadOnlyList<(Bytes32 Root, ulong Slot, Bytes32 ParentRoot, long Weight)> nodes,
        Bytes32 head,
        Bytes32 justifiedRoot, ulong justifiedSlot,
        Bytes32 finalizedRoot, ulong finalizedSlot,
        Bytes32 safeTarget)
    { ... }
}
```

Port the trunk/branch rendering logic from ethlambda:
- Build children map (parent → sorted children by weight desc)
- Find tree root (node whose parent is not in the map)
- Render linear trunk, then branches with Unicode connectors
- Missing slot indicators: `[ ]` (gap=1), `[N]` (gap>1)
- Head marker `*`, leaf weight `[w:N]`
- Depth truncation at 20

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Lean.Consensus.Tests -c Release --filter "FullyQualifiedName~ForkChoiceTreeFormatterTests" -v minimal`
Expected: All 5 tests PASS

**Step 5: Commit**

```
feat(fork-choice): add ASCII tree formatter for fork choice logging
```

---

### Task 6: Integrate ASCII tree logging at interval 4

**Files:**
- Modify: `src/Lean.Consensus/ConsensusServiceV2.cs:482-484`

**Step 1: Add tree logging after TickInterval at interval 4**

After line 483 (`RefreshSnapshot();`), add:

```csharp
if (intervalInSlot == 4)
{
    try
    {
        var allNodes = _store.ProtoArray.GetAllNodes();
        var tree = ForkChoiceTreeFormatter.Format(
            allNodes, _store.HeadRoot,
            _store.JustifiedRoot, _store.JustifiedSlot,
            _store.FinalizedRoot, _store.FinalizedSlot,
            _store.SafeTarget);
        _logger.LogInformation("\n{ForkChoiceTree}", tree);
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "Failed to format fork choice tree");
    }
}
```

**Step 2: Verify build**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded

**Step 3: Run full test suite**

Run: `dotnet test Lean.sln -c Release --filter "TestCategory!=Integration"`
Expected: All tests pass

**Step 4: Commit**

```
feat(consensus): log ASCII fork choice tree at end of each slot
```

---

### Task 7: Run format check and final validation

**Step 1: Run dotnet-format check**

Run: `./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor`
Expected: No formatting violations

**Step 2: Fix any formatting issues**

Run: `./.dotnet-tools/dotnet-format Lean.sln --fix-whitespace --exclude vendor`

**Step 3: Run full test suite**

Run: `dotnet test Lean.sln -c Release --filter "TestCategory!=Integration"`
Expected: All tests pass

**Step 4: Final commit if format changes needed**

```
style: fix whitespace formatting
```

---

### Task 8: Integration verification on devnet

**Step 1: Rebuild Docker image**

Run: `docker build -t nlean-local:duty-fix .`

**Step 2: Restart nlean_0 and verify**

- Check ASCII tree appears in logs at each slot
- Verify `curl http://localhost:5053/lean/v0/fork_choice` returns valid JSON
- Open `http://localhost:5053/lean/v0/fork_choice/ui` in browser

**Step 3: Commit all and push devnet3 branch**

```bash
git push origin devnet3
```
