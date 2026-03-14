using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Storage;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class BlockByRootStoreTests
{
    [Test]
    public void SaveLoad_RoundTripsPayloadByRoot()
    {
        var store = new BlockByRootStore(new InMemoryKeyValueStore());
        var root = new Bytes32(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());
        var payload = new byte[] { 0x01, 0x02, 0x03, 0xFF };

        store.Save(root, payload);

        var loaded = store.TryLoad(root, out var reloaded);
        Assert.That(loaded, Is.True);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!, Is.EqualTo(payload));
    }

    [Test]
    public void TryLoad_ReturnsFalseForMissingRoot()
    {
        var store = new BlockByRootStore(new InMemoryKeyValueStore());
        var root = new Bytes32(Enumerable.Repeat((byte)0xAB, 32).ToArray());

        var loaded = store.TryLoad(root, out var payload);

        Assert.That(loaded, Is.False);
        Assert.That(payload, Is.Null);
    }

    [Test]
    public void Delete_RemovesBlock()
    {
        var store = new BlockByRootStore(new InMemoryKeyValueStore());
        var root = new Bytes32(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        store.Save(root, payload);
        store.Delete(root);

        Assert.That(store.TryLoad(root, out _), Is.False);
    }
}
