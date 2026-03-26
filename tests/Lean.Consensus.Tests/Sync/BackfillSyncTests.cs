using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Sync;

[TestFixture]
public sealed class BackfillSyncTests
{
    [Test]
    public async Task RequestParents_FetchesAndProcesses()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        // grandparent is known (e.g. genesis)
        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);
        processor.StateReadyRoots.Add(grandparentRoot);

        var parentBlock = MakeSignedBlock(grandparentRoot, slot: 1);
        var parentRoot = ComputeRoot(parentBlock);
        network.BlocksByRoot[parentRoot] = parentBlock;

        await backfill.RequestParentsAsync(new List<Bytes32> { parentRoot }, CancellationToken.None);

        Assert.That(processor.ProcessedBlocks, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RequestParents_NoPeers_DoesNothing()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();

        var parentRoot = MakeRoot(0x01);
        await backfill.RequestParentsAsync(new List<Bytes32> { parentRoot }, CancellationToken.None);

        Assert.That(processor.ProcessedBlocks, Is.Empty);
    }

    [Test]
    public async Task RequestParents_RecursesMissingGrandparent()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        // grandparent is known
        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);
        processor.StateReadyRoots.Add(grandparentRoot);

        // parent block exists on network, its parent is grandparent (known)
        var parentBlock = MakeSignedBlock(grandparentRoot, slot: 1);
        var parentRoot = ComputeRoot(parentBlock);
        network.BlocksByRoot[parentRoot] = parentBlock;

        // child's missing parent is parentRoot
        await backfill.RequestParentsAsync(new List<Bytes32> { parentRoot }, CancellationToken.None);

        Assert.That(processor.ProcessedBlocks, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RequestParents_RespectsMaxDepth()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync(maxDepth: 3);
        peerMgr.AddPeer("peer-1");

        // Create a chain of 5 blocks, none with known parent
        var roots = new List<Bytes32>();
        var prevRoot = MakeRoot(0xFF); // unknown root
        for (int i = 0; i < 5; i++)
        {
            var block = MakeSignedBlock(prevRoot, slot: (ulong)(i + 1));
            var root = ComputeRoot(block);
            network.BlocksByRoot[root] = block;
            roots.Add(root);
            prevRoot = root;
        }

        await backfill.RequestParentsAsync(new List<Bytes32> { roots[^1] }, CancellationToken.None);

        // Should stop after maxDepth=3 fetches
        Assert.That(network.RequestCount, Is.LessThanOrEqualTo(3));
    }

    [Test]
    public async Task RequestParents_StopsAtFinalizedFloor()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        // Finalized at slot 100 — backfill should not walk past this.
        processor.FinalizedSlot = 100;

        // Create a chain: slot 102 -> slot 101 -> slot 100 -> slot 99 (below finalized)
        var root99 = MakeRoot(0x99);
        var block100 = MakeSignedBlock(root99, slot: 100);
        var root100 = ComputeRoot(block100);
        network.BlocksByRoot[root100] = block100;

        var block101 = MakeSignedBlock(root100, slot: 101);
        var root101 = ComputeRoot(block101);
        network.BlocksByRoot[root101] = block101;

        var block102 = MakeSignedBlock(root101, slot: 102);
        var root102 = ComputeRoot(block102);
        network.BlocksByRoot[root102] = block102;

        // Also provide block99 on the network (should NOT be fetched)
        var root98 = MakeRoot(0x98);
        var block99 = MakeSignedBlock(root98, slot: 99);
        network.BlocksByRoot[root99] = block99;

        await backfill.RequestParentsAsync(new List<Bytes32> { root102 }, CancellationToken.None);

        // block100 is at the finalized floor — its parent (root99) should NOT be enqueued.
        // Without the fix this would walk to root99, root98, etc. for up to maxDepth.
        // With the fix, we stop at block100 and never fetch block99.
        Assert.That(network.RequestCount, Is.LessThanOrEqualTo(3),
            "Backfill should stop at finalized floor, not walk past it");
    }

    [Test]
    public async Task RequestParents_UpdatesPeerScoreOnSuccess()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        // Make grandparent known so no recursive fetch happens
        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);
        processor.StateReadyRoots.Add(grandparentRoot);

        var parentBlock = MakeSignedBlock(grandparentRoot, slot: 1);
        var parentRoot = ComputeRoot(parentBlock);
        network.BlocksByRoot[parentRoot] = parentBlock;

        var scoreBefore = peerMgr.GetPeerScore("peer-1");
        await backfill.RequestParentsAsync(new List<Bytes32> { parentRoot }, CancellationToken.None);

        Assert.That(peerMgr.GetPeerScore("peer-1"), Is.GreaterThan(scoreBefore));
    }

    [Test]
    public async Task RequestParents_UpdatesPeerScoreOnFailure()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        // Root not in network -> empty response = failure
        var missingRoot = MakeRoot(0xAA);

        var scoreBefore = peerMgr.GetPeerScore("peer-1");
        await backfill.RequestParentsAsync(new List<Bytes32> { missingRoot }, CancellationToken.None);

        Assert.That(peerMgr.GetPeerScore("peer-1"), Is.LessThan(scoreBefore));
    }

    [Test]
    public async Task RequestParents_PrefersSuggestedPeerFirst()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");
        peerMgr.AddPeer("peer-2");

        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);
        processor.StateReadyRoots.Add(grandparentRoot);

        var parentBlock = MakeSignedBlock(grandparentRoot, slot: 1);
        var parentRoot = ComputeRoot(parentBlock);
        network.BlocksByRoot[parentRoot] = parentBlock;

        await backfill.RequestParentsAsync(
            new List<Bytes32> { parentRoot },
            CancellationToken.None,
            preferredPeerId: "peer-2");

        Assert.That(network.RequestedPeers, Is.Not.Empty);
        Assert.That(network.RequestedPeers[0], Is.EqualTo("peer-2"));
    }

    [Test]
    public async Task RequestParents_SkipsAlreadyKnownRoots()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        var knownRoot = MakeRoot(0x01);
        processor.KnownRoots.Add(knownRoot);

        await backfill.RequestParentsAsync(new List<Bytes32> { knownRoot }, CancellationToken.None);

        Assert.That(network.RequestCount, Is.EqualTo(0));
    }

    [Test]
    public async Task RequestBackfill_ProcessedByConsumer()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);
        processor.StateReadyRoots.Add(grandparentRoot);

        var parentBlock = MakeSignedBlock(grandparentRoot, slot: 1);
        var parentRoot = ComputeRoot(parentBlock);
        network.BlocksByRoot[parentRoot] = parentBlock;

        using var cts = new CancellationTokenSource();
        backfill.SetShutdownToken(cts.Token);

        backfill.RequestBackfill(parentRoot);

        // Wait for consumer to process the queued item.
        for (var i = 0; i < 50 && processor.ProcessedBlocks.Count == 0; i++)
            await Task.Delay(50);

        Assert.That(processor.ProcessedBlocks, Has.Count.EqualTo(1));

        await cts.CancelAsync();
        await backfill.StopAsync();
    }

    [Test]
    public async Task RequestBackfill_DuplicateRoot_IsIgnored()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);
        processor.StateReadyRoots.Add(grandparentRoot);

        var parentBlock = MakeSignedBlock(grandparentRoot, slot: 1);
        var parentRoot = ComputeRoot(parentBlock);
        network.BlocksByRoot[parentRoot] = parentBlock;

        using var cts = new CancellationTokenSource();
        backfill.SetShutdownToken(cts.Token);

        // Queue same root twice — second should be deduped
        backfill.RequestBackfill(parentRoot);
        backfill.RequestBackfill(parentRoot);

        for (var i = 0; i < 50 && processor.ProcessedBlocks.Count == 0; i++)
            await Task.Delay(50);

        // Only one block should be processed (dedup in RequestBackfill)
        Assert.That(processor.ProcessedBlocks, Has.Count.EqualTo(1));

        await cts.CancelAsync();
        await backfill.StopAsync();
    }

    [Test]
    public async Task StopAsync_CompletesGracefully()
    {
        var (backfill, _, _, _) = CreateBackfillSync();

        using var cts = new CancellationTokenSource();
        backfill.SetShutdownToken(cts.Token);

        await cts.CancelAsync();
        await backfill.StopAsync();

        // Should not throw or hang
    }

    [Test]
    public async Task RequestParents_ParentKnownButStateUnavailable_DoesNotProcessChild()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync(maxDepth: 1);
        peerMgr.AddPeer("peer-1");

        var parentRoot = MakeRoot(0x11);
        processor.KnownRoots.Add(parentRoot);

        var childBlock = MakeSignedBlock(parentRoot, slot: 2);
        var childRoot = ComputeRoot(childBlock);
        network.BlocksByRoot[childRoot] = childBlock;

        await backfill.RequestParentsAsync(new List<Bytes32> { childRoot }, CancellationToken.None);

        Assert.That(processor.ProcessedBlocks, Is.Empty);
        Assert.That(processor.KnownRoots, Does.Not.Contain(childRoot));
    }

    [Test]
    public async Task RequestBackfill_WhenSynced_DefersEnqueue()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync(
            shouldDeferBackfill: () => true);
        peerMgr.AddPeer("peer-1");

        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);
        processor.StateReadyRoots.Add(grandparentRoot);

        var parentBlock = MakeSignedBlock(grandparentRoot, slot: 1);
        var parentRoot = ComputeRoot(parentBlock);
        network.BlocksByRoot[parentRoot] = parentBlock;

        using var cts = new CancellationTokenSource();
        backfill.SetShutdownToken(cts.Token);

        backfill.RequestBackfill(parentRoot);

        // Should NOT be processed within 200ms (grace period is 500ms).
        await Task.Delay(200);
        Assert.That(processor.ProcessedBlocks, Is.Empty,
            "Block should not be processed during grace period");

        // Wait for grace period to expire + processing time.
        for (var i = 0; i < 50 && processor.ProcessedBlocks.Count == 0; i++)
            await Task.Delay(50);

        Assert.That(processor.ProcessedBlocks, Has.Count.EqualTo(1),
            "Block should be processed after grace period expires");

        await cts.CancelAsync();
        await backfill.StopAsync();
    }

    [Test]
    public async Task RequestBackfill_WhenSyncing_EnqueuesImmediately()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync(
            shouldDeferBackfill: () => false);
        peerMgr.AddPeer("peer-1");

        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);
        processor.StateReadyRoots.Add(grandparentRoot);

        var parentBlock = MakeSignedBlock(grandparentRoot, slot: 1);
        var parentRoot = ComputeRoot(parentBlock);
        network.BlocksByRoot[parentRoot] = parentBlock;

        using var cts = new CancellationTokenSource();
        backfill.SetShutdownToken(cts.Token);

        backfill.RequestBackfill(parentRoot);

        // Should be processed quickly (no grace period).
        for (var i = 0; i < 20 && processor.ProcessedBlocks.Count == 0; i++)
            await Task.Delay(50);

        Assert.That(processor.ProcessedBlocks, Has.Count.EqualTo(1),
            "Block should be processed immediately when not deferring");

        await cts.CancelAsync();
        await backfill.StopAsync();
    }

    [Test]
    public async Task RequestBackfill_Deferred_ParentArrivesViaGossip_SkipsRpc()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync(
            shouldDeferBackfill: () => true);
        peerMgr.AddPeer("peer-1");

        var parentRoot = MakeRoot(0xBB);

        using var cts = new CancellationTokenSource();
        backfill.SetShutdownToken(cts.Token);

        backfill.RequestBackfill(parentRoot);

        // Simulate parent arriving via gossip within the grace period.
        await Task.Delay(100);
        processor.KnownRoots.Add(parentRoot);

        // Wait for grace period to expire + extra margin.
        await Task.Delay(600);

        Assert.That(network.RequestCount, Is.EqualTo(0),
            "No RPC should be made when parent arrived via gossip during grace period");

        await cts.CancelAsync();
        await backfill.StopAsync();
    }

    [Test]
    public async Task RequestBackfill_Deferred_ParentDoesNotArrive_ProceedsWithRpc()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync(
            shouldDeferBackfill: () => true);
        peerMgr.AddPeer("peer-1");

        var parentRoot = MakeRoot(0xCC);

        using var cts = new CancellationTokenSource();
        backfill.SetShutdownToken(cts.Token);

        backfill.RequestBackfill(parentRoot);

        // Wait for grace period + consumer processing time.
        for (var i = 0; i < 40 && network.RequestCount == 0; i++)
            await Task.Delay(50);

        Assert.That(network.RequestCount, Is.GreaterThan(0),
            "RPC should be made when parent did not arrive during grace period");

        await cts.CancelAsync();
        await backfill.StopAsync();
    }

    [Test]
    public async Task RequestBackfill_Deferred_Shutdown_CleansUp()
    {
        var (backfill, network, _, _) = CreateBackfillSync(
            shouldDeferBackfill: () => true);

        var parentRoot = MakeRoot(0xDD);

        using var cts = new CancellationTokenSource();
        backfill.SetShutdownToken(cts.Token);

        backfill.RequestBackfill(parentRoot);

        // Cancel before grace period expires.
        await Task.Delay(100);
        await cts.CancelAsync();
        await backfill.StopAsync();

        Assert.That(network.RequestCount, Is.EqualTo(0),
            "No RPC should be made when shutdown cancels before grace period");
    }

    // --- Helpers ---

    private static (BackfillSync backfill, FakeNetworkRequester network,
        FakeBackfillProcessor processor, SyncPeerManager peerMgr) CreateBackfillSync(
        int maxDepth = 512, Func<bool>? shouldDeferBackfill = null)
    {
        var network = new FakeNetworkRequester();
        var processor = new FakeBackfillProcessor();
        var peerMgr = new SyncPeerManager();
        var backfill = new BackfillSync(network, processor, peerMgr, maxDepth,
            shouldDeferBackfill: shouldDeferBackfill);
        return (backfill, network, processor, peerMgr);
    }

    private static Bytes32 MakeRoot(byte fill) =>
        new(Enumerable.Repeat(fill, 32).ToArray());

    private static Bytes32 ComputeRoot(SignedBlock signedBlock) =>
        new(signedBlock.Block.HashTreeRoot());

    private static SignedBlock MakeSignedBlock(Bytes32 parentRoot, ulong slot)
    {
        var body = new BlockBody(Array.Empty<AggregatedAttestation>());
        var block = new Block(new Slot(slot), 0, parentRoot, Bytes32.Zero(), body);
        var sig = new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), XmssSignature.Empty());
        return new SignedBlock(block, sig);
    }

    private sealed class FakeNetworkRequester : INetworkRequester
    {
        public Dictionary<Bytes32, SignedBlock> BlocksByRoot { get; } = new();
        public List<string> RequestedPeers { get; } = new();
        public int RequestCount { get; private set; }

        public Task<List<SignedBlock>> RequestBlocksByRootAsync(
            string peerId, List<Bytes32> roots, CancellationToken ct)
        {
            RequestCount++;
            RequestedPeers.Add(peerId);
            var result = new List<SignedBlock>();
            foreach (var root in roots)
            {
                if (BlocksByRoot.TryGetValue(root, out var block))
                    result.Add(block);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class FakeBackfillProcessor : IBlockProcessor
    {
        public HashSet<Bytes32> KnownRoots { get; } = new();
        public HashSet<Bytes32> StateReadyRoots { get; } = new();
        public List<SignedBlock> ProcessedBlocks { get; } = new();
        public ulong HeadSlot { get; private set; }
        public ulong FinalizedSlot { get; set; }

        public bool IsBlockKnown(Bytes32 root) => KnownRoots.Contains(root);
        public bool HasState(Bytes32 root) => StateReadyRoots.Contains(root);

        public ForkChoiceApplyResult ProcessBlock(SignedBlock signedBlock)
        {
            ProcessedBlocks.Add(signedBlock);
            var root = new Bytes32(signedBlock.Block.HashTreeRoot());
            KnownRoots.Add(root);
            StateReadyRoots.Add(root);
            HeadSlot = Math.Max(HeadSlot, signedBlock.Block.Slot.Value);
            return ForkChoiceApplyResult.AcceptedResult(false, HeadSlot, root);
        }
    }
}
