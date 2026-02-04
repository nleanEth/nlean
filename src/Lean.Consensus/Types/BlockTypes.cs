using Lean.Ssz;

namespace Lean.Consensus.Types;

public sealed record BlockBody(IReadOnlyList<AggregatedAttestation> Attestations)
{
    public byte[] HashTreeRoot()
    {
        var roots = Attestations.Select(att => att.HashTreeRoot()).ToList();
        return Ssz.HashTreeRootContainer(
            Ssz.HashTreeRootList(roots, Attestations.Count));
    }
}

public sealed record BlockHeader(
    Slot Slot,
    ulong ProposerIndex,
    Bytes32 ParentRoot,
    Bytes32 StateRoot,
    Bytes32 BodyRoot)
{
    public byte[] HashTreeRoot()
    {
        return Ssz.HashTreeRootContainer(
            Ssz.HashTreeRootUInt64(Slot.Value),
            Ssz.HashTreeRootUInt64(ProposerIndex),
            ParentRoot.HashTreeRoot(),
            StateRoot.HashTreeRoot(),
            BodyRoot.HashTreeRoot());
    }
}

public sealed record Block(
    Slot Slot,
    ulong ProposerIndex,
    Bytes32 ParentRoot,
    Bytes32 StateRoot,
    BlockBody Body)
{
    public byte[] HashTreeRoot()
    {
        return Ssz.HashTreeRootContainer(
            Ssz.HashTreeRootUInt64(Slot.Value),
            Ssz.HashTreeRootUInt64(ProposerIndex),
            ParentRoot.HashTreeRoot(),
            StateRoot.HashTreeRoot(),
            Body.HashTreeRoot());
    }
}

public sealed record BlockWithAttestation(Block Block, Attestation ProposerAttestation)
{
    public byte[] HashTreeRoot()
    {
        return Ssz.HashTreeRootContainer(
            Block.HashTreeRoot(),
            ProposerAttestation.HashTreeRoot());
    }
}

public sealed record BlockSignatures(
    IReadOnlyList<AggregatedSignatureProof> AttestationSignatures,
    byte[] ProposerSignature)
{
    public byte[] HashTreeRoot()
    {
        var attestationRoots = AttestationSignatures.Select(sig => sig.HashTreeRoot()).ToList();
        return Ssz.HashTreeRootContainer(
            Ssz.HashTreeRootList(attestationRoots, AttestationSignatures.Count),
            Ssz.HashTreeRootBytes(ProposerSignature));
    }
}

public sealed record SignedBlockWithAttestation(BlockWithAttestation Message, BlockSignatures Signature)
{
    public byte[] HashTreeRoot()
    {
        return Ssz.HashTreeRootContainer(
            Message.HashTreeRoot(),
            Signature.HashTreeRoot());
    }
}
