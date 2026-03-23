# devnet4 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement leanSpec devnet4 changes on the nlean `main` branch: separate attestation/proposal keys, removal of `BlockWithAttestation`, simplified store mappings, and recursive aggregation API shape.

**Architecture:** Replace the single-key validator model with dual XMSS key pairs (attestation + proposal). Flatten block gossip from `SignedBlockWithAttestation(BlockWithAttestation(...))` to `SignedBlock(Block, BlockSignatures)`. Rekey store mappings from per-validator `(validatorId, dataRootHex)` to per-`AttestationData` with sets. Proposer signs `HashTreeRoot(block)` with proposal key; all validators attest (no proposer skip).

**Tech Stack:** C# (.NET 10+), NUnit, YamlDotNet, Rust FFI (lean-crypto-ffi)

**References:**
- Design doc: `docs/plans/2026-03-23-devnet4-design.md`
- leanSpec commits: `be853180d21aa36d6401b8c1541aa6fcaad5008d` → HEAD
- leanSpec PR #436: "simplify store mappings"

---

## Affected Files Summary

**Types (Lean.Consensus):**
- `src/Lean.Consensus/Types/StateTypes.cs` — Validator record
- `src/Lean.Consensus/Types/BlockTypes.cs` — Block types
- `src/Lean.Consensus/Types/SignatureTypes.cs` — AggregatedSignatureProof
- `src/Lean.Consensus/Types/SszEncoding.cs` — Encode methods
- `src/Lean.Consensus/Types/SszDecoding.cs` — Decode methods
- `src/Lean.Consensus/Types/BlockGossipDecodeResult.cs` — Result type

**Consensus logic:**
- `src/Lean.Consensus/ConsensusConfig.cs` — Genesis validator config
- `src/Lean.Consensus/ChainStateTransition.cs` — Genesis state builder
- `src/Lean.Consensus/ForkChoice/ProtoArrayForkChoiceStore.cs` — Store mappings, OnBlock, OnAttestation
- `src/Lean.Consensus/ConsensusServiceV2.cs` — Interface wrapper
- `src/Lean.Consensus/IConsensusService.cs` — Service interface
- `src/Lean.Consensus/SignedBlockWithAttestationGossipDecoder.cs` — Block gossip decoder (rename + refactor)

**Sync:**
- `src/Lean.Consensus/Sync/BackfillSync.cs`
- `src/Lean.Consensus/Sync/HeadSync.cs`
- `src/Lean.Consensus/Sync/IBlockProcessor.cs`
- `src/Lean.Consensus/Sync/INetworkRequester.cs`
- `src/Lean.Consensus/Sync/ISyncService.cs`
- `src/Lean.Consensus/Sync/Libp2pNetworkRequester.cs`
- `src/Lean.Consensus/Sync/NewBlockCache.cs`
- `src/Lean.Consensus/Sync/ProtoArrayBlockProcessor.cs`
- `src/Lean.Consensus/Sync/SyncService.cs`
- `src/Lean.Consensus/Sync/CheckpointSync.cs`

**Validator:**
- `src/Lean.Validator/ValidatorService.cs` — Dual keys, block proposal, attestation
- `src/Lean.Validator/ValidatorDutyConfig.cs` — Dual key config

**Node:**
- `src/Lean.Node/Configuration/LeanChainConfig.cs` — YAML genesis format
- `src/Lean.Node/NodeApp.cs` — Wiring

**Tests:**
- `tests/Lean.Consensus.Tests/BlockGossipDecoderTests.cs`
- `tests/Lean.Consensus.Tests/ConsensusMultiNodeFinalizationV2Tests.cs`
- `tests/Lean.Consensus.Tests/SszStateRoundTripTests.cs`
- `tests/Lean.Consensus.Tests/StateByRootStoreTests.cs`
- `tests/Lean.Consensus.Tests/Sync/CheckpointSyncTests.cs`
- `tests/Lean.Validator.Tests/` — various

---

### Task 1: Switch to main branch and create devnet4 feature branch

**Step 1: Create feature branch from main**

```bash
git checkout main
git pull origin main
git checkout -b feat/devnet4
```

**Step 2: Verify build succeeds on main**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded.

