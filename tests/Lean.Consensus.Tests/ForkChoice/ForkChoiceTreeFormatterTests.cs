using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.ForkChoice;

[TestFixture]
public class ForkChoiceTreeFormatterTests
{
    private static Bytes32 Root(byte b) => new(Enumerable.Repeat(b, 32).ToArray());

    [Test]
    public void EmptyBlocks_ReturnsEmptyMessage()
    {
        var result = ForkChoiceTreeFormatter.Format(
            Array.Empty<(Bytes32, ulong, Bytes32, ulong, long)>(),
            Root(1), Root(1), 0, Root(1), 0, Root(1));

        Assert.That(result, Does.Contain("Fork Choice Tree:"));
        Assert.That(result, Does.Contain("(empty)"));
    }

    [Test]
    public void LinearChain_ShowsAllNodes()
    {
        var root = Root(1);
        var a = Root(2);
        var b = Root(3);
        var nodes = new (Bytes32, ulong, Bytes32, ulong, long)[]
        {
            (root, 0, Bytes32.Zero(), 0, 0),
            (a, 1, root, 0, 0),
            (b, 2, a, 0, 0),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, b, root, 0, root, 0, root);

        Assert.That(result, Does.Contain("(0)"));
        Assert.That(result, Does.Contain("(1)"));
        Assert.That(result, Does.Contain("(2)"));
        Assert.That(result, Does.Contain("*"));
    }

    [Test]
    public void Fork_ShowsBranches()
    {
        var root = Root(1);
        var a = Root(2);
        var b = Root(3);
        var c = Root(4);
        var d = Root(5);
        var nodes = new (Bytes32, ulong, Bytes32, ulong, long)[]
        {
            (root, 0, Bytes32.Zero(), 0, 0),
            (a, 1, root, 0, 0),
            (b, 2, a, 0, 0),
            (c, 3, b, 0, 3),
            (d, 3, b, 0, 1),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, c, root, 0, root, 0, root);

        Assert.That(result, Does.Contain("2 branches"));
        Assert.That(result, Does.Contain("[w:3]"));
        Assert.That(result, Does.Contain("[w:1]"));
    }

    [Test]
    public void MissingSingleSlot_ShowsGapIndicator()
    {
        var root = Root(1);
        var a = Root(2);
        var nodes = new (Bytes32, ulong, Bytes32, ulong, long)[]
        {
            (root, 0, Bytes32.Zero(), 0, 0),
            (a, 2, root, 0, 0),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, a, root, 0, root, 0, root);

        Assert.That(result, Does.Contain("[ ]"));
    }

    [Test]
    public void MissingMultipleSlots_ShowsGapCount()
    {
        var root = Root(1);
        var a = Root(2);
        var nodes = new (Bytes32, ulong, Bytes32, ulong, long)[]
        {
            (root, 0, Bytes32.Zero(), 0, 0),
            (a, 4, root, 0, 0),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, a, root, 0, root, 0, root);

        Assert.That(result, Does.Contain("[3]"));
    }

    [Test]
    public void DepthTruncation_ShowsEllipsis()
    {
        var roots = Enumerable.Range(1, 25).Select(i => Root((byte)i)).ToArray();
        var nodes = new List<(Bytes32, ulong, Bytes32, ulong, long)>
        {
            (roots[0], 0, Bytes32.Zero(), 0, 0)
        };
        for (int i = 1; i < 25; i++)
        {
            nodes.Add((roots[i], (ulong)i, roots[i - 1], 0, 0));
        }

        var result = ForkChoiceTreeFormatter.Format(
            nodes, roots[24], roots[0], 0, roots[0], 0, roots[0]);

        Assert.That(result, Does.Contain("..."));
    }

    [Test]
    public void FinalizedAdvanced_TreeStartsFromFinalizedRoot()
    {
        var genesis = Root(1);
        var a = Root(2);
        var b = Root(3);
        var c = Root(4);
        var d = Root(5);
        var nodes = new (Bytes32, ulong, Bytes32, ulong, long)[]
        {
            (genesis, 0, Bytes32.Zero(), 0, 0),
            (a, 1, genesis, 0, 0),
            (b, 2, a, 0, 0),
            (c, 3, b, 0, 0),
            (d, 4, c, 0, 0),
        };

        // Finalized at slot 2 (root = b). Tree should start from b, not genesis.
        var result = ForkChoiceTreeFormatter.Format(
            nodes, d, c, 3, b, 2, b);

        var lineWithTrunk = result.Split('\n').First(l => l.Contains("(2)"));
        Assert.That(lineWithTrunk, Does.Contain("(2)"), "Trunk should start at finalized slot");
        Assert.That(lineWithTrunk, Does.Not.Contain("(0)"), "Tree should not include pre-finalization genesis");
        Assert.That(lineWithTrunk, Does.Not.Contain("(1)"), "Tree should not include pre-finalization slot 1");
    }

    [Test]
    public void NestedFork_ShowsMultipleBranchLevels()
    {
        var root = Root(1);
        var a = Root(2);
        var b = Root(3);
        var c = Root(4);
        var d = Root(5);
        var e = Root(6);
        var f = Root(7);
        var g = Root(8);
        var nodes = new (Bytes32, ulong, Bytes32, ulong, long)[]
        {
            (root, 0, Bytes32.Zero(), 0, 0),
            (a, 1, root, 0, 0),
            (b, 2, a, 0, 0),
            (c, 3, b, 0, 5),
            (d, 3, b, 0, 2),
            (e, 4, c, 0, 4),
            (f, 5, e, 0, 4),
            (g, 5, e, 0, 1),
        };

        var result = ForkChoiceTreeFormatter.Format(
            nodes, f, root, 0, root, 0, root);

        var branchCount = result.Split("2 branches").Length - 1;
        Assert.That(branchCount, Is.EqualTo(2), "Should show both outer and inner fork");
    }
}
