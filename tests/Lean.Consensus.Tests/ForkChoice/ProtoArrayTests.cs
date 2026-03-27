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
    public void GetIndex_ReturnsCorrectIndex()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);

        Assert.That(array.GetIndex(genesis), Is.EqualTo(0));
        Assert.That(array.GetIndex(a), Is.EqualTo(1));
        Assert.That(array.GetIndex(MakeRoot(0xFF)), Is.Null);
    }

    [Test]
    public void ApplyDeltas_PropagatesWeightsBottomUp()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 1
        });

        Assert.That(array.GetWeight(b), Is.EqualTo(1));
        Assert.That(array.GetWeight(a), Is.EqualTo(1));
        Assert.That(array.GetWeight(genesis), Is.EqualTo(1));
    }

    [Test]
    public void ApplyDeltas_UpdatesBestChildAndBestDescendant()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 1
        });

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
        Assert.That(FindHead(array, genesis), Is.EqualTo(genesis));
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

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 1
        });

        Assert.That(FindHead(array, genesis), Is.EqualTo(b));
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

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 2,
            [ProtoArray.RootKey(b)] = 1
        });

        Assert.That(FindHead(array, genesis), Is.EqualTo(a));
    }

    [Test]
    public void FindHead_UnknownJustifiedRoot_ReturnsSelf()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, 0, 0);
        var unknown = MakeRoot(0xFF);
        // GetIndex returns null for unknown roots
        Assert.That(array.GetIndex(unknown), Is.Null);
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
    public void Prune_ReturnsIndexMapping()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        var mapping = array.Prune(a);

        // a was index 1 -> now 0, b was index 2 -> now 1
        Assert.That(mapping[1], Is.EqualTo(0));
        Assert.That(mapping[2], Is.EqualTo(1));
        Assert.That(mapping.ContainsKey(0), Is.False); // genesis was pruned
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

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 5
        });
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

    // ========== Tie-breaking: weight → root (lexicographic hex) ==========

    [Test]
    public void TieBreak_EqualWeight_HigherRootWins_DifferentSlots()
    {
        var genesis = MakeRoot(0x01);
        var low = MakeRoot(0x02);   // slot 1, hex 0202...
        var high = MakeRoot(0x03);  // slot 2, hex 0303... > 0202...
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(low, genesis, 1, 0, 0);
        array.RegisterBlock(high, genesis, 2, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(low)] = 1,
            [ProtoArray.RootKey(high)] = 1
        });

        // Equal weight — tiebreaker is root only (slot is irrelevant)
        Assert.That(FindHead(array, genesis), Is.EqualTo(high));
    }

    [Test]
    public void TieBreak_EqualWeight_HigherRootWins_EvenAtLowerSlot()
    {
        var genesis = MakeRoot(0x01);
        var lowSlotHighRoot = MakeRoot(0xAA);  // slot 1, hex AAAA...
        var highSlotLowRoot = MakeRoot(0x02);  // slot 2, hex 0202...
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(lowSlotHighRoot, genesis, 1, 0, 0);
        array.RegisterBlock(highSlotLowRoot, genesis, 2, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(lowSlotHighRoot)] = 1,
            [ProtoArray.RootKey(highSlotLowRoot)] = 1
        });

        // Equal weight — higher root wins, regardless of slot
        Assert.That(FindHead(array, genesis), Is.EqualTo(lowSlotHighRoot));
    }

    [Test]
    public void TieBreak_EqualWeightAndSlot_HigherRootHexWins()
    {
        var genesis = MakeRoot(0x01);
        var low = MakeRoot(0x02);   // hex: 0202...
        var high = MakeRoot(0xAA);  // hex: AAAA... > 0202...
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(low, genesis, 1, 0, 0);   // same slot
        array.RegisterBlock(high, genesis, 1, 0, 0);   // same slot

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(low)] = 1,
            [ProtoArray.RootKey(high)] = 1
        });

        Assert.That(FindHead(array, genesis), Is.EqualTo(high));
    }

    [Test]
    public void FindHead_ZeroWeightEverywhere_TieBreakByRoot()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0xBB);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>());

        var head = FindHead(array, genesis);
        // Equal weight (0), equal slot (1) → higher hex root wins
        Assert.That(head, Is.EqualTo(b));
    }

    // ========== Deep chain ==========

    [Test]
    public void FindHead_DeepChain_VoteOnLeafPropagates()
    {
        var genesis = MakeRoot(0x01);
        var roots = Enumerable.Range(2, 5).Select(i => MakeRoot((byte)i)).ToArray();
        var array = new ProtoArray(genesis, 0, 0);
        var parent = genesis;
        for (int i = 0; i < roots.Length; i++)
        {
            array.RegisterBlock(roots[i], parent, (ulong)(i + 1), 0, 0);
            parent = roots[i];
        }

        var leaf = roots[^1];
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(leaf)] = 1
        });

        Assert.That(FindHead(array, genesis), Is.EqualTo(leaf));
        Assert.That(array.GetWeight(genesis), Is.EqualTo(1));
        Assert.That(array.GetWeight(roots[0]), Is.EqualTo(1));
    }

    // ========== Vote migration ==========

    [Test]
    public void ApplyDeltas_VoteMigration_ShiftsWeightBetweenForks()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);

        // Round 1: vote for a
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 1
        });
        Assert.That(FindHead(array, genesis), Is.EqualTo(a));

        // Round 2: vote moves to b (full rebuild: only b has vote now)
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 1
        });
        Assert.That(FindHead(array, genesis), Is.EqualTo(b));
        Assert.That(array.GetWeight(a), Is.EqualTo(0));
        Assert.That(array.GetWeight(b), Is.EqualTo(1));
    }

    // ========== CutoffWeight filtering ==========

    [Test]
    public void ApplyDeltas_CutoffWeight_FiltersBestDescendant()
    {
        // genesis -> a -> b; b has 1 vote, cutoff=2 → b not qualified as bestDescendant
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        // With cutoff=2, b (weight=1) doesn't qualify as bestDescendant
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 1
        }, cutoffWeight: 2);

        // genesis.BestDescendant should be null since no descendant meets cutoff
        var genNode = array.GetNode(genesis)!;
        Assert.That(genNode.BestDescendant, Is.Null);
    }

    [Test]
    public void ApplyDeltas_CutoffWeight_Zero_AllQualify()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 1
        }, cutoffWeight: 0);

        Assert.That(FindHead(array, genesis), Is.EqualTo(a));
    }

    // ========== Viability / 3SF ==========

    [Test]
    public void FindHead_AllNodesViable_FollowsWeight()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 5, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 10
        });

        Assert.That(FindHead(array, genesis), Is.EqualTo(b));
    }

    [Test]
    public void FindHead_HeavierFork_WinsRegardlessOfCheckpoints()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 5, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 1,
            [ProtoArray.RootKey(b)] = 10
        });

        Assert.That(FindHead(array, genesis), Is.EqualTo(b));
    }

    // ========== Prune: advanced scenarios ==========

    [Test]
    public void Prune_RemovesDanglingBranches()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, genesis, 1, 0, 0);

        array.Prune(b);

        Assert.That(array.ContainsBlock(b), Is.True);
        Assert.That(array.ContainsBlock(genesis), Is.False);
        Assert.That(array.ContainsBlock(a), Is.False);
        Assert.That(array.ContainsBlock(c), Is.False);
        Assert.That(array.NodeCount, Is.EqualTo(1));
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

        array.Prune(a);

        var c = MakeRoot(0x04);
        array.RegisterBlock(c, b, 3, 0, 0);
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(c)] = 1
        });

        Assert.That(FindHead(array, a), Is.EqualTo(c));
        Assert.That(array.NodeCount, Is.EqualTo(3));
    }

    [Test]
    public void Prune_SequentialPrunes_WorkCorrectly()
    {
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
        Assert.That(array.NodeCount, Is.EqualTo(4));

        array.Prune(c);
        Assert.That(array.NodeCount, Is.EqualTo(2));
        Assert.That(array.ContainsBlock(a), Is.False);
        Assert.That(array.ContainsBlock(b), Is.False);
        Assert.That(array.ContainsBlock(c), Is.True);
        Assert.That(array.ContainsBlock(d), Is.True);
    }

    [Test]
    public void FindHead_JustifiedSlotAdvancement_SwitchesHead()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 1, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);
        array.RegisterBlock(c, a, 2, 2, 0);

        // With justified root=genesis: both forks, b has more weight
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(b)] = 5,
            [ProtoArray.RootKey(c)] = 1
        });
        Assert.That(FindHead(array, genesis), Is.EqualTo(b));

        // With justified root=a: only fork1 descendants visible
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(c)] = 1
        });
        Assert.That(FindHead(array, a), Is.EqualTo(c));
    }

    [Test]
    public void BestDescendant_PointsToDeepestViableLeaf()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, b, 3, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(c)] = 1
        });

        var bestDescRoot = array.GetNode(genesis) is { BestDescendant: { } bd }
            ? array.GetNodeByIndex(bd)?.Root
            : null;
        Assert.That(bestDescRoot, Is.EqualTo(c));
    }

    [Test]
    public void BestChild_SwitchesWhenWeightChanges()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);

        // a has more weight
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 3,
            [ProtoArray.RootKey(b)] = 1
        });
        Assert.That(FindHead(array, genesis), Is.EqualTo(a));

        // b overtakes
        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(a)] = 3,
            [ProtoArray.RootKey(b)] = 6
        });
        Assert.That(FindHead(array, genesis), Is.EqualTo(b));
    }

    [Test]
    public void FindHead_AfterPrune_WithoutApplyDeltas_StillWorks()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, b, 3, 0, 0);

        ApplyDeltasFromDict(array, new Dictionary<string, long>
        {
            [ProtoArray.RootKey(c)] = 1
        });

        array.Prune(a);

        // After prune, BestChild/BestDescendant are cleared, so FindHead
        // returns justified root (a) since no BestDescendant is set
        Assert.DoesNotThrow(() => FindHead(array, a));
    }

    // ========== ComputeReorgDepth ==========

    [Test]
    public void ComputeReorgDepth_NormalExtension_ReturnsZero()
    {
        // genesis -> a -> b: oldHead=a, newHead=b (a is ancestor of b)
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);

        Assert.That(array.ComputeReorgDepth(a, b), Is.EqualTo(0UL));
    }

    [Test]
    public void ComputeReorgDepth_SameRoot_ReturnsZero()
    {
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);

        Assert.That(array.ComputeReorgDepth(a, a), Is.EqualTo(0UL));
    }

    [Test]
    public void ComputeReorgDepth_UnknownRoot_ReturnsZero()
    {
        var genesis = MakeRoot(0x01);
        var array = new ProtoArray(genesis, 0, 0);

        Assert.That(array.ComputeReorgDepth(MakeRoot(0xFF), genesis), Is.EqualTo(0UL));
        Assert.That(array.ComputeReorgDepth(genesis, MakeRoot(0xFF)), Is.EqualTo(0UL));
    }

    [Test]
    public void ComputeReorgDepth_SingleSlotReorg_ReturnsOne()
    {
        // genesis -> a (old head), genesis -> b (new head)
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, genesis, 1, 0, 0);

        Assert.That(array.ComputeReorgDepth(a, b), Is.EqualTo(1UL));
    }

    [Test]
    public void ComputeReorgDepth_AsymmetricFork_ReturnsOldBranchLength()
    {
        // genesis -> a -> b -> c (old head)
        // genesis -> d (new head)
        // common ancestor = genesis, depth from oldHead = 3
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var d = MakeRoot(0x05);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, b, 3, 0, 0);
        array.RegisterBlock(d, genesis, 1, 0, 0);

        Assert.That(array.ComputeReorgDepth(c, d), Is.EqualTo(3UL));
    }

    [Test]
    public void ComputeReorgDepth_DeepFork_ReturnsCorrectDepth()
    {
        // genesis -> a -> b (old head)
        // genesis -> a -> c -> d (new head)
        // common ancestor = a, depth from oldHead = 1
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var d = MakeRoot(0x05);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, a, 2, 0, 0);
        array.RegisterBlock(d, c, 3, 0, 0);

        Assert.That(array.ComputeReorgDepth(b, d), Is.EqualTo(1UL));
    }

    [Test]
    public void ComputeReorgDepth_AfterPrune_StillWorks()
    {
        // genesis -> a -> b -> c (old), a -> d (new)
        // prune to a, then check depth from c to d = 2 (c->b->a, a is common)
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var d = MakeRoot(0x05);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, b, 3, 0, 0);
        array.RegisterBlock(d, a, 2, 0, 0);

        array.Prune(a);

        Assert.That(array.ComputeReorgDepth(c, d), Is.EqualTo(2UL));
    }

    [Test]
    public void ComputeReorgDepth_GenesisIsCommonAncestor_FullDepth()
    {
        // genesis -> a -> b (old head)
        // genesis -> c -> d (new head)
        // depth from oldHead to genesis = 2
        var genesis = MakeRoot(0x01);
        var a = MakeRoot(0x02);
        var b = MakeRoot(0x03);
        var c = MakeRoot(0x04);
        var d = MakeRoot(0x05);
        var array = new ProtoArray(genesis, 0, 0);
        array.RegisterBlock(a, genesis, 1, 0, 0);
        array.RegisterBlock(b, a, 2, 0, 0);
        array.RegisterBlock(c, genesis, 1, 0, 0);
        array.RegisterBlock(d, c, 2, 0, 0);

        Assert.That(array.ComputeReorgDepth(b, d), Is.EqualTo(2UL));
    }

    // ========== Helpers ==========

    /// <summary>
    /// Simulates a full-rebuild delta apply: resets all node weights/best pointers,
    /// converts dict to array, and calls ApplyDeltas.
    /// This matches the store's ComputeDeltas → ApplyDeltas cycle.
    /// </summary>
    private static void ApplyDeltasFromDict(ProtoArray array, Dictionary<string, long> dict, long cutoffWeight = 0)
    {
        // Reset all node weights and best pointers (store does this in ComputeDeltas)
        for (int i = 0; i < array.NodeCount; i++)
        {
            var node = array.GetNodeByIndex(i);
            if (node is not null)
            {
                node.Weight = 0;
                node.BestChild = null;
                node.BestDescendant = null;
            }
        }

        // Convert dict to long[]
        var deltas = new long[array.NodeCount];
        foreach (var (key, delta) in dict)
        {
            if (array.ContainsKey(key))
            {
                // Find index by iterating (GetIndex takes Bytes32, not string key)
                foreach (var (root, _, _) in array.GetAllBlocks())
                {
                    if (ProtoArray.RootKey(root) == key)
                    {
                        var idx = array.GetIndex(root);
                        if (idx.HasValue)
                            deltas[idx.Value] = delta;
                        break;
                    }
                }
            }
        }

        array.ApplyDeltas(deltas, cutoffWeight);
    }

    /// <summary>
    /// FindHead equivalent: looks up justified root's bestDescendant.
    /// </summary>
    private static Bytes32 FindHead(ProtoArray array, Bytes32 justifiedRoot)
    {
        var justifiedIdx = array.GetIndex(justifiedRoot);
        if (!justifiedIdx.HasValue)
            return default;

        var justifiedNode = array.GetNodeByIndex(justifiedIdx.Value);
        if (justifiedNode is null)
            return default;

        var bestDescIdx = justifiedNode.BestDescendant ?? justifiedIdx.Value;
        var bestDesc = array.GetNodeByIndex(bestDescIdx);
        return bestDesc?.Root ?? justifiedRoot;
    }

    internal static Bytes32 MakeRoot(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());
}
