# Spec Test Framework Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a convention-based spec test framework that dynamically discovers and runs leanSpec consensus test vectors, requiring zero code changes when new fixture files are added.

**Architecture:** NUnit `[TestCaseSource]` dynamically scans the leanSpec `fixtures/consensus/` directory at test discovery time. Each fixture kind (fork_choice, state_transition, ssz, verify_signatures) dispatches to a typed runner. Runners deserialize JSON fixtures into C# records, execute the test logic against nlean's consensus layer, and assert expected outcomes. Block labels are tracked in a registry for fork choice check resolution.

**Tech Stack:** NUnit 3, System.Text.Json, Lean.Consensus (existing SSZ + fork choice + state transition)

---

### Task 1: Create SpecTests project scaffold

**Files:**
- Create: `tests/Lean.SpecTests/Lean.SpecTests.csproj`

**Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NUnit" Version="4.3.2" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Lean.Consensus/Lean.Consensus.csproj" />
  </ItemGroup>
</Project>
```

**Step 2: Add project to solution**

Run: `dotnet sln Lean.sln add tests/Lean.SpecTests/Lean.SpecTests.csproj`
Expected: Project added successfully

**Step 3: Verify build**

Run: `dotnet build tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo -v q`
Expected: Build succeeded. 0 Error(s)

**Step 4: Commit**

```bash
git add tests/Lean.SpecTests/Lean.SpecTests.csproj Lean.sln
git commit -m "test(spectest): add Lean.SpecTests project scaffold"
```

---

### Task 2: Define fixture JSON types — common + fork choice

**Files:**
- Create: `tests/Lean.SpecTests/Types/FixtureTypes.cs`

**Step 1: Write types covering the fork choice fixture schema**

These map to the JSON schema from leanSpec `fixtures/consensus/fork_choice/`. Field names use `[JsonPropertyName]` for camelCase mapping.

```csharp
using System.Text.Json.Serialization;

namespace Lean.SpecTests.Types;

// Top-level: each JSON file is Dictionary<string, ForkChoiceTest>
public sealed record ForkChoiceTest(
    [property: JsonPropertyName("network")] string Network,
    [property: JsonPropertyName("leanEnv")] string LeanEnv,
    [property: JsonPropertyName("anchorState")] TestState AnchorState,
    [property: JsonPropertyName("anchorBlock")] TestBlock AnchorBlock,
    [property: JsonPropertyName("steps")] List<ForkChoiceStep> Steps,
    [property: JsonPropertyName("maxSlot")] ulong MaxSlot,
    [property: JsonPropertyName("_info")] TestInfo? Info);

public sealed record StateTransitionTest(
    [property: JsonPropertyName("network")] string Network,
    [property: JsonPropertyName("leanEnv")] string LeanEnv,
    [property: JsonPropertyName("pre")] TestState Pre,
    [property: JsonPropertyName("blocks")] List<TestSignedBlock> Blocks,
    [property: JsonPropertyName("post")] PostState Post,
    [property: JsonPropertyName("_info")] TestInfo? Info);

public sealed record TestState(
    [property: JsonPropertyName("config")] TestConfig Config,
    [property: JsonPropertyName("slot")] ulong Slot,
    [property: JsonPropertyName("latestBlockHeader")] TestBlockHeader LatestBlockHeader,
    [property: JsonPropertyName("latestJustified")] TestCheckpoint LatestJustified,
    [property: JsonPropertyName("latestFinalized")] TestCheckpoint LatestFinalized,
    [property: JsonPropertyName("historicalBlockHashes")] TestDataArray<string> HistoricalBlockHashes,
    [property: JsonPropertyName("justifiedSlots")] TestDataArray<ulong> JustifiedSlots,
    [property: JsonPropertyName("validators")] TestDataArray<TestValidator> Validators,
    [property: JsonPropertyName("justificationsRoots")] TestDataArray<string> JustificationsRoots,
    [property: JsonPropertyName("justificationsValidators")] TestDataArray<bool> JustificationsValidators);

public sealed record TestConfig(
    [property: JsonPropertyName("genesisTime")] ulong GenesisTime);

