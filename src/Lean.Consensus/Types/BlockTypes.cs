using System.Linq;

namespace Lean.Consensus.Types;

public sealed record BlockBody(IReadOnlyList<AggregatedAttestation> Attestations)
{
    public byte[] HashTreeRoot()
    {
        var roots = Attestations.Select(att => att.HashTreeRoot()).ToList();
        return SszInterop.HashList(roots, SszEncoding.ValidatorRegistryLimit);
    }
}

public sealed record BlockHeader(
    Slot Slot,
    ValidatorIndex ProposerIndex,
    Bytes32 ParentRoot,
    Bytes32 StateRoot,
    Bytes32 BodyRoot)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            SszInterop.HashUInt64(Slot.Value),
            SszInterop.HashUInt64(ProposerIndex),
            ParentRoot.HashTreeRoot(),
            StateRoot.HashTreeRoot(),
            BodyRoot.HashTreeRoot());
    }
}

public sealed record Block(
    Slot Slot,
    ValidatorIndex ProposerIndex,
    Bytes32 ParentRoot,
    Bytes32 StateRoot,
    BlockBody Body)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            SszInterop.HashUInt64(Slot.Value),
            SszInterop.HashUInt64(ProposerIndex),
            ParentRoot.HashTreeRoot(),
            StateRoot.HashTreeRoot(),
            Body.HashTreeRoot());
    }
}

public sealed record BlockSignatures(
    IReadOnlyList<AggregatedSignatureProof> AttestationSignatures,
    XmssSignature ProposerSignature)
{
    public byte[] HashTreeRoot()
    {
        var attestationRoots = AttestationSignatures.Select(sig => sig.HashTreeRoot()).ToList();
        return SszInterop.HashContainer(
            SszInterop.HashList(attestationRoots, SszEncoding.ValidatorRegistryLimit),
            ProposerSignature.HashTreeRoot());
    }
}

public sealed record SignedBlock(Block Block, BlockSignatures Signature)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            Block.HashTreeRoot(),
            Signature.HashTreeRoot());
    }
}
