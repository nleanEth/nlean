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

    internal static Bytes32 MakeRoot(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());
}
