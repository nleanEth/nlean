namespace Lean.Consensus.Types;

public sealed record BlockBody(IReadOnlyList<AggregatedAttestation> Attestations)
{
}

public sealed record BlockHeader(
    Slot Slot,
    ulong ProposerIndex,
    Bytes32 ParentRoot,
    Bytes32 StateRoot,
    Bytes32 BodyRoot)
{
}

public sealed record Block(
    Slot Slot,
    ulong ProposerIndex,
    Bytes32 ParentRoot,
    Bytes32 StateRoot,
    BlockBody Body)
{
}

public sealed record BlockWithAttestation(Block Block, Attestation ProposerAttestation)
{
}

public sealed record BlockSignatures(
    IReadOnlyList<AggregatedSignatureProof> AttestationSignatures,
    byte[] ProposerSignature)
{
}

public sealed record SignedBlockWithAttestation(BlockWithAttestation Message, BlockSignatures Signature)
{
}