public sealed record TestBlockHeader(
    [property: JsonPropertyName("slot")] ulong Slot,
    [property: JsonPropertyName("proposerIndex")] ulong ProposerIndex,
    [property: JsonPropertyName("parentRoot")] string ParentRoot,
    [property: JsonPropertyName("stateRoot")] string StateRoot,
    [property: JsonPropertyName("bodyRoot")] string BodyRoot);

public sealed record TestCheckpoint(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("slot")] ulong Slot);

public sealed record TestValidator(
    [property: JsonPropertyName("index")] ulong Index,
    [property: JsonPropertyName("attestationPubkey")] string AttestationPubkey,
    [property: JsonPropertyName("proposalPubkey")] string ProposalPubkey);

public sealed record TestDataArray<T>(
    [property: JsonPropertyName("data")] List<T> Data);

public sealed record TestBlock(
    [property: JsonPropertyName("slot")] ulong Slot,
    [property: JsonPropertyName("proposerIndex")] ulong ProposerIndex,
    [property: JsonPropertyName("parentRoot")] string ParentRoot,
    [property: JsonPropertyName("stateRoot")] string StateRoot,
    [property: JsonPropertyName("body")] TestBlockBody Body);

public sealed record TestSignedBlock(
    [property: JsonPropertyName("message")] TestBlock Message,
    [property: JsonPropertyName("signature")] TestBlockSignature? Signature);

public sealed record TestBlockBody(
    [property: JsonPropertyName("attestations")] TestDataArray<TestAggregatedAttestation>? Attestations);

public sealed record TestAggregatedAttestation(
    [property: JsonPropertyName("aggregationBits")] TestDataArray<bool> AggregationBits,
    [property: JsonPropertyName("data")] TestAttestationData Data);

public sealed record TestAttestationData(
    [property: JsonPropertyName("slot")] ulong Slot,
    [property: JsonPropertyName("head")] TestCheckpoint Head,
    [property: JsonPropertyName("target")] TestCheckpoint Target,
    [property: JsonPropertyName("source")] TestCheckpoint Source);

public sealed record TestBlockSignature(
    [property: JsonPropertyName("proposerSignature")] string? ProposerSignature,
    [property: JsonPropertyName("attestationSignatures")] List<string>? AttestationSignatures);

public sealed record ForkChoiceStep(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("checks")] StoreChecks? Checks,
    [property: JsonPropertyName("stepType")] string StepType,
    [property: JsonPropertyName("block")] TestBlockStepData? Block,
    [property: JsonPropertyName("attestation")] TestAttestationStepData? Attestation,
    [property: JsonPropertyName("gossipAggregatedAttestation")] TestGossipAggregatedAttestationStepData? GossipAggregatedAttestation,
    [property: JsonPropertyName("time")] ulong? Time);

public sealed record TestBlockStepData(
    [property: JsonPropertyName("slot")] ulong Slot,
    [property: JsonPropertyName("proposerIndex")] ulong ProposerIndex,
    [property: JsonPropertyName("parentRoot")] string ParentRoot,
    [property: JsonPropertyName("stateRoot")] string StateRoot,
    [property: JsonPropertyName("body")] TestBlockBody Body,
    [property: JsonPropertyName("blockRootLabel")] string? BlockRootLabel);

public sealed record TestAttestationStepData(
    [property: JsonPropertyName("data")] TestAttestationData Data,
    [property: JsonPropertyName("validatorId")] ulong? ValidatorId);

public sealed record TestGossipAggregatedAttestationStepData(
    [property: JsonPropertyName("data")] TestAttestationData Data,
    [property: JsonPropertyName("aggregationBits")] TestDataArray<bool>? AggregationBits);