**Step 3: Commit (empty branch marker)**

```bash
git commit --allow-empty -m "feat: begin devnet4 implementation"
```

---

### Task 2: Validator dual-key type changes

**Files:**
- Modify: `src/Lean.Consensus/Types/StateTypes.cs`
- Modify: `src/Lean.Consensus/Types/SszEncoding.cs` (Validator encode + ValidatorLength constant)
- Modify: `src/Lean.Consensus/Types/SszDecoding.cs` (DecodeValidatorList)

**Step 1: Update Validator record in StateTypes.cs**

Change `Validator(Bytes52 Pubkey, ulong Index)` to:

```csharp
public sealed record Validator(Bytes52 AttestationPubkey, Bytes52 ProposalPubkey, ulong Index)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            SszInterop.HashBytesVector(AttestationPubkey.AsSpan()),
            SszInterop.HashBytesVector(ProposalPubkey.AsSpan()),
            SszInterop.HashUInt64(Index));
    }
}
```

**Step 2: Update ValidatorLength constant in SszEncoding.cs**

Change line 16:
```csharp
// Before
public const int ValidatorLength = Bytes52Length + UInt64Length;
// After
public const int ValidatorLength = Bytes52Length + Bytes52Length + UInt64Length;
```

**Step 3: Update Encode(Validator) in SszEncoding.cs**

```csharp
public static byte[] Encode(Validator value)
{
    var buffer = new byte[ValidatorLength];
    value.AttestationPubkey.AsSpan().CopyTo(buffer.AsSpan(0, Bytes52Length));
    value.ProposalPubkey.AsSpan().CopyTo(buffer.AsSpan(Bytes52Length, Bytes52Length));
    Ssz.Encode(buffer.AsSpan(Bytes52Length + Bytes52Length, UInt64Length), value.Index);
    return buffer;
}
```

**Step 4: Update DecodeValidatorList in SszDecoding.cs**

```csharp
private static List<Validator> DecodeValidatorList(ReadOnlySpan<byte> data)
{
    var count = data.Length / SszEncoding.ValidatorLength;
    var result = new List<Validator>(count);
    for (var i = 0; i < count; i++)
    {
        var offset = i * SszEncoding.ValidatorLength;
        var attestationPubkey = new Bytes52(data.Slice(offset, SszEncoding.Bytes52Length).ToArray());
        var proposalPubkey = new Bytes52(data.Slice(offset + SszEncoding.Bytes52Length, SszEncoding.Bytes52Length).ToArray());
        var index = BinaryPrimitives.ReadUInt64LittleEndian(
            data.Slice(offset + SszEncoding.Bytes52Length + SszEncoding.Bytes52Length, SszEncoding.UInt64Length));
        result.Add(new Validator(attestationPubkey, proposalPubkey, index));
    }
    return result;
}
```

**Step 5: Fix all .Pubkey references**

Search `git grep "\.Pubkey\b" -- '*.cs'` and update each to `AttestationPubkey` or `ProposalPubkey` as appropriate:
- `src/Lean.Consensus/Sync/CheckpointSync.cs` — uses pubkey for state display → `AttestationPubkey`
- `tests/Lean.Consensus.Tests/SszStateRoundTripTests.cs` — test validator construction
- `tests/Lean.Consensus.Tests/StateByRootStoreTests.cs` — test validator construction
- `tests/Lean.Consensus.Tests/Sync/CheckpointSyncTests.cs` — test validator construction

For test files constructing `new Validator(pubkey, index)`, change to `new Validator(pubkey, pubkey, index)` (same key for both in tests, unless specific test needs different).

**Step 6: Build to verify compile**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded (may have warnings about unused fields).

**Step 7: Commit**

```bash
git add -A && git commit -m "feat(types): add dual attestation/proposal pubkeys to Validator"
```

---

### Task 3: Block type refactoring — remove BlockWithAttestation, add SignedBlock

**Files:**
- Modify: `src/Lean.Consensus/Types/BlockTypes.cs`
- Modify: `src/Lean.Consensus/Types/BlockGossipDecodeResult.cs`
- Modify: `src/Lean.Consensus/Types/SszEncoding.cs`
- Rename+Modify: `src/Lean.Consensus/SignedBlockWithAttestationGossipDecoder.cs` → `src/Lean.Consensus/SignedBlockGossipDecoder.cs`

