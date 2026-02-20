using System.Buffers.Binary;
using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Storage;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class ConsensusStateStoreTests
{
    [Test]
    public void SerializeDeserialize_RoundTripsHeadState()
    {
        var headRoot = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var state = new ConsensusHeadState(42, headRoot);

        var encoded = state.Serialize();
        var decoded = ConsensusHeadState.TryDeserialize(encoded, out var reloaded);

        Assert.That(decoded, Is.True);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.HeadSlot, Is.EqualTo(state.HeadSlot));
        Assert.That(reloaded.HeadRoot, Is.EqualTo(headRoot));
        Assert.That(reloaded.LatestJustifiedSlot, Is.EqualTo(state.HeadSlot));
        Assert.That(reloaded.LatestFinalizedSlot, Is.EqualTo(state.HeadSlot));
        Assert.That(reloaded.SafeTargetSlot, Is.EqualTo(state.HeadSlot));
        Assert.That(reloaded.LatestJustifiedRoot, Is.EqualTo(headRoot));
        Assert.That(reloaded.LatestFinalizedRoot, Is.EqualTo(headRoot));
        Assert.That(reloaded.SafeTargetRoot, Is.EqualTo(headRoot));
    }

    [Test]
    public void SerializeDeserialize_RoundTripsExtendedState()
    {
        var state = new ConsensusHeadState(
            headSlot: 64,
            headRoot: Enumerable.Repeat((byte)0xAA, 32).ToArray(),
            latestJustifiedSlot: 48,
            latestJustifiedRoot: Enumerable.Repeat((byte)0xBB, 32).ToArray(),
            latestFinalizedSlot: 32,
            latestFinalizedRoot: Enumerable.Repeat((byte)0xCC, 32).ToArray(),
            safeTargetSlot: 56,
            safeTargetRoot: Enumerable.Repeat((byte)0xDD, 32).ToArray());

        var encoded = state.Serialize();
        var decoded = ConsensusHeadState.TryDeserialize(encoded, out var reloaded);

        Assert.That(decoded, Is.True);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.HeadSlot, Is.EqualTo(64));
        Assert.That(reloaded.HeadRoot, Is.EqualTo(Enumerable.Repeat((byte)0xAA, 32).ToArray()));
        Assert.That(reloaded.LatestJustifiedSlot, Is.EqualTo(48));
        Assert.That(reloaded.LatestJustifiedRoot, Is.EqualTo(Enumerable.Repeat((byte)0xBB, 32).ToArray()));
        Assert.That(reloaded.LatestFinalizedSlot, Is.EqualTo(32));
        Assert.That(reloaded.LatestFinalizedRoot, Is.EqualTo(Enumerable.Repeat((byte)0xCC, 32).ToArray()));
        Assert.That(reloaded.SafeTargetSlot, Is.EqualTo(56));
        Assert.That(reloaded.SafeTargetRoot, Is.EqualTo(Enumerable.Repeat((byte)0xDD, 32).ToArray()));
    }

    [Test]
    public void TryDeserialize_ReturnsFalseForUnsupportedVersion()
    {
        var payload = new byte[13];
        payload[0] = 2;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1, 8), 10);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), 0);

        var decoded = ConsensusHeadState.TryDeserialize(payload, out var state);

        Assert.That(decoded, Is.False);
        Assert.That(state, Is.Null);
    }

    [Test]
    public void TryDeserialize_ReturnsFalseForInvalidLength()
    {
        var payload = new ConsensusHeadState(1, new byte[] { 0xAA, 0xBB, 0xCC }).Serialize();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), 5);

        var decoded = ConsensusHeadState.TryDeserialize(payload, out var state);

        Assert.That(decoded, Is.False);
        Assert.That(state, Is.Null);
    }

    [Test]
    public void SaveLoad_RoundTripsState()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var store = new ConsensusStateStore(keyValueStore);
        var expected = new ConsensusHeadState(
            headSlot: 128,
            headRoot: Enumerable.Repeat((byte)0x01, 32).ToArray(),
            latestJustifiedSlot: 120,
            latestJustifiedRoot: Enumerable.Repeat((byte)0x02, 32).ToArray(),
            latestFinalizedSlot: 96,
            latestFinalizedRoot: Enumerable.Repeat((byte)0x03, 32).ToArray(),
            safeTargetSlot: 124,
            safeTargetRoot: Enumerable.Repeat((byte)0x04, 32).ToArray());

        store.Save(expected);
        var loaded = store.TryLoad(out var reloaded);

        Assert.That(loaded, Is.True);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.HeadSlot, Is.EqualTo(expected.HeadSlot));
        Assert.That(reloaded.HeadRoot, Is.EqualTo(expected.HeadRoot));
        Assert.That(reloaded.LatestJustifiedSlot, Is.EqualTo(expected.LatestJustifiedSlot));
        Assert.That(reloaded.LatestJustifiedRoot, Is.EqualTo(expected.LatestJustifiedRoot));
        Assert.That(reloaded.LatestFinalizedSlot, Is.EqualTo(expected.LatestFinalizedSlot));
        Assert.That(reloaded.LatestFinalizedRoot, Is.EqualTo(expected.LatestFinalizedRoot));
        Assert.That(reloaded.SafeTargetSlot, Is.EqualTo(expected.SafeTargetSlot));
        Assert.That(reloaded.SafeTargetRoot, Is.EqualTo(expected.SafeTargetRoot));
    }

    [Test]
    public void SaveLoad_RoundTripsHeadChainStateSnapshot()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var store = new ConsensusStateStore(keyValueStore);
        var headRoot = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var state = new ConsensusHeadState(7, headRoot);
        var chainState = new State(
            new Config(123),
            new Slot(7),
            new BlockHeader(
                new Slot(7),
                1,
                new Bytes32(Enumerable.Repeat((byte)0x10, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x12, 32).ToArray())),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x21, 32).ToArray()), new Slot(6)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x22, 32).ToArray()), new Slot(5)),
            new[]
            {
                new Bytes32(Enumerable.Repeat((byte)0x31, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x32, 32).ToArray())
            },
            new[] { true, false, true },
            new[]
            {
                new Validator(new Bytes52(Enumerable.Repeat((byte)0x41, 52).ToArray()), 0),
                new Validator(new Bytes52(Enumerable.Repeat((byte)0x42, 52).ToArray()), 1)
            },
            new[]
            {
                new Bytes32(Enumerable.Repeat((byte)0x51, 32).ToArray())
            },
            new[] { true, false });

        store.Save(state, chainState);
        var loaded = store.TryLoad(out var reloadedState, out var reloadedChainState);

        Assert.That(loaded, Is.True);
        Assert.That(reloadedState, Is.Not.Null);
        Assert.That(reloadedState!.HeadSlot, Is.EqualTo(state.HeadSlot));
        Assert.That(reloadedChainState, Is.Not.Null);
        Assert.That(reloadedChainState!.Config.GenesisTime, Is.EqualTo(chainState.Config.GenesisTime));
        Assert.That(reloadedChainState.Slot.Value, Is.EqualTo(chainState.Slot.Value));
        Assert.That(reloadedChainState.LatestBlockHeader.ProposerIndex, Is.EqualTo(chainState.LatestBlockHeader.ProposerIndex));
        Assert.That(reloadedChainState.HistoricalBlockHashes.Count, Is.EqualTo(chainState.HistoricalBlockHashes.Count));
        Assert.That(reloadedChainState.Validators.Count, Is.EqualTo(chainState.Validators.Count));
        Assert.That(reloadedChainState.JustifiedSlots, Is.EqualTo(chainState.JustifiedSlots));
        Assert.That(reloadedChainState.JustificationsValidators, Is.EqualTo(chainState.JustificationsValidators));
    }

    [Test]
    public void Delete_RemovesPersistedState()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var store = new ConsensusStateStore(keyValueStore);

        store.Save(new ConsensusHeadState(7, new byte[] { 0xAB }));
        store.Delete();
        var loaded = store.TryLoad(out var reloaded);

        Assert.That(loaded, Is.False);
        Assert.That(reloaded, Is.Null);
    }
}
