using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lean.SpecTests.Types;

// Top-level fixture: each JSON file is Dictionary<string, ForkChoiceTest>
public sealed record ForkChoiceTest(
    [property: JsonPropertyName("network")] string Network,
    [property: JsonPropertyName("leanEnv")] string LeanEnv,
    [property: JsonPropertyName("anchorState")] TestState AnchorState,
    [property: JsonPropertyName("anchorBlock")] TestBlock AnchorBlock,
    [property: JsonPropertyName("steps")] List<ForkChoiceStep> Steps,
    [property: JsonPropertyName("maxSlot")] ulong MaxSlot,
    [property: JsonPropertyName("_info")] TestInfo? Info);

public sealed record VerifySignaturesTest(
    [property: JsonPropertyName("network")] string Network,
    [property: JsonPropertyName("leanEnv")] string LeanEnv,
    [property: JsonPropertyName("anchorState")] TestState AnchorState,
    [property: JsonPropertyName("signedBlock")] TestSignedBlock SignedBlock,
    [property: JsonPropertyName("expectException")] string? ExpectException,
    [property: JsonPropertyName("_info")] TestInfo? Info);

public sealed record TestSignedBlock(
    [property: JsonPropertyName("block")] TestBlock Block,
    [property: JsonPropertyName("signature")] TestBlockSignatures Signature);

public sealed record TestBlockSignatures(
    [property: JsonPropertyName("attestationSignatures")] TestDataArray<JsonElement>? AttestationSignatures,
    [property: JsonPropertyName("proposerSignature")] string ProposerSignature);

public sealed record StateTransitionTest(
    [property: JsonPropertyName("network")] string Network,
    [property: JsonPropertyName("leanEnv")] string LeanEnv,
    [property: JsonPropertyName("pre")] TestState Pre,
    [property: JsonPropertyName("blocks")] List<TestBlock> Blocks,
    [property: JsonPropertyName("post")] PostState? Post,
    [property: JsonPropertyName("expectException")] string? ExpectException,
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

// Current leanSpec fixtures split keys; older lantern snapshots used a single `pubkey`.
public sealed record TestValidator(
    [property: JsonPropertyName("attestationPubkey")] string? AttestationPubkey,
    [property: JsonPropertyName("proposalPubkey")] string? ProposalPubkey,
    [property: JsonPropertyName("pubkey")] string? Pubkey,
    [property: JsonPropertyName("index")] ulong Index)
{
    public string AttestationKeyHex => AttestationPubkey ?? Pubkey ?? string.Empty;
    public string ProposalKeyHex => ProposalPubkey ?? Pubkey ?? string.Empty;
}

public sealed record TestDataArray<T>(
    [property: JsonPropertyName("data")] List<T> Data);

public sealed record TestBlock(
    [property: JsonPropertyName("slot")] ulong Slot,
    [property: JsonPropertyName("proposerIndex")] ulong ProposerIndex,
    [property: JsonPropertyName("parentRoot")] string ParentRoot,
    [property: JsonPropertyName("stateRoot")] string StateRoot,
    [property: JsonPropertyName("body")] TestBlockBody Body);

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

public sealed record ForkChoiceStep(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("checks")] StoreChecks? Checks,
    [property: JsonPropertyName("stepType")] string StepType,
    [property: JsonPropertyName("block")] TestBlockStepData? Block,
    [property: JsonPropertyName("attestation")] TestAttestationStepData? Attestation,
    [property: JsonPropertyName("gossipAggregatedAttestation")] TestGossipAggregatedAttestationStepData? GossipAggregatedAttestation,
    [property: JsonPropertyName("time")] ulong? Time);

// Current leanSpec fixtures put block fields flat inside the step. Older lantern
// fixtures nested them under `block` with a `proposerAttestation` sibling.
public sealed record TestBlockStepData(
    [property: JsonPropertyName("slot")] ulong? Slot,
    [property: JsonPropertyName("proposerIndex")] ulong? ProposerIndex,
    [property: JsonPropertyName("parentRoot")] string? ParentRoot,
    [property: JsonPropertyName("stateRoot")] string? StateRoot,
    [property: JsonPropertyName("body")] TestBlockBody? Body,
    [property: JsonPropertyName("blockRootLabel")] string? BlockRootLabel,
    [property: JsonPropertyName("block")] TestBlock? NestedBlock,
    [property: JsonPropertyName("proposerAttestation")] TestAttestationStepData? ProposerAttestation)
{
    public TestBlock ResolveBlock()
    {
        if (NestedBlock is not null) return NestedBlock;
        if (Slot is null || ProposerIndex is null || ParentRoot is null || StateRoot is null || Body is null)
            throw new InvalidOperationException("Block step is missing required fields.");
        return new TestBlock(Slot.Value, ProposerIndex.Value, ParentRoot, StateRoot, Body);
    }
}

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
    [property: JsonPropertyName("latestBlockHeaderSlot")] ulong? LatestBlockHeaderSlot = null,
    [property: JsonPropertyName("historicalBlockHashesCount")] ulong? HistoricalBlockHashesCount = null);

public sealed record TestInfo(
    [property: JsonPropertyName("hash")] string? Hash,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("testId")] string? TestId,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("fixtureFormat")] string? FixtureFormat);
