# devnet3 Alignment Design

**Date**: 2026-02-23
**Branch**: feature/full-spec-alignment
**Upstream**: leanSpec upmain1 (commit 8b7636b), zeam main, ethlambda main, ream master

## Problem

nlean is aligned to devnet2 spec. devnet3 introduces:
1. XmssSignature must be a proper SSZ Container (not an opaque byte blob)
2. Aggregator architecture — attestation subnets + aggregated attestation gossip topic
3. ValidatorIndex semantic type replacing raw Uint64

## Key Design Decision: XmssSignature as SSZ Container

Reference: [leanSig generalized_xmss.rs](https://github.com/leanEthereum/leanSig/blob/16c660e/src/signature/generalized_xmss.rs#L45)

The Signature is an SSZ Container with typed fields:
```
Signature {
  path: HashTreeOpening {
    siblings: List[Vector[Fp, HASH_LEN_FE], NODE_LIST_LIMIT]   // variable-size List
  }
  rho: Vector[Fp, RAND_LEN_FE]                                  // fixed-size
  hashes: List[Vector[Fp, HASH_LEN_FE], NODE_LIST_LIMIT]        // variable-size List
}
```

**Why not FixedBytes<3112>?**

`hash_tree_root` depends on type structure, not raw bytes:
- `hash_tree_root(FixedBytes<3112>)` = merkleize(chunk 3112 bytes into 32-byte leaves) ← **WRONG**
- `hash_tree_root(Signature)` = merkleize([hash_tree_root(path), hash_tree_root(rho), hash_tree_root(hashes)]) ← **CORRECT**

The fields `siblings` and `hashes` are `List` (variable-size with limit), not `Vector` (fixed-size).
`NODE_LIST_LIMIT` is a limit, not a length. Different XMSS configs produce different signature sizes.
leansig's `to_bytes()` calls `as_ssz_bytes()` which produces standard SSZ Container encoding.
ream stores the result as `FixedBytes<3112>` but this is a shortcut that happens to work for
wire encoding only — the `hash_tree_root` must respect the Container structure.

## Design: Feature-Sliced Alignment

### Slice 1 — SSZ + Types Foundation

**Goal**: Wire-format compatibility with devnet3 peers, correct hash_tree_root.

#### 1a. XmssSignature as Structured Container

Replace the current opaque `XmssSignature` (byte[3112]) with a proper Container type:

```csharp
// The Signature Container — SSZ encode/decode respects field types
public sealed class XmssSignature
{
    public HashTreeOpening Path { get; }     // Container with List siblings
    public Randomness Rho { get; }           // Vector[Fp, 7] — fixed
    public HashDigestList Hashes { get; }    // List[Vector[Fp, 8]] — variable

    // SSZ encoding: 2 offsets (path, hashes) in Signature + 1 offset (siblings) in HashTreeOpening
    public byte[] EncodeBytes() => SszEncoding.Encode(this);

    // hash_tree_root as Container: merkleize field roots
    public byte[] HashTreeRoot() =>
        SszInterop.HashContainer(
            Path.HashTreeRoot(),
            Rho.HashTreeRoot(),
            Hashes.HashTreeRoot());

    // Factory: decode from SSZ bytes (wire format from leansig)
    public static XmssSignature FromBytes(ReadOnlySpan<byte> bytes) => SszDecoding.DecodeSignature(bytes);

    // Factory: create empty signature for testing
    public static XmssSignature Empty() => new(
        new HashTreeOpening(HashDigestList.Empty()),
        Randomness.Zero(),
        HashDigestList.Empty());
}
```

Update `HashTreeOpening`:
```csharp
public sealed class HashTreeOpening
{
    public HashDigestList Siblings { get; }

    // hash_tree_root as Container with 1 field
    public byte[] HashTreeRoot() =>
        SszInterop.HashContainer(Siblings.HashTreeRoot());
}
```

Update `HashDigestList.HashTreeRoot()`:
```csharp
// List hash_tree_root: merkleize elements, mix in length
public byte[] HashTreeRoot() =>
    SszInterop.MerkleizeList(
        Elements.Select(e => e.HashTreeRoot()).ToList(),
        SszEncoding.NodeListLimit);
```

**SszEncoding changes**:
- `Encode(XmssSignature)`: Write Container with 2 offsets (path variable, rho fixed, hashes variable)
- `Encode(HashTreeOpening)`: Write Container with 1 offset (siblings variable)
- Remove the `const int Length = 3112` constraint

**SszDecoding changes**:
- `DecodeSignature(ReadOnlySpan<byte>)`: Parse offsets, decode path/rho/hashes
- `DecodeHashTreeOpening(ReadOnlySpan<byte>)`: Parse offset, decode siblings list
- `DecodeHashDigestList(ReadOnlySpan<byte>)`: Decode list of Vector[Fp, 8]

**SszInterop additions**:
- `MerkleizeList(roots, limit)`: Merkleize list elements with limit, mix_in_length
- Existing `HashContainer` works for Container root

#### 1b. ValidatorIndex Type

```csharp
public readonly struct ValidatorIndex : IEquatable<ValidatorIndex>, IComparable<ValidatorIndex>
{
    public ulong Value { get; }
    public ValidatorIndex(ulong value) { Value = value; }

    public bool IsProposerFor(ulong slot, int numValidators)
        => (int)(slot % (ulong)numValidators) == (int)Value;
    public bool IsValid(int numValidators) => (int)Value < numValidators;
    public int ComputeSubnetId(int numCommittees) => (int)(Value % (ulong)numCommittees);

    // Implicit conversion from ulong for backward compatibility
    public static implicit operator ulong(ValidatorIndex v) => v.Value;
    public static implicit operator ValidatorIndex(ulong v) => new(v);
}
```

Update fields in: Block, BlockHeader, Attestation, SignedAttestation, Validator.

#### 1c. New Container: SignedAggregatedAttestation

```csharp
public sealed record SignedAggregatedAttestation(
    AttestationData Data,
    AggregatedSignatureProof Proof);
```

SSZ: variable-size Container (Proof has variable fields).

### Slice 2 — Aggregator in Fork Choice Store

**Goal**: Store tracks aggregated attestation payloads (not raw per-validator attestations).

#### Store Field Changes

Old fields (remove from ProtoArrayForkChoiceStore):
- `_pendingAttestations` (per-validator attestation data)

New fields:
- `_gossipSignatures: Dictionary<SignatureKey, byte[]>` — per-validator XMSS sigs from subnet gossip
- `_attestationDataByRoot: Dictionary<Bytes32, AttestationData>` — root → data lookup
- `_newAggregatedPayloads: Dictionary<SignatureKey, List<AggregatedSignatureProof>>` — pending tick
- `_knownAggregatedPayloads: Dictionary<SignatureKey, List<AggregatedSignatureProof>>` — after tick

Where `SignatureKey = (ValidatorIndex validatorId, Bytes32 dataRoot)`.

#### Method Changes

- `OnAttestation(SignedAttestation)` → `OnGossipAggregatedAttestation(SignedAggregatedAttestation)`
- New: `ExtractAttestationsFromAggregatedPayloads()` — for block building
- New: `AggregateCommitteeSignatures()` → `List<SignedAggregatedAttestation>`
- `TickInterval()`: promote new → known aggregated payloads at last interval
- New: `PruneStaleAttestationData()` — cleanup on finalization

### Slice 3 — Network Gossip Topics

**Goal**: Subscribe to devnet3 gossip topics.

- `attestation` → `attestation_{subnet_id}` (subnet topics, 1 subnet currently)
- New: `aggregation` topic for `SignedAggregatedAttestation`
- `ATTESTATION_COMMITTEE_COUNT = 1`
- Block topic unchanged

Message handling:
- Attestation subnet: `SignedAttestation` → store raw signature in `_gossipSignatures`
- Aggregation topic: `SignedAggregatedAttestation` → `OnGossipAggregatedAttestation()`

### Slice 4 — Validator Aggregator Role

**Goal**: Validators produce and broadcast aggregated attestations.

- Interval 0: Block production (unchanged)
- Interval 1: Attestation → broadcast to subnet topic
- After interval 1: If aggregator, collect from `_gossipSignatures`, aggregate, broadcast to `aggregation` topic
- `StoreProposerAttestationSignature()` — for self-aggregation
- `_attestedSlots` set to prevent duplicate attestations

## Config Updates

```
INTERVALS_PER_SLOT = 5            (already done in V2)
SECONDS_PER_SLOT = 4              (already done)
MILLISECONDS_PER_INTERVAL = 800   (already done in V2)
ATTESTATION_COMMITTEE_COUNT = 1   (new)
```

## Test Strategy

Per slice:
1. SSZ roundtrip tests with leanSpec test vectors (especially XMSS containers)
2. hash_tree_root conformance tests (Container root, not bytes root)
3. Unit tests for new store methods
4. Integration tests updated to new types

## Dependencies

- Slice 1 (Types+SSZ) — no dependencies
- Slice 2 (Store) — depends on Slice 1
- Slice 3 (Network) — depends on Slice 1
- Slice 4 (Validator) — depends on Slices 2 + 3
