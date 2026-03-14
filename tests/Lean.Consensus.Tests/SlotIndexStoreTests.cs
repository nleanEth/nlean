using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Storage;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SlotIndexStoreTests
{
    [Test]
    public void SaveLoad_RoundTripsSlotToRoot()
    {
        var store = new SlotIndexStore(new InMemoryKeyValueStore());
        var root = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());

        store.Save(100, root);
        var loaded = store.TryLoad(100, out var reloaded);

        Assert.That(loaded, Is.True);
        Assert.That(reloaded.AsSpan().ToArray(), Is.EqualTo(root.AsSpan().ToArray()));
    }

    [Test]
    public void TryLoad_ReturnsFalseForMissing()
    {
        var store = new SlotIndexStore(new InMemoryKeyValueStore());

        var loaded = store.TryLoad(999, out _);

        Assert.That(loaded, Is.False);
    }

    [Test]
    public void DeleteBelow_RemovesEntriesBelowCutoff()
    {
        var store = new SlotIndexStore(new InMemoryKeyValueStore());
        var root1 = new Bytes32(Enumerable.Repeat((byte)0x01, 32).ToArray());
        var root2 = new Bytes32(Enumerable.Repeat((byte)0x02, 32).ToArray());
        var root3 = new Bytes32(Enumerable.Repeat((byte)0x03, 32).ToArray());

        store.Save(10, root1);
        store.Save(20, root2);
        store.Save(30, root3);

        store.DeleteBelow(20);

        Assert.That(store.TryLoad(10, out _), Is.False);
        Assert.That(store.TryLoad(20, out _), Is.True);
        Assert.That(store.TryLoad(30, out _), Is.True);
    }

    [Test]
    public void GetEntriesBelow_ReturnsOnlyEntriesBelowCutoff()
    {
        var store = new SlotIndexStore(new InMemoryKeyValueStore());
        var root1 = new Bytes32(Enumerable.Repeat((byte)0x01, 32).ToArray());
        var root2 = new Bytes32(Enumerable.Repeat((byte)0x02, 32).ToArray());
        var root3 = new Bytes32(Enumerable.Repeat((byte)0x03, 32).ToArray());

        store.Save(10, root1);
        store.Save(20, root2);
        store.Save(30, root3);

        var entries = store.GetEntriesBelow(25);

        Assert.That(entries.Count, Is.EqualTo(2));
        Assert.That(entries[0].Slot, Is.EqualTo(10));
        Assert.That(entries[1].Slot, Is.EqualTo(20));
    }
}
