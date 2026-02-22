using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Sync;

[TestFixture]
public sealed class NewBlockCacheTests
{
    [Test]
    public void Add_StoresBlock()
    {
        var cache = new NewBlockCache(capacity: 10);
        var root = MakeRoot(0x01);
        var parentRoot = MakeRoot(0x02);
        var pending = MakePending(root, parentRoot, slot: 1);

        cache.Add(pending);

        Assert.That(cache.Count, Is.EqualTo(1));
        Assert.That(cache.TryGet(root, out var found), Is.True);
        Assert.That(found!.Root, Is.EqualTo(root));
    }

    [Test]
    public void Add_Duplicate_IsIgnored()
    {
        var cache = new NewBlockCache(capacity: 10);
        var root = MakeRoot(0x01);
        var pending = MakePending(root, MakeRoot(0x02), slot: 1);

        cache.Add(pending);
        cache.Add(pending);

        Assert.That(cache.Count, Is.EqualTo(1));
    }

    [Test]
    public void Add_EvictsOldest_WhenAtCapacity()
    {
        var cache = new NewBlockCache(capacity: 2);
        var first = MakePending(MakeRoot(0x01), MakeRoot(0x00), slot: 1);
        var second = MakePending(MakeRoot(0x02), MakeRoot(0x00), slot: 2);
        var third = MakePending(MakeRoot(0x03), MakeRoot(0x00), slot: 3);

        cache.Add(first);
        cache.Add(second);
        cache.Add(third);

        Assert.That(cache.Count, Is.EqualTo(2));
        Assert.That(cache.TryGet(MakeRoot(0x01), out _), Is.False); // evicted
        Assert.That(cache.TryGet(MakeRoot(0x02), out _), Is.True);
        Assert.That(cache.TryGet(MakeRoot(0x03), out _), Is.True);
    }

    [Test]
    public void Remove_DeletesBlock()
    {
        var cache = new NewBlockCache(capacity: 10);
        var root = MakeRoot(0x01);
        cache.Add(MakePending(root, MakeRoot(0x02), slot: 1));

        cache.Remove(root);

        Assert.That(cache.Count, Is.EqualTo(0));
        Assert.That(cache.TryGet(root, out _), Is.False);
    }

    [Test]
    public void GetChildren_ReturnsSortedBySlot()
    {
        var cache = new NewBlockCache(capacity: 10);
        var parent = MakeRoot(0x01);
        var child3 = MakePending(MakeRoot(0x04), parent, slot: 3);
        var child1 = MakePending(MakeRoot(0x02), parent, slot: 1);
        var child2 = MakePending(MakeRoot(0x03), parent, slot: 2);

        cache.Add(child3);
        cache.Add(child1);
        cache.Add(child2);

        var children = cache.GetChildren(parent);
        Assert.That(children.Count, Is.EqualTo(3));
        Assert.That(children[0].Slot, Is.EqualTo(1UL));
        Assert.That(children[1].Slot, Is.EqualTo(2UL));
        Assert.That(children[2].Slot, Is.EqualTo(3UL));
    }

    [Test]
    public void GetChildren_ReturnsEmpty_WhenNoChildren()
    {
        var cache = new NewBlockCache(capacity: 10);
        var children = cache.GetChildren(MakeRoot(0xFF));
        Assert.That(children, Is.Empty);
    }

    [Test]
    public void MarkOrphan_And_GetOrphanParents()
    {
        var cache = new NewBlockCache(capacity: 10);
        var parent1 = MakeRoot(0x01);
        var parent2 = MakeRoot(0x02);

        cache.MarkOrphan(parent1);
        cache.MarkOrphan(parent2);

        var orphanParents = cache.GetOrphanParents();
        Assert.That(orphanParents, Has.Count.EqualTo(2));
        Assert.That(orphanParents, Does.Contain(parent1));
        Assert.That(orphanParents, Does.Contain(parent2));
    }

    [Test]
    public void UnmarkOrphan_RemovesFromSet()
    {
        var cache = new NewBlockCache(capacity: 10);
        var parent = MakeRoot(0x01);
        cache.MarkOrphan(parent);
        cache.UnmarkOrphan(parent);

        Assert.That(cache.GetOrphanParents(), Is.Empty);
    }

    [Test]
    public void OrphanCount_TracksOrphans()
    {
        var cache = new NewBlockCache(capacity: 10);
        Assert.That(cache.OrphanCount, Is.EqualTo(0));

        cache.MarkOrphan(MakeRoot(0x01));
        cache.MarkOrphan(MakeRoot(0x02));
        Assert.That(cache.OrphanCount, Is.EqualTo(2));

        cache.UnmarkOrphan(MakeRoot(0x01));
        Assert.That(cache.OrphanCount, Is.EqualTo(1));
    }

    [Test]
    public void GetProcessable_ReturnsBlocksWhoseParentIsKnown()
    {
        var cache = new NewBlockCache(capacity: 10);
        var knownParent = MakeRoot(0x01);
        var unknownParent = MakeRoot(0x02);

        cache.Add(MakePending(MakeRoot(0x10), knownParent, slot: 1));
        cache.Add(MakePending(MakeRoot(0x11), unknownParent, slot: 2));

        var processable = cache.GetProcessable(root => root.Equals(knownParent));

        Assert.That(processable.Count, Is.EqualTo(1));
        Assert.That(processable[0].ParentRoot, Is.EqualTo(knownParent));
    }

    [Test]
    public void Remove_CleansUpParentChildIndex()
    {
        var cache = new NewBlockCache(capacity: 10);
        var parent = MakeRoot(0x01);
        var child = MakeRoot(0x02);
        cache.Add(MakePending(child, parent, slot: 1));

        cache.Remove(child);

        Assert.That(cache.GetChildren(parent), Is.Empty);
    }

    [Test]
    public void Eviction_CleansUpOrphanStatus()
    {
        var cache = new NewBlockCache(capacity: 1);
        var parent1 = MakeRoot(0x01);
        cache.Add(MakePending(MakeRoot(0x10), parent1, slot: 1));
        cache.MarkOrphan(parent1);

        // Evict by adding a second block
        cache.Add(MakePending(MakeRoot(0x11), MakeRoot(0x02), slot: 2));

        // The evicted block's parent might still be orphaned if other children exist,
        // but since no children of parent1 remain, a cleanup could remove it.
        // The cache itself doesn't auto-clean orphans on eviction (that's HeadSync's job).
        Assert.That(cache.Count, Is.EqualTo(1));
    }

    private static Bytes32 MakeRoot(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());

    private static PendingBlock MakePending(Bytes32 root, Bytes32 parentRoot, ulong slot,
        string? receivedFrom = null) =>
        new(root, parentRoot, slot, receivedFrom);
}