**Step 1: Update BlockTypes.cs**

Remove `BlockWithAttestation` and `SignedBlockWithAttestation`. Add `SignedBlock`:

```csharp
// REMOVE these two records:
// public sealed record BlockWithAttestation(Block Block, Attestation ProposerAttestation) { ... }
// public sealed record SignedBlockWithAttestation(BlockWithAttestation Message, BlockSignatures Signature) { ... }

// ADD:
public sealed record SignedBlock(Block Block, BlockSignatures Signature)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            Block.HashTreeRoot(),
            Signature.HashTreeRoot());
    }
}
```

**Step 2: Update SszEncoding.cs**

Remove `Encode(BlockWithAttestation)` and `Encode(SignedBlockWithAttestation)`. Add:

```csharp
public static byte[] Encode(SignedBlock value)
{
    var blockBytes = Encode(value.Block);
    var signatureBytes = Encode(value.Signature);
    var fixedSize = UInt32Length + UInt32Length;
    var buffer = new byte[fixedSize + blockBytes.Length + signatureBytes.Length];
    WriteOffset(buffer, 0, fixedSize);
    WriteOffset(buffer, UInt32Length, fixedSize + blockBytes.Length);
    blockBytes.CopyTo(buffer.AsSpan(fixedSize));
    signatureBytes.CopyTo(buffer.AsSpan(fixedSize + blockBytes.Length));
    return buffer;
}
```

**Step 3: Update BlockGossipDecodeResult.cs**

Change `SignedBlockWithAttestation` → `SignedBlock` in the result type.

**Step 4: Refactor gossip decoder**

Rename `SignedBlockWithAttestationGossipDecoder.cs` → `SignedBlockGossipDecoder.cs`. Refactor:
- Class name: `SignedBlockGossipDecoder`
- Remove `TryDecodeBlockWithAttestation` — no longer needed
- Remove `BlockWithAttestationFixedLength` constant
- `TryDecodeSignedBlockWithAttestation` → `TryDecodeSignedBlock` — decodes `SignedBlock(Block, BlockSignatures)` directly
- The SSZ layout for `SignedBlock` is: `[offset_block(4), offset_signature(4) | block_data | signature_data]` — same as `SignedBlockWithAttestation` outer level, but inner message is `Block` not `BlockWithAttestation`

**Step 5: Fix all 20 files referencing old types**

Every file in the affected list from `git grep -l "SignedBlockWithAttestation\|BlockWithAttestation"`:
- Replace `SignedBlockWithAttestation` → `SignedBlock`
- Replace `BlockWithAttestation` → removed (access `.Block` directly)
- Replace `.Message.Block` → `.Block`
- Replace `.Message.ProposerAttestation` → removed
- Replace `SignedBlockWithAttestationGossipDecoder` → `SignedBlockGossipDecoder`
- Replace `_signedBlockDecoder` type annotation

Files to update:
1. `src/Lean.Consensus/ConsensusServiceV2.cs`
2. `src/Lean.Consensus/ForkChoice/ProtoArrayForkChoiceStore.cs`
3. `src/Lean.Consensus/IConsensusService.cs`
4. `src/Lean.Consensus/Sync/BackfillSync.cs`
5. `src/Lean.Consensus/Sync/HeadSync.cs`
6. `src/Lean.Consensus/Sync/IBlockProcessor.cs`
7. `src/Lean.Consensus/Sync/INetworkRequester.cs`
8. `src/Lean.Consensus/Sync/ISyncService.cs`
9. `src/Lean.Consensus/Sync/Libp2pNetworkRequester.cs`
10. `src/Lean.Consensus/Sync/NewBlockCache.cs`
11. `src/Lean.Consensus/Sync/ProtoArrayBlockProcessor.cs`
12. `src/Lean.Consensus/Sync/SyncService.cs`
13. `src/Lean.Node/NodeApp.cs`
14. `src/Lean.Validator/ValidatorService.cs`
15. `tests/Lean.Consensus.Tests/BlockGossipDecoderTests.cs`
16. `tests/Lean.Consensus.Tests/ConsensusMultiNodeFinalizationV2Tests.cs`

