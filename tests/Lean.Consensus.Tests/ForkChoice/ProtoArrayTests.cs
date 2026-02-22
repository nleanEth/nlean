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

    internal static Bytes32 MakeRoot(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());
}
