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

    // TODO: Replace LINQ .Select().ToList() with incremental Merkle caching for mainnet-scale
    // validator counts. Current approach allocates temporary List<byte[]> per call, acceptable
    // for devnet3 (few validators) but will become a bottleneck with thousands of validators.
    public byte[] HashTreeRoot()
    {
        var historicalRoots = HistoricalBlockHashes.Select(hash => hash.HashTreeRoot()).ToList();
        var validatorRoots = Validators.Select(validator => validator.HashTreeRoot()).ToList();
        var justificationRoots = JustificationsRoots.Select(hash => hash.HashTreeRoot()).ToList();

        return SszInterop.HashContainer(
            Config.HashTreeRoot(),
            SszInterop.HashUInt64(Slot.Value),
            LatestBlockHeader.HashTreeRoot(),
            LatestJustified.HashTreeRoot(),
            LatestFinalized.HashTreeRoot(),
            SszInterop.HashList(historicalRoots, SszEncoding.HistoricalRootsLimit),
            SszInterop.HashBitlist(JustifiedSlots.ToArray(), SszEncoding.HistoricalRootsLimit),
            SszInterop.HashList(validatorRoots, SszEncoding.ValidatorRegistryLimit),
            SszInterop.HashList(justificationRoots, SszEncoding.HistoricalRootsLimit),
            SszInterop.HashBitlist(JustificationsValidators.ToArray(), JustificationsValidatorsLimit));
    }
}