**Step 6: Build to verify**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded.

**Step 7: Commit**

```bash
git add -A && git commit -m "feat(types): replace BlockWithAttestation with SignedBlock"
```

---

### Task 4: Genesis config — dual pubkey YAML format

**Files:**
- Modify: `src/Lean.Node/Configuration/LeanChainConfig.cs`
- Modify: `src/Lean.Consensus/ConsensusConfig.cs`
- Modify: `src/Lean.Consensus/ChainStateTransition.cs`
- Modify: `src/Lean.Node/NodeApp.cs`
- Modify: `src/Lean.Validator/ValidatorDutyConfig.cs`

**Step 1: Add GenesisValidatorEntry class to LeanChainConfig.cs**

```csharp
public sealed class GenesisValidatorEntry
{
    [YamlMember(Alias = "attestation_pubkey")]
    public string AttestationPubkey { get; set; } = string.Empty;

    [YamlMember(Alias = "proposal_pubkey")]
    public string ProposalPubkey { get; set; } = string.Empty;
}
```

Change `LeanChainConfig.GenesisValidators` from `List<string>?` to `List<GenesisValidatorEntry>?`.

**Step 2: Update ConsensusConfig.cs**

```csharp
// Before
public IReadOnlyList<string> GenesisValidatorPublicKeys { get; set; } = Array.Empty<string>();
// After
public IReadOnlyList<(string AttestationPubkey, string ProposalPubkey)> GenesisValidatorKeys { get; set; }
    = Array.Empty<(string, string)>();
```

**Step 3: Update NodeApp.cs ApplyInitialValidatorCount**

Where it copies `chainConfig.GenesisValidators` into `options.Consensus.GenesisValidatorPublicKeys`, change to:

```csharp
options.Consensus.GenesisValidatorKeys = chainConfig.GenesisValidators
    .Select(v => (v.AttestationPubkey, v.ProposalPubkey))
    .ToList();
```

**Step 4: Update ChainStateTransition.BuildGenesisValidators()**

```csharp
private IReadOnlyList<Validator> BuildGenesisValidators(ulong initialValidatorCount)
{
    var validators = new List<Validator>();
    for (var index = 0; index < _config.GenesisValidatorKeys.Count; index++)
    {
        var (attestKeyHex, proposalKeyHex) = _config.GenesisValidatorKeys[index];

        if (!TryParseHexBytes(attestKeyHex, out var attestBytes) || attestBytes.Length != SszEncoding.Bytes52Length)
            attestBytes = new byte[SszEncoding.Bytes52Length];

        if (!TryParseHexBytes(proposalKeyHex, out var proposalBytes) || proposalBytes.Length != SszEncoding.Bytes52Length)
            proposalBytes = new byte[SszEncoding.Bytes52Length];

        validators.Add(new Validator(new Bytes52(attestBytes), new Bytes52(proposalBytes), (ulong)index));
    }

    var targetCount = validators.Count > 0
        ? (ulong)validators.Count
        : Math.Max(1UL, initialValidatorCount);
    while ((ulong)validators.Count < targetCount)
    {
        validators.Add(new Validator(Bytes52.Zero(), Bytes52.Zero(), (ulong)validators.Count));
    }

    return validators;
}
```

**Step 5: Update ValidatorDutyConfig.cs**

```csharp
// Before
public IReadOnlyList<string> GenesisValidatorPublicKeys { get; init; } = Array.Empty<string>();
// After
public IReadOnlyList<(string AttestationPubkey, string ProposalPubkey)> GenesisValidatorKeys { get; init; }
    = Array.Empty<(string, string)>();
```

**Step 6: Update NodeApp.cs BuildValidatorDutyConfig**

Update wiring from old `GenesisValidatorPublicKeys` to new `GenesisValidatorKeys`.

**Step 7: Fix all remaining references to `GenesisValidatorPublicKeys`**

Search and replace across codebase.

**Step 8: Build to verify**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded.

**Step 9: Commit**

