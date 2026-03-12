using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Storage;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class StateRootIndexStoreTests
{
    [Test]
    public void SaveLoad_RoundTripsStateRootToBlockRoot()
    {
        var store = new StateRootIndexStore(new InMemoryKeyValueStore());
        var stateRoot = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());
        var blockRoot = new Bytes32(Enumerable.Repeat((byte)0xBB, 32).ToArray());

        store.Save(stateRoot, blockRoot);
        var loaded = store.TryLoad(stateRoot, out var reloaded);

        Assert.That(loaded, Is.True);
        Assert.That(reloaded.AsSpan().ToArray(), Is.EqualTo(blockRoot.AsSpan().ToArray()));
    }

    [Test]
    public void TryLoad_ReturnsFalseForMissing()
    {
        var store = new StateRootIndexStore(new InMemoryKeyValueStore());
        var stateRoot = new Bytes32(Enumerable.Repeat((byte)0xFF, 32).ToArray());

        var loaded = store.TryLoad(stateRoot, out _);

        Assert.That(loaded, Is.False);
    }
}
