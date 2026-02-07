using System.Buffers.Binary;
using Lean.Consensus;
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
        var expected = new ConsensusHeadState(128, new byte[] { 0x01, 0x02, 0x03, 0xFE });

        store.Save(expected);
        var loaded = store.TryLoad(out var reloaded);

        Assert.That(loaded, Is.True);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.HeadSlot, Is.EqualTo(expected.HeadSlot));
        Assert.That(reloaded.HeadRoot, Is.EqualTo(expected.HeadRoot));
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
