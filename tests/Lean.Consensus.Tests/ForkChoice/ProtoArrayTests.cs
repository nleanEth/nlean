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

    // ========== FindHead: Tie-breaking ==========

    [Test]
    public void FindHead_EqualWeight_TieBreakByHigherRootHex()
    {
        // Two children of genesis with equal weight; higher hex root wins
        var genesis = MakeRoot(0x01);
        var low = MakeRoot(0x02);   // hex: 0202...
        var high = MakeRoot(0xAA);  // hex: AAAA... > 0202...
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(low, genesis, 1, 0, 0);
        array.RegisterBlock(high, genesis, 1, 0, 0);
        array.ApplyScoreChanges(new Dictionary<string, long>
        {
            [ProtoArray.RootKey(low)] = 1,
            [ProtoArray.RootKey(high)] = 1
        }, 0, 0);

        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(high));
    }

    [Test]
    public void FindHead_ZeroWeightEverywhere_TieBreakByRoot()
    {
        // No votes at all on a forked tree — still picks a head via tie-break
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0xBB);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);
        // Must call ApplyScoreChanges to compute BestChild/BestDescendant
        array.ApplyScoreChanges(new Dictionary<string, long>(), 0, 0);

        var head = array.FindHead(genesis, 0, 0);
        // With zero weight, tie-break picks higher hex root
        Assert.That(head, Is.EqualTo(b));
    }

    // ========== FindHead: Deep chain ==========

    [Test]
    public void FindHead_DeepChain_VoteOnLeafPropagates()
    {
        // genesis -> a -> b -> c -> d -> e (depth 5)
        var genesis = MakeRoot(0x01);
        var roots = Enumerable.Range(2, 5).Select(i => MakeRoot((byte)i)).ToArray();
        var array = new ProtoArray(genesis, 0, 0);
        var parent = genesis;
        for (int i = 0; i < roots.Length; i++)
        {
            array.RegisterBlock(roots[i], parent, (ulong)(i + 1), 0, 0);
            parent = roots[i];
        }

        var leaf = roots[^1]; // e
        array.ApplyScoreChanges(
            new Dictionary<string, long> { [ProtoArray.RootKey(leaf)] = 1 }, 0, 0);

        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(leaf));
        // All ancestors should have weight 1
        Assert.That(array.GetWeight(genesis), Is.EqualTo(1));
        Assert.That(array.GetWeight(roots[0]), Is.EqualTo(1));
    }

    // ========== Vote migration ==========

    [Test]
    public void ApplyScoreChanges_VoteMigration_ShiftsWeightBetweenForks()
    {
        // genesis -> a (fork1), genesis -> b (fork2)
        // Round 1: vote on a. Round 2: vote moves from a to b.
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);

        // Round 1: vote for a
        array.ApplyScoreChanges(
            new Dictionary<string, long> { [ProtoArray.RootKey(a)] = 1 }, 0, 0);
        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(a));

        // Round 2: move vote from a to b (delta: a -1, b +1)
        array.ApplyScoreChanges(new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = -1,
            [ProtoArray.RootKey(b)] = 1
        }, 0, 0);
        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(b));
        Assert.That(array.GetWeight(a), Is.EqualTo(0));
        Assert.That(array.GetWeight(b), Is.EqualTo(1));
    }

    [Test]
    public void ApplyScoreChanges_MultipleRounds_AccumulatesCorrectly()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);

        var key = ProtoArray.RootKey(a);
        array.ApplyScoreChanges(new Dictionary<string, long> { [key] = 5 }, 0, 0);
        array.ApplyScoreChanges(new Dictionary<string, long> { [key] = 3 }, 0, 0);
        array.ApplyScoreChanges(new Dictionary<string, long> { [key] = -2 }, 0, 0);

        Assert.That(array.GetWeight(a), Is.EqualTo(6)); // 5+3-2
        Assert.That(array.GetWeight(genesis), Is.EqualTo(6)); // propagated
    }

    // ========== Viability filtering ==========

    [Test]
    public void FindHead_NonViableNode_Skipped()
    {
        // genesis (justified=0,finalized=0) -> a (justified=5,finalized=0) -> b (justified=0,finalized=0)
        // Store says justified=5, finalized=0.
        // b has justified=0 which is < 5, so b is non-viable. Head should be a.
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 5, 0);   // justified=5
        array.RegisterBlock(b, a, 2, 0, 0);          // justified=0, not viable when store justified=5

        array.ApplyScoreChanges(
            new Dictionary<string, long> { [ProtoArray.RootKey(b)] = 10 }, 5, 0);

        // b has weight but is non-viable for justified=5
        // a should be head (it has justified=5 >= store justified=5)
        var head = array.FindHead(genesis, 5, 0);
        Assert.That(head, Is.EqualTo(a));
    }

    [Test]
    public void FindHead_HigherJustifiedSlot_OverridesWeight()
    {
        // Two forks from genesis:
        //   fork1: a (justified=5) with 1 vote
        //   fork2: b (justified=0) with 10 votes
        // Store justified=5 -> b non-viable, a wins despite less weight
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 5, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);

        array.ApplyScoreChanges(new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 1,
            [ProtoArray.RootKey(b)] = 10
        }, 5, 0);

        Assert.That(array.FindHead(genesis, 5, 0), Is.EqualTo(a));
    }

    // ========== Prune: advanced scenarios ==========

    [Test]
    public void Prune_RemovesDanglingBranches()
    {
        // genesis -> a -> b (finalized), genesis -> c (dangling fork)
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04); // not on finalized path
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, genesis, 1, 0, 0); // fork off genesis

        array.Prune(b);

        Assert.That(array.ContainsBlock(b), Is.True);
        Assert.That(array.ContainsBlock(genesis), Is.False);
        Assert.That(array.ContainsBlock(a), Is.False);
        Assert.That(array.ContainsBlock(c), Is.False); // dangling fork removed
        Assert.That(array.NodeCount, Is.EqualTo(1));    // only b remains
    }

    [Test]
    public void Prune_ThenRegisterBlock_ThenFindHead_Works()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        array.Prune(a); // removes genesis

        // Add new block after prune
        var c = MakeRoot(0x04);
        array.RegisterBlock(c, b, 3, 0, 0);
        array.ApplyScoreChanges(
            new Dictionary<string, long> { [ProtoArray.RootKey(c)] = 1 }, 0, 0);

        Assert.That(array.FindHead(a, 0, 0), Is.EqualTo(c));
        Assert.That(array.NodeCount, Is.EqualTo(3)); // a, b, c
    }

    [Test]
    public void Prune_SequentialPrunes_WorkCorrectly()
    {
        // genesis -> a -> b -> c -> d
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var d = MakeRoot(0x05);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, b, 3, 0, 0);
        array.RegisterBlock(d, c, 4, 0, 0);

        array.Prune(a);
        Assert.That(array.NodeCount, Is.EqualTo(4)); // a,b,c,d

        array.Prune(c);
        Assert.That(array.NodeCount, Is.EqualTo(2)); // c,d
        Assert.That(array.ContainsBlock(a), Is.False);
        Assert.That(array.ContainsBlock(b), Is.False);
        Assert.That(array.ContainsBlock(c), Is.True);
        Assert.That(array.ContainsBlock(d), Is.True);
    }

    // ========== Justified/finalized checkpoint advancement (3SF) ==========

    [Test]
    public void FindHead_JustifiedSlotAdvancement_SwitchesHead()
    {
        // Simulate 3SF: justified slot advances quickly
        // fork1: genesis -> a (justified=1) -> c (justified=2)
        // fork2: genesis -> b (justified=0) with more weight
        // Initially justified=0, justified root=genesis: b wins on weight
        // After justified advances to 1, justified root=a: only fork1 descendants are viable
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 1, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);
        array.RegisterBlock(c, a, 2, 2, 0);

        // With justified=0, root=genesis: both forks viable, b has more weight
        array.ApplyScoreChanges(new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 5,
            [ProtoArray.RootKey(c)] = 1
        }, 0, 0);
        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(b));

        // Justified advances to slot 1, root=a: b is not a descendant of a,
        // so now we search from a. c has justified=2 >= 1, so c is viable.
        array.ApplyScoreChanges(new Dictionary<string, long>(), 1, 0);
        Assert.That(array.FindHead(a, 1, 0), Is.EqualTo(c));
    }

    // ========== BestChild/BestDescendant detailed ==========

    [Test]
    public void BestDescendant_PointsToDeepestViableLeaf()
    {
        // genesis -> a -> b -> c
        // Vote on c. Genesis.BestDescendant should be c (not a or b).
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, b, 3, 0, 0);

        array.ApplyScoreChanges(
            new Dictionary<string, long> { [ProtoArray.RootKey(c)] = 1 }, 0, 0);

        var genNode = array.GetNode(genesis)!;
        var bestDescRoot = array.GetNode(genesis) is { BestDescendant: { } bd }
            ? array.GetNodeByIndex(bd)?.Root
            : null;
        Assert.That(bestDescRoot, Is.EqualTo(c));
    }

    [Test]
    public void BestChild_SwitchesWhenWeightChanges()
    {
        // genesis -> a (fork1), genesis -> b (fork2)
        // Initially a has more weight, then b overtakes
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);

        array.ApplyScoreChanges(new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 3,
            [ProtoArray.RootKey(b)] = 1
        }, 0, 0);
        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(a));

        // b overtakes: +5 to b
        array.ApplyScoreChanges(new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 5
        }, 0, 0);
        Assert.That(array.FindHead(genesis, 0, 0), Is.EqualTo(b));
    }

    internal static Bytes32 MakeRoot(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());
}