```bash
git add -A && git commit -m "feat(genesis): support dual pubkey YAML format for devnet4 validators"
```

---

### Task 5: Store mappings refactor (PR #436 port)

**Files:**
- Modify: `src/Lean.Consensus/ForkChoice/ProtoArrayForkChoiceStore.cs`
- Modify: `src/Lean.Consensus/IConsensusService.cs`
- Modify: `src/Lean.Consensus/ConsensusServiceV2.cs`

**Step 1: Add GossipSignatureEntry type**

Add to `ProtoArrayForkChoiceStore.cs` (or a new file `src/Lean.Consensus/ForkChoice/GossipSignatureEntry.cs`):

```csharp
public readonly record struct GossipSignatureEntry(ulong ValidatorId, XmssSignature Signature);
```

**Step 2: Replace store mapping fields**

```csharp
// REMOVE:
// private readonly Dictionary<(ulong, string), XmssSignature> _gossipSignatures = new();
// private readonly Dictionary<string, AttestationData> _attestationDataByRoot = new(StringComparer.Ordinal);
// private readonly Dictionary<string, List<AggregatedSignatureProof>> _newAggregatedPayloads = new(StringComparer.Ordinal);
// private readonly Dictionary<string, List<AggregatedSignatureProof>> _knownAggregatedPayloads = new(StringComparer.Ordinal);

// ADD:
private readonly Dictionary<string, (AttestationData Data, HashSet<GossipSignatureEntry> Entries)> _gossipSignatures = new(StringComparer.Ordinal);
private readonly Dictionary<string, (AttestationData Data, HashSet<AggregatedSignatureProof> Proofs)> _newAggregatedPayloads = new(StringComparer.Ordinal);
private readonly Dictionary<string, (AttestationData Data, HashSet<AggregatedSignatureProof> Proofs)> _knownAggregatedPayloads = new(StringComparer.Ordinal);
```

Note: We use `string` (dataRootHex) as dictionary key for performance, but store the `AttestationData` alongside. This avoids needing a custom `AttestationData` equality comparer while still removing the separate `_attestationDataByRoot`.

**Step 3: Update OnGossipSignature**

```csharp
public void OnGossipSignature(ulong validatorId, AttestationData data, XmssSignature signature)
{
    var dataRootKey = ToDataRootKey(data);
    if (!_gossipSignatures.TryGetValue(dataRootKey, out var entry))
    {
        entry = (data, new HashSet<GossipSignatureEntry>());
        _gossipSignatures[dataRootKey] = entry;
    }
    entry.Entries.Add(new GossipSignatureEntry(validatorId, signature));
}
```

**Step 4: Update TryOnAttestation**

Replace `_attestationDataByRoot[dataRootKey] = attestation.Message;` and `_gossipSignatures[(attestation.ValidatorId, dataRootKey)] = attestation.Signature;` with:

```csharp
if (storeSignature)
{
    var dataRootKey = ToDataRootKey(attestation.Message);
    if (!_gossipSignatures.TryGetValue(dataRootKey, out var entry))
    {
        entry = (attestation.Message, new HashSet<GossipSignatureEntry>());
        _gossipSignatures[dataRootKey] = entry;
    }
    entry.Entries.Add(new GossipSignatureEntry(attestation.ValidatorId, attestation.Signature));
}
```

**Step 5: Update OnBlock**

- Remove proposer attestation handling (no `proposerAttestation` field in `SignedBlock`)
- Store block attestation proofs into `_knownAggregatedPayloads` keyed by dataRootKey with `AttestationData`
- Remove `_attestationDataByRoot` population
- Remove proposer signature injection into `_gossipSignatures`

**Step 6: Update TryOnGossipAggregatedAttestation**

Store into `_newAggregatedPayloads` keyed by dataRootKey.

**Step 7: Update CollectAttestationsForAggregation**

Iterate `_gossipSignatures` by key → collect entries, sorted by validatorId.

**Step 8: Update AcceptNewAttestations**

- Unpack `_newAggregatedPayloads` using stored `AttestationData` (no `_attestationDataByRoot` lookup)
- Merge into `_knownAggregatedPayloads` using set union

**Step 9: Update PruneAttestationDataOlderThan**

