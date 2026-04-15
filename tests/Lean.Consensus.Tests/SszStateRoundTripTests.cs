using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SszStateRoundTripTests
{
    [Test]
    public void EncodeDecodeState_RoundTrips()
    {
        var state = new State(
            new Config(9999),
            new Slot(100),
            new BlockHeader(
                new Slot(100),
                3,
                new Bytes32(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
                new Bytes32(Enumerable.Range(32, 32).Select(i => (byte)i).ToArray()),
                new Bytes32(Enumerable.Range(64, 32).Select(i => (byte)i).ToArray())),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0xA1, 32).ToArray()), new Slot(96)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0xB2, 32).ToArray()), new Slot(64)),
            new[]
            {
                new Bytes32(Enumerable.Repeat((byte)0x01, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x02, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x03, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x04, 32).ToArray())
            },
            new[] { true, false, true, true, false, true, false, false, true },
            new[]
            {
                new Validator(new Bytes52(Enumerable.Repeat((byte)0x60, 52).ToArray()), new Bytes52(Enumerable.Repeat((byte)0xA0, 52).ToArray()), 0),
                new Validator(new Bytes52(Enumerable.Repeat((byte)0x61, 52).ToArray()), new Bytes52(Enumerable.Repeat((byte)0xA1, 52).ToArray()), 1),
                new Validator(new Bytes52(Enumerable.Repeat((byte)0x62, 52).ToArray()), new Bytes52(Enumerable.Repeat((byte)0xA2, 52).ToArray()), 2)
            },
            new[]
            {
                new Bytes32(Enumerable.Repeat((byte)0x70, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x71, 32).ToArray())
            },
            new[] { true, true, false, true });

        var encoded = SszEncoding.Encode(state);
        var decoded = SszDecoding.DecodeState(encoded);

        Assert.That(decoded.Config.GenesisTime, Is.EqualTo(state.Config.GenesisTime));
        Assert.That(decoded.Slot.Value, Is.EqualTo(state.Slot.Value));

        Assert.That(decoded.LatestBlockHeader.Slot.Value, Is.EqualTo(state.LatestBlockHeader.Slot.Value));
        Assert.That(decoded.LatestBlockHeader.ProposerIndex, Is.EqualTo(state.LatestBlockHeader.ProposerIndex));
        Assert.That(decoded.LatestBlockHeader.ParentRoot.AsSpan().ToArray(),
            Is.EqualTo(state.LatestBlockHeader.ParentRoot.AsSpan().ToArray()));
        Assert.That(decoded.LatestBlockHeader.StateRoot.AsSpan().ToArray(),
            Is.EqualTo(state.LatestBlockHeader.StateRoot.AsSpan().ToArray()));
        Assert.That(decoded.LatestBlockHeader.BodyRoot.AsSpan().ToArray(),
            Is.EqualTo(state.LatestBlockHeader.BodyRoot.AsSpan().ToArray()));

        Assert.That(decoded.LatestJustified.Slot.Value, Is.EqualTo(state.LatestJustified.Slot.Value));
        Assert.That(decoded.LatestJustified.Root.AsSpan().ToArray(),
            Is.EqualTo(state.LatestJustified.Root.AsSpan().ToArray()));

        Assert.That(decoded.LatestFinalized.Slot.Value, Is.EqualTo(state.LatestFinalized.Slot.Value));
        Assert.That(decoded.LatestFinalized.Root.AsSpan().ToArray(),
            Is.EqualTo(state.LatestFinalized.Root.AsSpan().ToArray()));

        Assert.That(decoded.HistoricalBlockHashes.Count, Is.EqualTo(4));
        for (var i = 0; i < 4; i++)
            Assert.That(decoded.HistoricalBlockHashes[i].AsSpan().ToArray(),
                Is.EqualTo(state.HistoricalBlockHashes[i].AsSpan().ToArray()));

        Assert.That(decoded.JustifiedSlots, Is.EqualTo(state.JustifiedSlots));

        Assert.That(decoded.Validators.Count, Is.EqualTo(3));
        for (var i = 0; i < 3; i++)
        {
            Assert.That(decoded.Validators[i].AttestationPubkey.AsSpan().ToArray(),
                Is.EqualTo(state.Validators[i].AttestationPubkey.AsSpan().ToArray()));
            Assert.That(decoded.Validators[i].ProposalPubkey.AsSpan().ToArray(),
                Is.EqualTo(state.Validators[i].ProposalPubkey.AsSpan().ToArray()));
            Assert.That(decoded.Validators[i].Index, Is.EqualTo(state.Validators[i].Index));
        }

        Assert.That(decoded.JustificationsRoots.Count, Is.EqualTo(2));
        for (var i = 0; i < 2; i++)
            Assert.That(decoded.JustificationsRoots[i].AsSpan().ToArray(),
                Is.EqualTo(state.JustificationsRoots[i].AsSpan().ToArray()));

        Assert.That(decoded.JustificationsValidators, Is.EqualTo(state.JustificationsValidators));
    }

    [Test]
    public void EncodeDecodeState_EmptyVariableLists()
    {
        var state = new State(
            new Config(0),
            new Slot(0),
            new BlockHeader(
                new Slot(0),
                0,
                Bytes32.Zero(),
                Bytes32.Zero(),
                Bytes32.Zero()),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            Array.Empty<Bytes32>(),
            Array.Empty<bool>(),
            Array.Empty<Validator>(),
            Array.Empty<Bytes32>(),
            Array.Empty<bool>());

        var encoded = SszEncoding.Encode(state);
        var decoded = SszDecoding.DecodeState(encoded);

        Assert.That(decoded.Config.GenesisTime, Is.EqualTo(0));
        Assert.That(decoded.Slot.Value, Is.EqualTo(0));
        Assert.That(decoded.HistoricalBlockHashes.Count, Is.EqualTo(0));
        Assert.That(decoded.JustifiedSlots, Is.Empty);
        Assert.That(decoded.Validators.Count, Is.EqualTo(0));
        Assert.That(decoded.JustificationsRoots.Count, Is.EqualTo(0));
        Assert.That(decoded.JustificationsValidators, Is.Empty);
    }

    [TestCase(new bool[] { }, Description = "Empty")]
    [TestCase(new[] { true }, Description = "Single true")]
    [TestCase(new[] { false }, Description = "Single false")]
    [TestCase(new[] { true, false, true }, Description = "Mixed 3")]
    [TestCase(new[] { true, true, true, true, true, true, true, true }, Description = "Full byte")]
    [TestCase(new[] { false, false, false, false, false, false, false, false, true }, Description = "Cross byte boundary")]
    public void EncodeDecode_Bitlist_RoundTrips(bool[] bits)
    {
        var encoded = SszEncoding.EncodeBitlist(bits);
        var decoded = SszDecoding.DecodeBitlist(encoded);

        Assert.That(decoded, Is.EqualTo(bits));
    }
}
