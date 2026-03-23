using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Storage;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class StateByRootStoreTests
{
    private static State CreateTestState()
    {
        return new State(
            new Config(1000),
            new Slot(42),
            new BlockHeader(
                new Slot(42),
                1,
                new Bytes32(Enumerable.Repeat((byte)0x10, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x12, 32).ToArray())),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x21, 32).ToArray()), new Slot(40)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x22, 32).ToArray()), new Slot(32)),
            new[]
            {
                new Bytes32(Enumerable.Repeat((byte)0x31, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x32, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x33, 32).ToArray())
            },
            new[] { true, false, true, true, false },
            new[]
            {
                new Validator(new Bytes52(Enumerable.Repeat((byte)0x41, 52).ToArray()), new Bytes52(Enumerable.Repeat((byte)0x41, 52).ToArray()), 0),
                new Validator(new Bytes52(Enumerable.Repeat((byte)0x42, 52).ToArray()), new Bytes52(Enumerable.Repeat((byte)0x42, 52).ToArray()), 1)
            },
            new[]
            {
                new Bytes32(Enumerable.Repeat((byte)0x51, 32).ToArray()),
                new Bytes32(Enumerable.Repeat((byte)0x52, 32).ToArray())
            },
            new[] { true, false, true });
    }

    [Test]
    public void SaveLoad_RoundTripsStateBySsz()
    {
        var store = new StateByRootStore(new InMemoryKeyValueStore());
        var blockRoot = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());
        var state = CreateTestState();

        store.Save(blockRoot, state);
        var loaded = store.TryLoad(blockRoot, out var reloaded);

        Assert.That(loaded, Is.True);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.Config.GenesisTime, Is.EqualTo(state.Config.GenesisTime));
        Assert.That(reloaded.Slot.Value, Is.EqualTo(state.Slot.Value));
        Assert.That(reloaded.LatestBlockHeader.Slot.Value, Is.EqualTo(state.LatestBlockHeader.Slot.Value));
        Assert.That(reloaded.LatestBlockHeader.ProposerIndex, Is.EqualTo(state.LatestBlockHeader.ProposerIndex));
        Assert.That(reloaded.LatestBlockHeader.ParentRoot.AsSpan().ToArray(), Is.EqualTo(state.LatestBlockHeader.ParentRoot.AsSpan().ToArray()));
        Assert.That(reloaded.LatestBlockHeader.StateRoot.AsSpan().ToArray(), Is.EqualTo(state.LatestBlockHeader.StateRoot.AsSpan().ToArray()));
        Assert.That(reloaded.LatestBlockHeader.BodyRoot.AsSpan().ToArray(), Is.EqualTo(state.LatestBlockHeader.BodyRoot.AsSpan().ToArray()));
        Assert.That(reloaded.LatestJustified.Slot.Value, Is.EqualTo(state.LatestJustified.Slot.Value));
        Assert.That(reloaded.LatestJustified.Root.AsSpan().ToArray(), Is.EqualTo(state.LatestJustified.Root.AsSpan().ToArray()));
        Assert.That(reloaded.LatestFinalized.Slot.Value, Is.EqualTo(state.LatestFinalized.Slot.Value));
        Assert.That(reloaded.LatestFinalized.Root.AsSpan().ToArray(), Is.EqualTo(state.LatestFinalized.Root.AsSpan().ToArray()));
        Assert.That(reloaded.HistoricalBlockHashes.Count, Is.EqualTo(state.HistoricalBlockHashes.Count));
        for (var i = 0; i < state.HistoricalBlockHashes.Count; i++)
            Assert.That(reloaded.HistoricalBlockHashes[i].AsSpan().ToArray(), Is.EqualTo(state.HistoricalBlockHashes[i].AsSpan().ToArray()));
        Assert.That(reloaded.JustifiedSlots, Is.EqualTo(state.JustifiedSlots));
        Assert.That(reloaded.Validators.Count, Is.EqualTo(state.Validators.Count));
        for (var i = 0; i < state.Validators.Count; i++)
        {
            Assert.That(reloaded.Validators[i].AttestationPubkey.AsSpan().ToArray(), Is.EqualTo(state.Validators[i].AttestationPubkey.AsSpan().ToArray()));
            Assert.That(reloaded.Validators[i].Index, Is.EqualTo(state.Validators[i].Index));
        }
        Assert.That(reloaded.JustificationsRoots.Count, Is.EqualTo(state.JustificationsRoots.Count));
        for (var i = 0; i < state.JustificationsRoots.Count; i++)
            Assert.That(reloaded.JustificationsRoots[i].AsSpan().ToArray(), Is.EqualTo(state.JustificationsRoots[i].AsSpan().ToArray()));
        Assert.That(reloaded.JustificationsValidators, Is.EqualTo(state.JustificationsValidators));
    }

    [Test]
    public void TryLoad_ReturnsFalseForMissing()
    {
        var store = new StateByRootStore(new InMemoryKeyValueStore());
        var root = new Bytes32(Enumerable.Repeat((byte)0xFF, 32).ToArray());

        var loaded = store.TryLoad(root, out var state);

        Assert.That(loaded, Is.False);
        Assert.That(state, Is.Null);
    }

    [Test]
    public void Delete_RemovesState()
    {
        var store = new StateByRootStore(new InMemoryKeyValueStore());
        var root = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());
        var state = CreateTestState();

        store.Save(root, state);
        store.Delete(root);

        Assert.That(store.TryLoad(root, out _), Is.False);
    }

    [Test]
    public void TryLoad_ReturnsFalseForCorruptPayload()
    {
        var kvStore = new InMemoryKeyValueStore();
        var store = new StateByRootStore(kvStore);
        var root = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());

        // Write garbage directly
        var key = System.Text.Encoding.ASCII.GetBytes($"consensus:state:{Convert.ToHexString(root.AsSpan())}");
        kvStore.Put(key, new byte[] { 0x01, 0x02, 0x03 });

        var loaded = store.TryLoad(root, out var state);

        Assert.That(loaded, Is.False);
        Assert.That(state, Is.Null);
    }
}
