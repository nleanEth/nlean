using Lean.Consensus.Types;
using Lean.Consensus.TestDriver.Fixtures;

namespace Lean.Consensus.TestDriver.Drivers;

/// <summary>
/// Shared helpers converting fixture record types to native consensus types.
/// Kept in one place so HTTP test-driver endpoints and unit-level spec runners
/// stay in sync.
/// </summary>
internal static class FixtureConverter
{
    public static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);

    public static Block ConvertBlock(TestBlock tb) => new(
        new Slot(tb.Slot),
        tb.ProposerIndex,
        new Bytes32(ParseHex(tb.ParentRoot)),
        new Bytes32(ParseHex(tb.StateRoot)),
        ConvertBlockBody(tb.Body));

    public static BlockBody ConvertBlockBody(TestBlockBody? body)
    {
        if (body?.Attestations?.Data is null or { Count: 0 })
            return new BlockBody(Array.Empty<AggregatedAttestation>());

        var attestations = body.Attestations.Data
            .Select(a => new AggregatedAttestation(
                new AggregationBits(a.AggregationBits.Data),
                ConvertAttestationData(a.Data)))
            .ToList();

        return new BlockBody(attestations);
    }

    public static AttestationData ConvertAttestationData(TestAttestationData td) => new(
        new Slot(td.Slot),
        new Checkpoint(new Bytes32(ParseHex(td.Head.Root)), new Slot(td.Head.Slot)),
        new Checkpoint(new Bytes32(ParseHex(td.Target.Root)), new Slot(td.Target.Slot)),
        new Checkpoint(new Bytes32(ParseHex(td.Source.Root)), new Slot(td.Source.Slot)));

    public static ConsensusConfig BuildConfigFromAnchor(TestState anchorState)
    {
        var validators = anchorState.Validators?.Data ?? new List<TestValidator>();
        var keys = validators
            .Select(v => (v.AttestationKeyHex, v.ProposalKeyHex))
            .ToList();

        return new ConsensusConfig
        {
            InitialValidatorCount = (ulong)Math.Max(1, validators.Count),
            GenesisTimeUnix = anchorState.Config.GenesisTime,
            GenesisValidatorKeys = keys,
        };
    }

    public static State ReconstructState(TestState ts)
    {
        var validators = (ts.Validators?.Data ?? new List<TestValidator>())
            .Select(v => new Validator(
                new Bytes52(ParseHex(v.AttestationKeyHex)),
                new Bytes52(ParseHex(v.ProposalKeyHex)),
                v.Index))
            .ToList();

        var header = new BlockHeader(
            new Slot(ts.LatestBlockHeader.Slot),
            ts.LatestBlockHeader.ProposerIndex,
            new Bytes32(ParseHex(ts.LatestBlockHeader.ParentRoot)),
            new Bytes32(ParseHex(ts.LatestBlockHeader.StateRoot)),
            new Bytes32(ParseHex(ts.LatestBlockHeader.BodyRoot)));

        var justified = new Checkpoint(
            new Bytes32(ParseHex(ts.LatestJustified.Root)), new Slot(ts.LatestJustified.Slot));
        var finalized = new Checkpoint(
            new Bytes32(ParseHex(ts.LatestFinalized.Root)), new Slot(ts.LatestFinalized.Slot));

        var historical = (ts.HistoricalBlockHashes?.Data ?? new List<string>())
            .Select(h => new Bytes32(ParseHex(h)))
            .ToList();
        var justifiedSlots = ts.JustifiedSlots?.Data ?? new List<bool>();
        var justificationsRoots = (ts.JustificationsRoots?.Data ?? new List<string>())
            .Select(h => new Bytes32(ParseHex(h)))
            .ToList();
        var justificationsValidators = ts.JustificationsValidators?.Data ?? new List<bool>();

        return new State(
            new Config(ts.Config.GenesisTime),
            new Slot(ts.Slot),
            header,
            justified,
            finalized,
            historical,
            justifiedSlots,
            validators,
            justificationsRoots,
            justificationsValidators);
    }
}
