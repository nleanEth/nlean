using System.Linq;

namespace Lean.Consensus.Types;

public sealed record Config(ulong GenesisTime)
{
    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(SszInterop.HashUInt64(GenesisTime));
    }
}

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

public sealed record State(
    Config Config,
    Slot Slot,
    BlockHeader LatestBlockHeader,
    Checkpoint LatestJustified,
    Checkpoint LatestFinalized,
    IReadOnlyList<Bytes32> HistoricalBlockHashes,
    IReadOnlyList<bool> JustifiedSlots,
    IReadOnlyList<Validator> Validators,
    IReadOnlyList<Bytes32> JustificationsRoots,
    IReadOnlyList<bool> JustificationsValidators)
{
    private const ulong JustificationsValidatorsLimit = 1UL << 30;

    // Incremental Merkle caching: Validators and Config are reference-stable
    // across State instances (the parent state's references are reused in
    // ChainStateTransition.TryComputePostState), so we memoize their sub-roots
    // via ConditionalWeakTable. Lists that grow per block (historical hashes,
    // justifications) get rebuilt because each State holds a fresh List
    // instance — caching them by reference would always miss.
    public byte[] HashTreeRoot()
    {
        var historicalRoots = new byte[HistoricalBlockHashes.Count][];
        for (var i = 0; i < HistoricalBlockHashes.Count; i++)
        {
            historicalRoots[i] = HistoricalBlockHashes[i].HashTreeRoot();
        }

        var justificationRoots = new byte[JustificationsRoots.Count][];
        for (var i = 0; i < JustificationsRoots.Count; i++)
        {
            justificationRoots[i] = JustificationsRoots[i].HashTreeRoot();
        }

        return SszInterop.HashContainer(
            StateHashCache.GetConfigRoot(Config),
            SszInterop.HashUInt64(Slot.Value),
            LatestBlockHeader.HashTreeRoot(),
            LatestJustified.HashTreeRoot(),
            LatestFinalized.HashTreeRoot(),
            SszInterop.HashList(historicalRoots, SszEncoding.HistoricalRootsLimit),
            SszInterop.HashBitlist(JustifiedSlots.ToArray(), SszEncoding.HistoricalRootsLimit),
            StateHashCache.GetValidatorListRoot(Validators),
            SszInterop.HashList(justificationRoots, SszEncoding.HistoricalRootsLimit),
            SszInterop.HashBitlist(JustificationsValidators.ToArray(), JustificationsValidatorsLimit));
    }
}