- Remove `_attestationDataByRoot` pruning
- Prune all three maps by checking stored `AttestationData.Target.Slot`

**Step 10: Update GetKnownAggregatedPayloadsForBlock**

Use greedy set-cover for proof selection (sort by participant count, pick overlapping).

**Step 11: Update UpdateSafeTarget**

Merge new + known payloads using `AttestationData` from stored values.

**Step 12: Update IConsensusService interface**

- `TryApplyLocalBlock(SignedBlock, ...)` instead of `SignedBlockWithAttestation`
- `CollectAttestationsForAggregation()` return type may need adjustment

**Step 13: Build to verify**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded.

**Step 14: Commit**

```bash
git add -A && git commit -m "refactor(store): rekey mappings by AttestationData, remove attestation_data_by_root"
```

---

### Task 6: Validator service — dual keys and proposal flow

**Files:**
- Modify: `src/Lean.Validator/ValidatorService.cs`
- Modify: `src/Lean.Validator/ValidatorDutyConfig.cs`

**Step 1: Add ValidatorKeyMaterial record**

```csharp
public sealed record ValidatorKeyMaterial(
    byte[] AttestationPublicKey,
    byte[] AttestationSecretKey,
    byte[] ProposalPublicKey,
    byte[] ProposalSecretKey);
```

**Step 2: Change _localValidators type**

```csharp
// Before
private readonly Dictionary<ulong, (byte[] PublicKey, byte[] SecretKey)> _localValidators = new();
// After
private readonly Dictionary<ulong, ValidatorKeyMaterial> _localValidators = new();
```

**Step 3: Update InitializeValidatorKeyMaterial**

Generate two key pairs per validator:
```csharp
var attestKeyPair = _leanSig.GenerateKeyPair(activationEpoch, numActiveEpochs);
var proposalKeyPair = _leanSig.GenerateKeyPair(activationEpoch, numActiveEpochs);
_localValidators[validatorId] = new ValidatorKeyMaterial(
    attestKeyPair.PublicKey, attestKeyPair.SecretKey,
    proposalKeyPair.PublicKey, proposalKeyPair.SecretKey);
```

Key file loading: try `validator_{idx}_attest_pk.ssz` first, fall back to `validator_{idx}_pk.ssz` (backward compat).

**Step 4: Update TryPublishProposerBlockAsync**

- Build `Block` as before
- Sign `HashTreeRoot(block)` with **proposal** secret key:
  ```csharp
  var keys = _localValidators[validatorId];
  var blockRoot = block.HashTreeRoot();
  var proposerSig = _leanSig.Sign(keys.ProposalSecretKey, ToSignatureEpoch(slot), blockRoot);
  ```
- Create `SignedBlock(block, new BlockSignatures(aggregatedProofs, XmssSignature.FromBytes(proposerSig)))`
- Remove all proposer attestation construction (no `proposerAttestation`, no `BlockWithAttestation`)

**Step 5: Remove proposer skip in attestation production**

In the interval 1 handler, remove:
```csharp
// REMOVE this condition:
if (validatorId == slotProposer && slotProposer != 0)
{
    continue;
}
```

All validators now attest at interval 1, including the slot's proposer.

**Step 6: Update attestation signing to use attestation key**

```csharp
var keys = _localValidators[validatorId];
var signatureBytes = _leanSig.Sign(keys.AttestationSecretKey, ToSignatureEpoch(slot), messageRoot);
```

**Step 7: Update aggregation duty to use attestation keys**

In `ExecuteAggregationDutyAsync`, when collecting public keys for aggregation, use `AttestationPublicKey`.

**Step 8: Update _validatorPublicKeys population**

The `_validatorPublicKeys` dictionary (used for known validator keys) should store attestation public keys.

**Step 9: Build to verify**

Run: `dotnet build Lean.sln -c Release`
Expected: Build succeeded.

**Step 10: Commit**

```bash
git add -A && git commit -m "feat(validator): dual attestation/proposal keys, remove proposer attestation embedding"
```

---

### Task 7: Update all tests

