using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Sync;

[TestFixture]
public sealed class HeadSyncTests
{
    [Test]
    public void OnGossipBlock_KnownBlock_IsSkipped()
    {
        var (headSync, processor, cache, backfill) = CreateHeadSync();
        var root = MakeRoot(0x01);
        processor.KnownRoots.Add(root);

        var block = MakeSignedBlock(MakeRoot(0x00), slot: 1);
        headSync.OnGossipBlock(block, root, peerId: "peer-1");

        Assert.That(cache.Count, Is.EqualTo(0));
        Assert.That(processor.ProcessedCount, Is.EqualTo(0));
    }

    [Test]
    public void OnGossipBlock_ParentKnown_ProcessesBlock()
    {
        var (headSync, processor, cache, backfill) = CreateHeadSync();
        var parentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(parentRoot);

        var block = MakeSignedBlock(parentRoot, slot: 1);
        var root = ComputeRoot(block);
        headSync.OnGossipBlock(block, root, peerId: "peer-1");

        Assert.That(processor.ProcessedCount, Is.EqualTo(1));
        Assert.That(processor.KnownRoots, Does.Contain(root));
    }

    [Test]
    public void OnGossipBlock_ParentUnknown_CachesAndMarksOrphan()
    {
        var (headSync, processor, cache, backfill) = CreateHeadSync();
        var parentRoot = MakeRoot(0x00);

        var block = MakeSignedBlock(parentRoot, slot: 1);
        var root = ComputeRoot(block);
        headSync.OnGossipBlock(block, root, peerId: "peer-1");

        Assert.That(cache.Count, Is.EqualTo(1));
        Assert.That(cache.OrphanCount, Is.EqualTo(1));
        Assert.That(backfill.BackfillRequests, Has.Count.EqualTo(1));
        Assert.That(backfill.BackfillRequests[0].Root, Is.EqualTo(parentRoot));
        Assert.That(backfill.BackfillRequests[0].PreferredPeerId, Is.EqualTo("peer-1"));
    }

    [Test]
    public void OnGossipBlock_CascadesDescendants()
    {
        var (headSync, processor, cache, backfill) = CreateHeadSync();
        var genesisRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(genesisRoot);

        // Create block A (parent=genesis)
        var blockA = MakeSignedBlock(genesisRoot, slot: 1);
        var rootA = ComputeRoot(blockA);

        // Create block B (parent=A)
        var blockB = MakeSignedBlock(rootA, slot: 2);
        var rootB = ComputeRoot(blockB);

        // B arrives first — parent A unknown, so cached
        headSync.OnGossipBlock(blockB, rootB, peerId: "peer-1");
        Assert.That(cache.Count, Is.EqualTo(1));
        Assert.That(processor.ProcessedCount, Is.EqualTo(0));

        // A arrives — parent genesis is known → process A, cascade to B
        headSync.OnGossipBlock(blockA, rootA, peerId: "peer-1");

        Assert.That(processor.ProcessedCount, Is.EqualTo(2));
        Assert.That(cache.Count, Is.EqualTo(0));
        Assert.That(processor.KnownRoots, Does.Contain(rootA));
        Assert.That(processor.KnownRoots, Does.Contain(rootB));
    }

    [Test]
    public void OnGossipBlock_CascadesMultipleLevels()
    {
        var (headSync, processor, cache, backfill) = CreateHeadSync();
        var genesisRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(genesisRoot);

        var blockA = MakeSignedBlock(genesisRoot, slot: 1);
        var rootA = ComputeRoot(blockA);

        var blockB = MakeSignedBlock(rootA, slot: 2);
        var rootB = ComputeRoot(blockB);

        var blockC = MakeSignedBlock(rootB, slot: 3);
        var rootC = ComputeRoot(blockC);

        // Cache C and B out of order
        headSync.OnGossipBlock(blockC, rootC, "peer-1");
        headSync.OnGossipBlock(blockB, rootB, "peer-1");
        Assert.That(cache.Count, Is.EqualTo(2));

        // A arrives -> cascades to B -> cascades to C
        headSync.OnGossipBlock(blockA, rootA, "peer-1");

        Assert.That(processor.ProcessedCount, Is.EqualTo(3));
        Assert.That(cache.Count, Is.EqualTo(0));
    }

