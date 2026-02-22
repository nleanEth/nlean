using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.ForkChoice;

[TestFixture]
public sealed class ProtoArrayTests
{
    [Test]
    public void Constructor_RegistersGenesisNode()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, justifiedSlot: 0, finalizedSlot: 0);
        Assert.That(array.NodeCount, Is.EqualTo(1));
        Assert.That(array.ContainsBlock(genesis), Is.True);
    }

    [Test]
    public void RegisterBlock_AddsChildNode()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, justifiedSlot: 0, finalizedSlot: 0);
        var child = MakeRoot(0x02);
        array.RegisterBlock(child, genesis, 1, 0, 0);
        Assert.That(array.NodeCount, Is.EqualTo(2));
        Assert.That(array.ContainsBlock(child), Is.True);
    }

    [Test]
    public void RegisterBlock_DuplicateRoot_IsIgnored()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, justifiedSlot: 0, finalizedSlot: 0);
        array.RegisterBlock(genesis, Bytes32.Zero(), 0, 0, 0);
        Assert.That(array.NodeCount, Is.EqualTo(1));
    }

    [Test]
    public void RegisterBlock_UnknownParent_StillAddsNode()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, justifiedSlot: 0, finalizedSlot: 0);
        var orphan = MakeRoot(0x0F);
        array.RegisterBlock(orphan, MakeRoot(0xFF), 5, 0, 0);
        Assert.That(array.NodeCount, Is.EqualTo(2));
    }

    [Test]
    public void ApplyScoreChanges_PropagatesWeightsBottomUp()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        var deltas = new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 1
        };
        array.ApplyScoreChanges(deltas, 0, 0);

        Assert.That(array.GetWeight(b), Is.EqualTo(1));
        Assert.That(array.GetWeight(a), Is.EqualTo(1));
        Assert.That(array.GetWeight(genesis), Is.EqualTo(1));
    }

    [Test]
    public void ApplyScoreChanges_NegativeDelta_ReducesWeight()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);

        var key = ProtoArray.RootKey(a);
        array.ApplyScoreChanges(new Dictionary<string, long> { [key] = 3 }, 0, 0);
        array.ApplyScoreChanges(new Dictionary<string, long> { [key] = -1 }, 0, 0);

        Assert.That(array.GetWeight(a), Is.EqualTo(2));
    }

    [Test]
    public void ApplyScoreChanges_UpdatesBestChildAndBestDescendant()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        array.ApplyScoreChanges(
            new Dictionary<string, long> { [ProtoArray.RootKey(b)] = 1 }, 0, 0);

        // Genesis best descendant should be b (the leaf with weight)
        var genNode = array.GetNode(genesis);
        Assert.That(genNode, Is.Not.Null);
        Assert.That(genNode!.BestChild, Is.Not.Null);
        Assert.That(genNode.BestDescendant, Is.Not.Null);
    }

    [Test]
    public void FindHead_ReturnsGenesis_WhenNoVotes()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, 0, 0);
        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(genesis));
    }

    [Test]
    public void FindHead_FollowsBestDescendant()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.ApplyScoreChanges(
            new Dictionary<string, long> { [ProtoArray.RootKey(b)] = 1 }, 0, 0);

        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(b));
    }

    [Test]
    public void FindHead_PicksHeavierFork()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);
        array.ApplyScoreChanges(new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 2,
            [ProtoArray.RootKey(b)] = 1
        }, 0, 0);

        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(a));
    }

    [Test]
    public void FindHead_UnknownJustifiedRoot_ReturnsDefault()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, 0, 0);
        var unknown = MakeRoot(0xFF);
        Assert.That(array.FindHead(unknown, 0, 0), Is.EqualTo(default(Bytes32)));
    }

    [Test]
    public void GetSlot_ReturnsSlotForKnownBlock()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 5, 0, 0);

        Assert.That(array.GetSlot(a), Is.EqualTo(5UL));
        Assert.That(array.GetSlot(genesis), Is.EqualTo(0UL));
    }

    [Test]
    public void GetSlot_ReturnsNull_ForUnknownBlock()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, 0, 0);
        Assert.That(array.GetSlot(MakeRoot(0xFF)), Is.Null);
    }

    [Test]
    public void GetParentRoot_ReturnsParentForKnownBlock()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);

        Assert.That(array.GetParentRoot(a), Is.EqualTo(genesis));
    }

    [Test]
    public void Prune_RemovesAncestorsOfFinalizedRoot()
    {
        // genesis -> a -> b (finalized) -> c
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, b, 3, 0, 0);

        array.Prune(b);

        Assert.That(array.ContainsBlock(genesis), Is.False);
        Assert.That(array.ContainsBlock(a), Is.False);
        Assert.That(array.ContainsBlock(b), Is.True);
        Assert.That(array.ContainsBlock(c), Is.True);
        Assert.That(array.NodeCount, Is.EqualTo(2));
    }

    [Test]
    public void Prune_PreservesWeightsAndParentLinks()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        array.ApplyScoreChanges(
            new Dictionary<string, long> { [ProtoArray.RootKey(b)] = 5 }, 0, 0);
        array.Prune(a);

        Assert.That(array.GetWeight(b), Is.EqualTo(5));
        Assert.That(array.GetWeight(a), Is.EqualTo(5));
        Assert.That(array.GetParentRoot(b), Is.EqualTo(a));
    }

    [Test]
    public void Prune_UnknownRoot_DoesNothing()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, 0, 0);
        array.Prune(MakeRoot(0xFF));
        Assert.That(array.NodeCount, Is.EqualTo(1));
    }

    internal static Bytes32 MakeRoot(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());
}