**Files:**
- Modify: `tests/Lean.Consensus.Tests/BlockGossipDecoderTests.cs`
- Modify: `tests/Lean.Consensus.Tests/ConsensusMultiNodeFinalizationV2Tests.cs`
- Modify: `tests/Lean.Consensus.Tests/SszStateRoundTripTests.cs`
- Modify: `tests/Lean.Consensus.Tests/StateByRootStoreTests.cs`
- Modify: `tests/Lean.Consensus.Tests/Sync/CheckpointSyncTests.cs`
- Modify: `tests/Lean.Validator.Tests/` — all affected tests

**Step 1: Fix Validator construction in all tests**

Replace `new Validator(pubkey, index)` → `new Validator(pubkey, pubkey, index)` (or distinct keys if testing for specific behavior).

**Step 2: Fix block construction in all tests**

Replace `SignedBlockWithAttestation` / `BlockWithAttestation` → `SignedBlock`.
Remove any proposer attestation creation in test block builders.

**Step 3: Fix gossip decoder tests**

Update SSZ test payloads and assertions for `SignedBlock` format (no inner `BlockWithAttestation` layer).

**Step 4: Fix multi-node simulation tests**

The `ConsensusMultiNodeFinalizationV2Tests` will need updates to:
- Dual key generation
- Block proposal flow (no proposer attestation embedding)
- Assertion updates for new types

**Step 5: Run all tests**

Run: `dotnet test Lean.sln -c Release --filter "TestCategory!=Integration"`
Expected: All tests pass.

**Step 6: Commit**

```bash
git add -A && git commit -m "test: update all tests for devnet4 type changes"
```

---

### Task 8: Format check and final verification

**Step 1: Run format check**

Run: `./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor`
Expected: No formatting violations.

**Step 2: Fix any formatting issues**

If violations found, fix with: `./.dotnet-tools/dotnet-format Lean.sln --fix-whitespace --exclude vendor`

**Step 3: Run full test suite**

Run: `dotnet test Lean.sln -c Release --filter "TestCategory!=Integration"`
Expected: All ~389 non-integration tests pass.

**Step 4: Run consensus simulation**

```bash
dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~ConsensusMultiNodeFinalizationV2Tests" \
  /m:1 /nodeReuse:false
```
Expected: Simulation passes (finalization advances).

**Step 5: Commit formatting fixes if any**

```bash
git add -A && git commit -m "style: fix formatting"
```

**Step 6: Verify clean build**

Run: `dotnet build Lean.sln -c Release`
Expected: 0 warnings, 0 errors.

---

### Task 9: (Optional) Aggregation API shape update

This task adds the new `Aggregate()` method signature to `ILeanMultiSig` matching leanSpec's API shape. The flat path delegates to the existing `AggregateSignatures()`. The recursive path throws `NotSupportedException`.

**Files:**
- Modify: `src/Lean.Crypto/LeanMultiSig.cs`

**Step 1: Add new Aggregate method to ILeanMultiSig**

```csharp
byte[] Aggregate(
    AggregationBits xmssParticipants,
    IReadOnlyList<byte[]> children,
    IReadOnlyList<(ReadOnlyMemory<byte> publicKey, ReadOnlyMemory<byte> signature)> rawXmss,
    ReadOnlySpan<byte> message,
    uint epoch,
    bool recursive = false);
```

**Step 2: Implement in RustLeanMultiSig**

```csharp
public byte[] Aggregate(
    AggregationBits xmssParticipants,
    IReadOnlyList<byte[]> children,
    IReadOnlyList<(ReadOnlyMemory<byte> publicKey, ReadOnlyMemory<byte> signature)> rawXmss,
    ReadOnlySpan<byte> message,
    uint epoch,
    bool recursive = false)
{
    if (recursive)
        throw new NotSupportedException("Recursive aggregation requires upstream lean-multisig crate support.");

    var publicKeys = rawXmss.Select(x => x.publicKey).ToList();
    var signatures = rawXmss.Select(x => x.signature).ToList();
    return AggregateSignatures(publicKeys, signatures, message, epoch);
}
```

**Step 3: Build to verify**

Run: `dotnet build Lean.sln -c Release`

**Step 4: Commit**

```bash
git add -A && git commit -m "feat(crypto): add Aggregate() API matching leanSpec devnet4 shape"
```