    [Test]
    public void OnGossipBlock_ProcessFailure_DoesNotCascade()
    {
        var (headSync, processor, cache, backfill) = CreateHeadSync();
        var genesisRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(genesisRoot);

        var blockA = MakeSignedBlock(genesisRoot, slot: 1);
        var rootA = ComputeRoot(blockA);

        var blockB = MakeSignedBlock(rootA, slot: 2);
        var rootB = ComputeRoot(blockB);

        // Cache B (parent=A unknown)
        headSync.OnGossipBlock(blockB, rootB, "peer-1");

        // Make processor reject A
        processor.RejectRoots.Add(rootA);
        headSync.OnGossipBlock(blockA, rootA, "peer-1");

        // A attempted but rejected; B should NOT cascade
        Assert.That(processor.ProcessedCount, Is.EqualTo(1));
        Assert.That(processor.KnownRoots, Does.Not.Contain(rootA));
    }

    [Test]
    public void OnGossipBlock_UnmarksOrphan_WhenParentArrives()
    {
        var (headSync, processor, cache, backfill) = CreateHeadSync();
        var grandParent = MakeRoot(0x00);
        processor.KnownRoots.Add(grandParent);

        var blockParent = MakeSignedBlock(grandParent, slot: 1);
        var parentRoot = ComputeRoot(blockParent);

        var blockChild = MakeSignedBlock(parentRoot, slot: 2);
        var childRoot = ComputeRoot(blockChild);

        // Child arrives, parent unknown -> orphan
        headSync.OnGossipBlock(blockChild, childRoot, "peer-1");
        Assert.That(cache.OrphanCount, Is.EqualTo(1));

        // Parent arrives with known grandparent -> cascade
        headSync.OnGossipBlock(blockParent, parentRoot, "peer-1");
        Assert.That(cache.OrphanCount, Is.EqualTo(0));
    }

    // --- Helpers ---

    private static (HeadSync headSync, FakeBlockProcessor processor,
        NewBlockCache cache, FakeBackfillTrigger backfill) CreateHeadSync()
    {
        var processor = new FakeBlockProcessor();
        var cache = new NewBlockCache(capacity: 100);
        var backfill = new FakeBackfillTrigger();
        var headSync = new HeadSync(processor, cache, backfill);
        return (headSync, processor, cache, backfill);
    }

    private static Bytes32 MakeRoot(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());

    private static Bytes32 ComputeRoot(SignedBlockWithAttestation signedBlock) =>
        new(signedBlock.Message.Block.HashTreeRoot());

    private static SignedBlockWithAttestation MakeSignedBlock(Bytes32 parentRoot, ulong slot)
    {
        var body = new BlockBody(Array.Empty<AggregatedAttestation>());
        var block = new Block(new Slot(slot), 0, parentRoot, Bytes32.Zero(), body);
        var attestation = new Attestation(0, new AttestationData(
            block.Slot, Checkpoint.Default(), Checkpoint.Default(), Checkpoint.Default()));
        var blockWithAttestation = new BlockWithAttestation(block, attestation);
        var sig = new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), XmssSignature.Empty());
        return new SignedBlockWithAttestation(blockWithAttestation, sig);
    }

    private sealed class FakeBlockProcessor : IBlockProcessor
    {
        public HashSet<Bytes32> KnownRoots { get; } = new();
        public HashSet<Bytes32> RejectRoots { get; } = new();
        public int ProcessedCount { get; private set; }
        public ulong HeadSlot { get; private set; }

        public bool IsBlockKnown(Bytes32 root) => KnownRoots.Contains(root);

        public ForkChoiceApplyResult ProcessBlock(SignedBlockWithAttestation signedBlock)
        {
            ProcessedCount++;
            var root = new Bytes32(signedBlock.Message.Block.HashTreeRoot());
            if (RejectRoots.Contains(root))
                return ForkChoiceApplyResult.Rejected(
                    ForkChoiceRejectReason.StateTransitionFailed, "rejected", 0, Bytes32.Zero());

            KnownRoots.Add(root);
            return ForkChoiceApplyResult.AcceptedResult(false, 0, Bytes32.Zero());
        }
    }

    private sealed class FakeBackfillTrigger : IBackfillTrigger
    {
        public List<(Bytes32 Root, string? PreferredPeerId)> BackfillRequests { get; } = new();
        public void RequestBackfill(Bytes32 parentRoot, string? preferredPeerId = null) =>
            BackfillRequests.Add((parentRoot, preferredPeerId));
    }
}
