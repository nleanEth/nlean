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

        var parentRoot = MakeRoot(0x01);
        var parentBlock = MakeSignedBlock(MakeRoot(0x00), slot: 1);
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
    public async Task RequestParents_UpdatesPeerScoreOnSuccess()
    {
        var (backfill, network, processor, peerMgr) = CreateBackfillSync();
        peerMgr.AddPeer("peer-1");

        // Make grandparent known so no recursive fetch happens
        var grandparentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(grandparentRoot);

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

    // --- Helpers ---

    private static (BackfillSync backfill, FakeNetworkRequester network,
        FakeBackfillProcessor processor, SyncPeerManager peerMgr) CreateBackfillSync(int maxDepth = 512)
    {
        var network = new FakeNetworkRequester();
        var processor = new FakeBackfillProcessor();
        var peerMgr = new SyncPeerManager();
        var backfill = new BackfillSync(network, processor, peerMgr, maxDepth);
        return (backfill, network, processor, peerMgr);
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

    private sealed class FakeNetworkRequester : INetworkRequester
    {
        public Dictionary<Bytes32, SignedBlockWithAttestation> BlocksByRoot { get; } = new();
        public int RequestCount { get; private set; }

        public Task<List<SignedBlockWithAttestation>> RequestBlocksByRootAsync(
            string peerId, List<Bytes32> roots, CancellationToken ct)
        {
            RequestCount++;
            var result = new List<SignedBlockWithAttestation>();
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
        public List<SignedBlockWithAttestation> ProcessedBlocks { get; } = new();
        public ulong HeadSlot { get; private set; }

        public bool IsBlockKnown(Bytes32 root) => KnownRoots.Contains(root);

        public ForkChoiceApplyResult ProcessBlock(SignedBlockWithAttestation signedBlock)
        {
            ProcessedBlocks.Add(signedBlock);
            var root = new Bytes32(signedBlock.Message.Block.HashTreeRoot());
            KnownRoots.Add(root);
            HeadSlot = Math.Max(HeadSlot, signedBlock.Message.Block.Slot.Value);
            return ForkChoiceApplyResult.AcceptedResult(false, HeadSlot, root);
        }
    }
}