public sealed record StoreChecks(
    [property: JsonPropertyName("headSlot")] ulong? HeadSlot,
    [property: JsonPropertyName("headRoot")] string? HeadRoot,
    [property: JsonPropertyName("headRootLabel")] string? HeadRootLabel,
    [property: JsonPropertyName("lexicographicHeadAmong")] List<string>? LexicographicHeadAmong,
    [property: JsonPropertyName("attestationChecks")] List<AttestationCheck>? AttestationChecks,
    [property: JsonPropertyName("attestationTargetSlot")] ulong? AttestationTargetSlot,
    [property: JsonPropertyName("latestJustifiedSlot")] ulong? LatestJustifiedSlot,
    [property: JsonPropertyName("latestJustifiedRoot")] string? LatestJustifiedRoot,
    [property: JsonPropertyName("latestFinalizedSlot")] ulong? LatestFinalizedSlot,
    [property: JsonPropertyName("latestFinalizedRoot")] string? LatestFinalizedRoot,
    [property: JsonPropertyName("safeTarget")] string? SafeTarget,
    [property: JsonPropertyName("time")] ulong? Time);

public sealed record AttestationCheck(
    [property: JsonPropertyName("validator")] ulong Validator,
    [property: JsonPropertyName("attestationSlot")] ulong? AttestationSlot,
    [property: JsonPropertyName("headSlot")] ulong? HeadSlot,
    [property: JsonPropertyName("sourceSlot")] ulong? SourceSlot,
    [property: JsonPropertyName("targetSlot")] ulong? TargetSlot,
    [property: JsonPropertyName("location")] string? Location);

public sealed record PostState(
    [property: JsonPropertyName("slot")] ulong Slot,
    [property: JsonPropertyName("latestBlockHeaderSlot")] ulong LatestBlockHeaderSlot,
    [property: JsonPropertyName("historicalBlockHashesCount")] ulong? HistoricalBlockHashesCount);

public sealed record TestInfo(
    [property: JsonPropertyName("hash")] string? Hash,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("testId")] string? TestId,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("fixtureFormat")] string? FixtureFormat);
```

**Step 2: Verify build**

Run: `dotnet build tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo -v q`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add tests/Lean.SpecTests/Types/FixtureTypes.cs
git commit -m "test(spectest): add fixture JSON types for fork choice and state transition"
```

---

### Task 3: Fixture discovery + dynamic test case source

**Files:**
- Create: `tests/Lean.SpecTests/FixtureDiscovery.cs`

**Step 1: Write the fixture discovery engine**

Scans the leanSpec fixtures directory, discovers JSON files, deserializes them, and yields NUnit `TestCaseData` objects. Path configurable via `LEAN_SPECTEST_FIXTURES` env var.

```csharp
using System.Text.Json;
using NUnit.Framework;

namespace Lean.SpecTests;

public static class FixtureDiscovery
{
    private static readonly string FixturesRoot = ResolveFixturesRoot();

    private static string ResolveFixturesRoot()
    {
        var envPath = Environment.GetEnvironmentVariable("LEAN_SPECTEST_FIXTURES");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
            return envPath;

        // Default: sibling leanSpec checkout relative to nlean repo root
        var repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            var leanSpec = Path.Combine(repoRoot, "..", "leanSpec", "fixtures", "consensus");
            if (Directory.Exists(leanSpec))
                return Path.GetFullPath(leanSpec);
        }

        return string.Empty;
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "Lean.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static bool IsAvailable => !string.IsNullOrEmpty(FixturesRoot) && Directory.Exists(FixturesRoot);

    public static IEnumerable<TestCaseData> DiscoverTests(string fixtureKind)
    {
        if (!IsAvailable)
        {
            yield return new TestCaseData("(no fixtures)", "{}")
                .SetName($"{fixtureKind}: fixtures not found at {FixturesRoot}")
                .Ignore("Fixtures directory not available. Set LEAN_SPECTEST_FIXTURES env var.");
            yield break;
        }

        var kindDir = Path.Combine(FixturesRoot, fixtureKind);
        if (!Directory.Exists(kindDir))
        {
            yield return new TestCaseData("(empty)", "{}")
                .SetName($"{fixtureKind}: no fixtures directory")
                .Ignore($"No {fixtureKind} fixtures found at {kindDir}");
            yield break;
        }

        foreach (var jsonFile in Directory.EnumerateFiles(kindDir, "*.json", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(FixturesRoot, jsonFile);
            var json = File.ReadAllText(jsonFile);

            // Each JSON file is a dictionary: { "testId": { ... test data ... } }
            // Yield one TestCaseData per test entry in the file.
            Dictionary<string, JsonElement>? tests;
            try
            {
                tests = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            }
            catch
            {
                yield return new TestCaseData(relativePath, json)
                    .SetName($"{fixtureKind}/{relativePath}: parse error")
                    .Ignore("Failed to parse fixture JSON");
                continue;
            }

            if (tests is null || tests.Count == 0) continue;

            foreach (var (testId, testElement) in tests)
            {
                var testJson = testElement.GetRawText();
                var shortName = ExtractShortName(testId, relativePath);
                yield return new TestCaseData(testId, testJson)
                    .SetName($"{fixtureKind}/{shortName}");
            }
        }
    }

    private static string ExtractShortName(string testId, string relativePath)
    {
        // testId is like "tests/consensus/devnet/fc/test_fork_choice_head.py::test_name[variant]"
        // Extract just the meaningful part after the last "::" or use the file-relative path
        var colonIdx = testId.LastIndexOf("::", StringComparison.Ordinal);
        if (colonIdx >= 0 && colonIdx + 2 < testId.Length)
            return testId[(colonIdx + 2)..];

        return Path.GetFileNameWithoutExtension(relativePath);
    }
}
```

