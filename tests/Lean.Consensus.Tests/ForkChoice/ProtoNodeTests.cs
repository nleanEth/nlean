using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.ForkChoice;

[TestFixture]
public sealed class ProtoNodeTests
{
    [Test]
    public void Constructor_SetsAllFields()
    {
        var root = Bytes32.Zero();
        var parentRoot = new Bytes32(Enumerable.Repeat((byte)0x01, 32).ToArray());
        var node = new ProtoNode(
            root: root, parentRoot: parentRoot, slot: 5UL,
            parentIndex: 0, justifiedSlot: 0UL, finalizedSlot: 0UL);

        Assert.That(node.Root, Is.EqualTo(root));
        Assert.That(node.ParentRoot, Is.EqualTo(parentRoot));
        Assert.That(node.Slot, Is.EqualTo(5UL));
        Assert.That(node.ParentIndex, Is.EqualTo(0));
        Assert.That(node.JustifiedSlot, Is.EqualTo(0UL));
        Assert.That(node.FinalizedSlot, Is.EqualTo(0UL));
        Assert.That(node.Weight, Is.EqualTo(0L));
        Assert.That(node.BestChild, Is.Null);
        Assert.That(node.BestDescendant, Is.Null);
    }

    [Test]
    public void MutableFields_CanBeUpdated()
    {
        var node = new ProtoNode(Bytes32.Zero(), Bytes32.Zero(), 1, null, 0, 0);
        node.Weight = 42;
        node.BestChild = 3;
        node.BestDescendant = 7;
        node.ParentIndex = 1;

        Assert.That(node.Weight, Is.EqualTo(42));
        Assert.That(node.BestChild, Is.EqualTo(3));
        Assert.That(node.BestDescendant, Is.EqualTo(7));
        Assert.That(node.ParentIndex, Is.EqualTo(1));
    }
}
