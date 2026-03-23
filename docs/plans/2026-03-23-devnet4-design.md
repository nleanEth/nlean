# devnet4 Implementation Design

**Date**: 2026-03-23
**Branch**: `main`
**Scope**: Single PR — all devnet4 changes in one branch

## Summary

Implements leanSpec devnet4 changes: separate attestation/proposal keys per validator,
removal of `BlockWithAttestation` wrapper, simplified store mappings (PR #436),
and recursive aggregation API shape.

## Reference

- leanSpec devnet4 commits: `be853180d21aa36d6401b8c1541aa6fcaad5008d` → `HEAD`
- leanSpec PR #436: "simplify store mappings" (merged)

---

## 1. Type Changes

### 1.1 Validator Model

**File**: `src/Lean.Consensus/Types/StateTypes.cs`

```
Before: Validator(Bytes52 Pubkey, ulong Index)
After:  Validator(Bytes52 AttestationPubkey, Bytes52 ProposalPubkey, ulong Index)
```

- SSZ layout changes: `[Bytes52, uint64]` → `[Bytes52, Bytes52, uint64]`
- All references to `validator.Pubkey` must be updated to use the appropriate key
- Attestation verification → `AttestationPubkey`
- Block proposer signature verification → `ProposalPubkey`

### 1.2 Block Types

**File**: `src/Lean.Consensus/Types/BlockTypes.cs`

```
Remove: BlockWithAttestation(Block Block, Attestation ProposerAttestation)
Remove: SignedBlockWithAttestation(BlockWithAttestation Message, BlockSignatures Signature)
Add:    SignedBlock(Block Block, BlockSignatures Signature)
```

- `BlockSignatures` keeps the same shape: `(IReadOnlyList<AggregatedSignatureProof> AttestationSignatures, XmssSignature ProposerSignature)`
- `ProposerSignature` now signs `HashTreeRoot(block)` with the **proposal key** (not the attestation key)
- The proposer no longer embeds their attestation in the block

### 1.3 Gossip Signature Store Types

**File**: `src/Lean.Consensus/ForkChoice/ProtoArrayForkChoiceStore.cs` (+ new type file)

```
Remove: _gossipSignatures: Dictionary<(ulong, string), XmssSignature>
Add:    _gossipSignatures: Dictionary<AttestationDataKey, HashSet<GossipSignatureEntry>>

Remove: _attestationDataByRoot: Dictionary<string, AttestationData>  (if exists)

Before: _newAggregatedPayloads: Dictionary<string, List<AggregatedSignatureProof>>
After:  _newAggregatedPayloads: Dictionary<AttestationDataKey, HashSet<AggregatedSignatureProof>>

Before: _knownAggregatedPayloads: Dictionary<string, List<AggregatedSignatureProof>>
After:  _knownAggregatedPayloads: Dictionary<AttestationDataKey, HashSet<AggregatedSignatureProof>>
```

New helper types:
- `GossipSignatureEntry(ValidatorIndex ValidatorId, XmssSignature Signature)` — record struct
- `AttestationDataKey` — wrapper around `AttestationData` with proper equality/hashing for use as dictionary key (based on `DataRootHex`)

### 1.4 Signature Types — Aggregation API

**File**: `src/Lean.Consensus/Types/SignatureTypes.cs`

`AggregatedSignatureProof` needs equality/hashing for `HashSet` usage:
- Add `IEquatable<AggregatedSignatureProof>` based on `ProofData` content

---

## 2. Consensus Logic Changes

### 2.1 Block Verification (`on_block`)

**File**: `src/Lean.Consensus/ForkChoice/ProtoArrayForkChoiceStore.cs`

```
Before: OnBlock(SignedBlockWithAttestation) — extracts proposer attestation, verifies proposer sig against attestation key
After:  OnBlock(SignedBlock) — no proposer attestation, verifies proposer sig against HashTreeRoot(block) with proposal key
```

Key changes:
- Remove proposer attestation extraction and storage
- Verify `ProposerSignature` against `HashTreeRoot(block)` using `validators[proposerIndex].ProposalPubkey`
- Block body attestations still verified against `AttestationPubkey` of respective validators
- Store update: proofs go to `_knownAggregatedPayloads` keyed by `AttestationDataKey`
- No more proposer signature injection into `_gossipSignatures`

### 2.2 Attestation Handling (`on_gossip_attestation`)

**File**: `src/Lean.Consensus/ForkChoice/ProtoArrayForkChoiceStore.cs`

```
Before: Stores sig as _gossipSignatures[(validatorId, dataRootHex)] = signature
After:  Stores sig as _gossipSignatures[attestationDataKey].Add(new GossipSignatureEntry(validatorId, signature))
```

- Remove `_attestationDataByRoot` population
- Verify against `validators[validatorId].AttestationPubkey`

### 2.3 Aggregation Duty

**File**: `src/Lean.Consensus/ForkChoice/ProtoArrayForkChoiceStore.cs` + `src/Lean.Validator/ValidatorService.cs`

`CollectAttestationsForAggregation()`:
- Iterate `_gossipSignatures` by `AttestationDataKey` → collect entries per attestation data
- Return groups of `(AttestationData, List<GossipSignatureEntry>)`

`aggregate_committee_signatures()`:
- After aggregation, prune used entries from gossip map by removing matched `GossipSignatureEntry` items from the set (not deleting per-validator keys)

### 2.4 Block Building (`build_block` / `select_aggregated_proofs`)

Changes to proof selection:
- Look up proofs by `AttestationDataKey` instead of per-validator `SignatureKey`
- Use greedy set-cover: sort candidate proofs by participant count desc, pick any that overlaps remaining validators
- Check `proof.Participants[validatorIndex]` bit for inclusion

### 2.5 Pruning (`prune_stale_attestation_data`)

- Remove `_attestationDataByRoot` pruning
- Prune `_gossipSignatures`, `_newAggregatedPayloads`, `_knownAggregatedPayloads` by checking `attestationData.Target.Slot <= finalizedSlot`

### 2.6 Accept New Attestations

- Merge `_newAggregatedPayloads` into `_knownAggregatedPayloads` using set union per `AttestationDataKey`

---

## 3. Validator Service Changes

### 3.1 Key Management

**File**: `src/Lean.Validator/ValidatorService.cs`, `src/Lean.Validator/ValidatorDutyConfig.cs`

```
Before: _localValidators: Dictionary<ulong, (byte[] PublicKey, byte[] SecretKey)>
After:  _localValidators: Dictionary<ulong, ValidatorKeyMaterial>

ValidatorKeyMaterial = record(
    byte[] AttestationPublicKey,
    byte[] AttestationSecretKey,
    byte[] ProposalPublicKey,
    byte[] ProposalSecretKey
)
```

Key file naming:
```
Before: validator_{idx}_pk.ssz, validator_{idx}_sk.ssz
After:  validator_{idx}_attest_pk.ssz, validator_{idx}_attest_sk.ssz
        validator_{idx}_propose_pk.ssz, validator_{idx}_propose_sk.ssz
```

Backward compatibility: if old single-key files exist, use the same key for both attestation and proposal (migration path).

### 3.2 Block Proposal

**File**: `src/Lean.Validator/ValidatorService.cs`

```
Before: Sign attestation data → embed in BlockWithAttestation → sign block+attestation hash → publish SignedBlockWithAttestation
After:  Build Block → sign HashTreeRoot(block) with proposal key → publish SignedBlock
        (attestation published separately at interval 1 like all validators)
```

### 3.3 Attestation Production

```
Before: Skip attestation if this validator is the proposer for this slot
After:  ALL validators produce attestations (no proposer skip)
```

### 3.4 Aggregation

Use attestation key for signing/verifying attestations. The proposal key is only used for block proposal signatures.

---

## 4. Genesis Config Changes

### 4.1 YAML Format

**File**: `src/Lean.Node/Configuration/LeanChainConfig.cs`

```yaml
# Before
GENESIS_VALIDATORS:
  - "0xabcd..."

# After
GENESIS_VALIDATORS:
  - attestation_pubkey: "0xabcd..."
    proposal_pubkey: "0xef01..."
```

New class: `GenesisValidatorEntry { AttestationPubkey: string, ProposalPubkey: string }`

`LeanChainConfig.GenesisValidators` changes from `List<string>` to `List<GenesisValidatorEntry>`.

### 4.2 ConsensusConfig

**File**: `src/Lean.Consensus/ConsensusConfig.cs`

```
Before: GenesisValidatorPublicKeys: IReadOnlyList<string>
After:  GenesisValidatorKeys: IReadOnlyList<(string AttestationPubkey, string ProposalPubkey)>
```

### 4.3 Genesis State Builder

**File**: `src/Lean.Consensus/ChainStateTransition.cs`

`BuildGenesisValidators()`:
```
Before: new Validator(new Bytes52(pubkeyBytes), index)
After:  new Validator(new Bytes52(attestationPubkeyBytes), new Bytes52(proposalPubkeyBytes), index)
```

---

## 5. Aggregation API Changes

### 5.1 New Aggregate Method Signature

**File**: `src/Lean.Crypto/LeanMultiSig.cs`

```csharp
// New API shape matching leanSpec
byte[] Aggregate(
    AggregationBits xmssParticipants,
    IReadOnlyList<AggregatedSignatureProof> children,  // existing sub-proofs
    IReadOnlyList<(byte[] publicKey, XmssSignature signature)> rawXmss,  // raw individual sigs
    ReadOnlySpan<byte> message,
    uint epoch,
    bool recursive = false);
```

Implementation:
- When `recursive = false` (default): extract raw signatures from `rawXmss`, call existing `AggregateSignatures()` FFI
- When `recursive = true`: throw `NotSupportedException("Recursive aggregation requires upstream lean-multisig crate support")` until the Rust FFI is extended to accept sub-proofs
- `children` parameter is accepted but only used in recursive mode

### 5.2 Post-Aggregation Cleanup

After successful `aggregate()`, remove the consumed entries:
- Remove gossip signatures that were aggregated
- Add new `AggregatedSignatureProof` to `_newAggregatedPayloads`

---

## 6. SSZ / Wire Format Changes

### 6.1 Gossip Topics

- Block gossip topic: payload changes from `SignedBlockWithAttestation` SSZ to `SignedBlock` SSZ
- Attestation gossip: unchanged (still `SignedAttestation`)
- Aggregated attestation gossip: unchanged

### 6.2 RPC

- `blocks_by_range` / `blocks_by_root`: return `SignedBlock` instead of `SignedBlockWithAttestation`
- Checkpoint sync state: `Validator` SSZ layout changes

### 6.3 State SSZ

- `Validator` SSZ changes: 2 x `Bytes52` + `uint64` instead of 1 x `Bytes52` + `uint64`
- Affects: state roots, hash tree roots, checkpoint sync serialization

---

## 7. Test Updates

All tests referencing `BlockWithAttestation`, `SignedBlockWithAttestation`, or the proposer attestation embedding must be updated.

Key test files:
- `tests/Lean.Consensus.Tests/` — fork choice tests, state transition tests, multi-node simulation
- `tests/Lean.Validator.Tests/` — validator duty tests
- Test helpers that build blocks (likely `TestBlockBuilder` or similar)

---

## 8. Implementation Order

1. **Types first**: Validator, Block types, GossipSignatureEntry, AttestationDataKey
2. **Genesis config**: YAML parsing, ConsensusConfig, genesis state builder
3. **Store mappings refactor** (PR #436): gossip_signatures, aggregated_payloads keying
4. **Consensus logic**: on_block, on_attestation, pruning, block building
5. **Validator service**: dual keys, block proposal, attestation production (no proposer skip)
6. **Aggregation API**: new method shape with recursive flag
7. **Wire format**: gossip, RPC, SSZ serialization
8. **Tests**: update all affected tests
9. **Build & verify**: `dotnet build`, `dotnet test`, format check

---

## 9. Risk Assessment

| Risk | Mitigation |
|------|------------|
| Recursive aggregation not supported in Rust FFI | Flag defaults to false; flat path works. NotSupportedException for recursive until upstream update |
| SSZ layout change breaks checkpoint sync interop | Expected — devnet4 is a new network, no cross-devnet sync needed |
| Old key files (single key per validator) | Backward compat: use same key for both if old files detected |
| Store mapping refactor is large | Follow PR #436 closely; types guide the changes |