**Step 2: Verify build**

Run: `dotnet build tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo -v q`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add tests/Lean.SpecTests/FixtureDiscovery.cs
git commit -m "test(spectest): add dynamic fixture discovery with TestCaseSource"
```

---

### Task 4: Runner interface + fork choice runner skeleton

**Files:**
- Create: `tests/Lean.SpecTests/Runners/ISpecTestRunner.cs`
- Create: `tests/Lean.SpecTests/Runners/ForkChoiceRunner.cs`

**Step 1: Define runner interface**

```csharp
namespace Lean.SpecTests.Runners;

public interface ISpecTestRunner
{
    void Run(string testId, string testJson);
}
```

**Step 2: Implement fork choice runner**

Follow ethlambda's pattern: initialize store from anchor, process steps sequentially, validate checks after each step. Use block label registry for check resolution.

```csharp
using System.Text.Json;
using Lean.Consensus;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class ForkChoiceRunner : ISpecTestRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<ForkChoiceTest>(testJson, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize fork choice test: {testId}");

        var validatorCount = (ulong)(test.AnchorState.Validators?.Data?.Count ?? 0);
        var config = new ConsensusConfig { InitialValidatorCount = validatorCount };

        var store = new ProtoArrayForkChoiceStore(config);

        // Block label registry: maps label -> block root (for check resolution)
        var blockRegistry = new Dictionary<string, Bytes32>();

        // Process anchor block
        var anchorBlock = ConvertBlock(test.AnchorBlock);
        var anchorRoot = new Bytes32(anchorBlock.HashTreeRoot());
        if (!string.IsNullOrEmpty("genesis"))
        {
            blockRegistry["genesis"] = anchorRoot;
        }

        for (var stepIdx = 0; stepIdx < test.Steps.Count; stepIdx++)
        {
            var step = test.Steps[stepIdx];

            switch (step.StepType)
            {
                case "block":
                    ProcessBlockStep(store, step, blockRegistry, config, test.AnchorState.Config.GenesisTime);
                    break;

                case "tick":
                    if (step.Time.HasValue)
                    {
                        store.OnTick(step.Time.Value);
                    }
                    break;

                case "attestation":
                    ProcessAttestationStep(store, step);
                    break;

                case "gossipAggregatedAttestation":
                    // TODO: implement when needed
                    break;

                default:
                    Assert.Ignore($"Unsupported step type: {step.StepType}");
                    break;
            }

            if (step.Checks is not null)
            {
                ValidateChecks(store, step.Checks, stepIdx, blockRegistry);
            }
        }
    }

    private static void ProcessBlockStep(
        ProtoArrayForkChoiceStore store,
        ForkChoiceStep step,
        Dictionary<string, Bytes32> blockRegistry,
        ConsensusConfig config,
        ulong genesisTime)
    {
        var blockData = step.Block
            ?? throw new InvalidOperationException("Block step missing block data");

        var block = ConvertBlockStepData(blockData);
        var blockRoot = new Bytes32(block.HashTreeRoot());

        if (!string.IsNullOrEmpty(blockData.BlockRootLabel))
        {
            blockRegistry[blockData.BlockRootLabel] = blockRoot;
        }

        // Build signed block with empty signatures (spec tests skip verification)
        var signedBlock = new SignedBlock(block, BlockSignature.Empty());

        // Compute canonical checkpoints from store
        var canonicalJustified = new Checkpoint(
            new Bytes32(Convert.FromHexString(RemoveHexPrefix(step.Block!.ParentRoot))),
            new Slot(store.JustifiedSlot));
        var canonicalFinalized = new Checkpoint(store.FinalizedRoot, new Slot(store.FinalizedSlot));

        var result = store.OnBlock(signedBlock, canonicalJustified, canonicalFinalized, store.ValidatorCount);

        if (step.Valid && !result.Accepted)
        {
            Assert.Fail($"Block step expected success but got rejection: {result.Reason}");
        }
        else if (!step.Valid && result.Accepted)
        {
            Assert.Fail("Block step expected failure but block was accepted");
        }
    }

    private static void ProcessAttestationStep(ProtoArrayForkChoiceStore store, ForkChoiceStep step)
    {
        var attData = step.Attestation
            ?? throw new InvalidOperationException("Attestation step missing attestation data");

        var attestationData = ConvertAttestationData(attData.Data);
        var attestation = new SignedAttestation(
            attData.ValidatorId ?? 0,
            attestationData,
            XmssSignature.Empty());

        store.TryOnAttestation(attestation, out _);
    }

    private static void ValidateChecks(
        ProtoArrayForkChoiceStore store,
        StoreChecks checks,
        int stepIdx,
        Dictionary<string, Bytes32> blockRegistry)
    {
        var context = $"step {stepIdx}";

        if (checks.HeadSlot.HasValue)
        {
            Assert.That(store.HeadSlot, Is.EqualTo(checks.HeadSlot.Value),
                $"{context}: headSlot mismatch");
        }

        if (checks.HeadRoot is not null)
        {
            var expectedRoot = ParseHexRoot(checks.HeadRoot);
            Assert.That(store.HeadRoot, Is.EqualTo(expectedRoot),
                $"{context}: headRoot mismatch");
        }

        if (checks.HeadRootLabel is not null && blockRegistry.TryGetValue(checks.HeadRootLabel, out var labelRoot))
        {
            Assert.That(store.HeadRoot, Is.EqualTo(labelRoot),
                $"{context}: headRootLabel '{checks.HeadRootLabel}' mismatch");
        }

        if (checks.LatestJustifiedSlot.HasValue)
        {
            Assert.That(store.JustifiedSlot, Is.EqualTo(checks.LatestJustifiedSlot.Value),
                $"{context}: latestJustifiedSlot mismatch");
        }

        if (checks.LatestFinalizedSlot.HasValue)
        {
            Assert.That(store.FinalizedSlot, Is.EqualTo(checks.LatestFinalizedSlot.Value),
                $"{context}: latestFinalizedSlot mismatch");
        }

        if (checks.LexicographicHeadAmong is { Count: > 0 })
        {
            var possibleRoots = checks.LexicographicHeadAmong
                .Where(blockRegistry.ContainsKey)
                .Select(label => blockRegistry[label])
                .ToList();

            Assert.That(possibleRoots, Does.Contain(store.HeadRoot),
                $"{context}: head not among lexicographic candidates");
        }
    }

    // --- Conversion helpers ---

    private static Block ConvertBlock(TestBlock tb) => new(
        new Slot(tb.Slot),
        tb.ProposerIndex,
        new Bytes32(ParseHex(tb.ParentRoot)),
        new Bytes32(ParseHex(tb.StateRoot)),
        ConvertBlockBody(tb.Body));

    private static Block ConvertBlockStepData(TestBlockStepData tb) => new(
        new Slot(tb.Slot),
        tb.ProposerIndex,
        new Bytes32(ParseHex(tb.ParentRoot)),
        new Bytes32(ParseHex(tb.StateRoot)),
        ConvertBlockBody(tb.Body));

    private static BlockBody ConvertBlockBody(TestBlockBody? body)
    {
        if (body?.Attestations?.Data is null or { Count: 0 })
            return BlockBody.Empty();

        var attestations = body.Attestations.Data
            .Select(a => new AggregatedAttestation(
                a.AggregationBits.Data,
                ConvertAttestationData(a.Data)))
            .ToList();

        return new BlockBody(attestations);
    }

    private static AttestationData ConvertAttestationData(TestAttestationData td) => new(
        new Slot(td.Slot),
        new Checkpoint(new Bytes32(ParseHex(td.Head.Root)), new Slot(td.Head.Slot)),
        new Checkpoint(new Bytes32(ParseHex(td.Target.Root)), new Slot(td.Target.Slot)),
        new Checkpoint(new Bytes32(ParseHex(td.Source.Root)), new Slot(td.Source.Slot)));

    private static Bytes32 ParseHexRoot(string hex) => new(ParseHex(hex));

    private static byte[] ParseHex(string hex) => Convert.FromHexString(RemoveHexPrefix(hex));

    private static string RemoveHexPrefix(string hex) =>
        hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
}
```

**Note:** This runner will need refinement as we test against actual fixtures. The conversion helpers (`Block`, `BlockBody`, `AttestationData`, etc.) must match nlean's existing type constructors exactly. Adjust constructor signatures based on compile errors.

**Step 3: Verify build**

Run: `dotnet build tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo -v q`
Expected: Build succeeded (may need to adjust type constructors)

**Step 4: Commit**

```bash
git add tests/Lean.SpecTests/Runners/
git commit -m "test(spectest): add fork choice runner with step processing and check validation"
```

---

### Task 5: NUnit test class with dynamic TestCaseSource

**Files:**
- Create: `tests/Lean.SpecTests/ForkChoiceSpecTests.cs`

**Step 1: Write the test class**

```csharp
using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class ForkChoiceSpecTests
{
    private static readonly ForkChoiceRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("fork_choice");

    [TestCaseSource(nameof(LoadTests))]
    public void RunForkChoiceTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
```

**Step 2: Run tests to verify discovery works**

Run: `dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --filter "Category=SpecTest" --nologo -v n 2>&1 | tail -20`
Expected: Tests are discovered from leanSpec fixtures (pass, fail, or ignore — discovery itself works)

**Step 3: Commit**

```bash
git add tests/Lean.SpecTests/ForkChoiceSpecTests.cs
git commit -m "test(spectest): add ForkChoiceSpecTests with dynamic TestCaseSource discovery"
```

---

### Task 6: State transition runner + test class

**Files:**
- Create: `tests/Lean.SpecTests/Runners/StateTransitionRunner.cs`
- Create: `tests/Lean.SpecTests/StateTransitionSpecTests.cs`

**Step 1: Implement state transition runner**

```csharp
using System.Text.Json;
using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class StateTransitionRunner : ISpecTestRunner
{
    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<StateTransitionTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize: {testId}");

        var validatorCount = (ulong)(test.Pre.Validators?.Data?.Count ?? 0);
        var config = new ConsensusConfig { InitialValidatorCount = validatorCount };
        var chainTransition = new ChainStateTransition(config);

        // Create initial state from pre
        var state = chainTransition.CreateGenesisState(validatorCount);

        // Process each block
        foreach (var testBlock in test.Blocks)
        {
            var block = ConvertTestBlock(testBlock);

            if (!chainTransition.TryComputeStateRoot(state, block, out _, out var postState, out var reason))
            {
                Assert.Fail($"State transition failed for block at slot {block.Slot.Value}: {reason}");
                return;
            }

            state = postState!;
        }

        // Validate post state
        Assert.That(state.Slot, Is.EqualTo(test.Post.Slot),
            "Post state slot mismatch");
        Assert.That(state.LatestBlockHeader.Slot.Value, Is.EqualTo(test.Post.LatestBlockHeaderSlot),
            "Post state latest block header slot mismatch");
    }

    private static Block ConvertTestBlock(TestSignedBlock tsb)
    {
        var tb = tsb.Message;
        return new Block(
            new Slot(tb.Slot),
            tb.ProposerIndex,
            new Bytes32(ParseHex(tb.ParentRoot)),
            new Bytes32(ParseHex(tb.StateRoot)),
            ConvertBlockBody(tb.Body));
    }

    private static BlockBody ConvertBlockBody(TestBlockBody? body)
    {
        if (body?.Attestations?.Data is null or { Count: 0 })
            return BlockBody.Empty();

        // Convert attestations — same pattern as ForkChoiceRunner
        var attestations = body.Attestations.Data
            .Select(a => new AggregatedAttestation(
                a.AggregationBits.Data,
                new AttestationData(
                    new Slot(a.Data.Slot),
                    new Checkpoint(new Bytes32(ParseHex(a.Data.Head.Root)), new Slot(a.Data.Head.Slot)),
                    new Checkpoint(new Bytes32(ParseHex(a.Data.Target.Root)), new Slot(a.Data.Target.Slot)),
                    new Checkpoint(new Bytes32(ParseHex(a.Data.Source.Root)), new Slot(a.Data.Source.Slot)))))
            .ToList();

        return new BlockBody(attestations);
    }

    private static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
}
```

**Step 2: Write test class**

```csharp
using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class StateTransitionSpecTests
{
    private static readonly StateTransitionRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("state_transition");

    [TestCaseSource(nameof(LoadTests))]
    public void RunStateTransitionTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
```

**Step 3: Verify build + run**

Run: `dotnet build tests/Lean.SpecTests/Lean.SpecTests.csproj -c Release --nologo -v q`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add tests/Lean.SpecTests/Runners/StateTransitionRunner.cs tests/Lean.SpecTests/StateTransitionSpecTests.cs
git commit -m "test(spectest): add state transition runner and test class"
```

---

### Task 7: CI integration

**Files:**
- Modify: `.github/workflows/ci.yml`

**Step 1: Add spectest job to CI**

Add a new job that runs spec tests. It needs leanSpec fixtures checked out alongside nlean.

```yaml
  spec-tests:
    runs-on: ubuntu-latest
    if: github.event_name == 'push' || github.event_name == 'pull_request'
    steps:
      - name: Checkout nlean
        uses: actions/checkout@v4

      - name: Checkout leanSpec fixtures
        uses: actions/checkout@v4
        with:
          repository: leanEthereum/leanSpec
          path: leanSpec
          sparse-checkout: fixtures/consensus

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Run spec tests
        env:
          LEAN_SPECTEST_FIXTURES: ${{ github.workspace }}/leanSpec/fixtures/consensus
        run: >
          dotnet test tests/Lean.SpecTests/Lean.SpecTests.csproj
          -c Release
          --filter "Category=SpecTest"
          --nologo
```

**Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add spec test job with leanSpec fixture checkout"
```

---

### Task 8: Iterate — fix compilation, run first fixtures, handle edge cases

**This is an iterative task.** After the scaffold compiles and runs against real fixtures, expect:

1. **Type constructor mismatches** — nlean's `Block`, `BlockBody`, `AttestationData` constructors may differ from what the runner assumes. Fix by reading `src/Lean.Consensus/Types/` and adjusting.

2. **Missing conversions** — some fixture fields may need SSZ-level conversion (e.g., `TestState` → nlean `State`). Add conversion methods as needed.

3. **Skip list** — some tests may test features nlean doesn't implement yet. Add `Assert.Ignore()` with clear reason.

4. **Fixture format variations** — different fixture kinds may use slightly different schemas. Extend `FixtureTypes.cs` as you encounter new fields.

**The key principle: new fixture JSON files added to leanSpec should automatically appear as new test cases without any code changes in nlean.**

---

## Extensibility Guide

**Adding a new fixture kind** (e.g., `ssz` or `verify_signatures`):

1. Create `tests/Lean.SpecTests/Runners/SszRunner.cs` implementing `ISpecTestRunner`
2. Create `tests/Lean.SpecTests/SszSpecTests.cs` with `[TestCaseSource]` pointing to `FixtureDiscovery.DiscoverTests("ssz")`
3. Add any new types to `FixtureTypes.cs` if the schema differs
4. **No changes needed** to `FixtureDiscovery.cs` — it's kind-agnostic

**Adding new fixtures**: Just add `.json` files to `leanSpec/fixtures/consensus/{kind}/` — they're auto-discovered.
