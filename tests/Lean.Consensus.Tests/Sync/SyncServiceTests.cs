using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Sync;

[TestFixture]
public sealed class SyncServiceTests
{
    [Test]
    public void InitialState_IsIdle()
    {
        var (svc, _, _, _, _) = CreateSyncService();
        Assert.That(svc.State, Is.EqualTo(SyncState.Idle));
    }

    [Test]
    public async Task OnPeerConnected_WithHigherHead_TransitionsToSyncing()
    {
        var (svc, _, processor, _, _) = CreateSyncService();

        svc.OnPeerConnected("peer-1");
        await svc.OnPeerStatusAsync("peer-1", headSlot: 100, finalizedSlot: 50);

        Assert.That(svc.State, Is.EqualTo(SyncState.Syncing));
    }

    [Test]
    public async Task OnPeerConnected_AtSameHead_TransitionsToSynced()
    {
        var (svc, _, processor, _, _) = CreateSyncService();
        // Both at head 5 — within tolerance, should be Synced.
        processor.CurrentHeadSlot = 5;
        svc.OnPeerConnected("peer-1");
        await svc.OnPeerStatusAsync("peer-1", headSlot: 5, finalizedSlot: 0);

        Assert.That(svc.State, Is.EqualTo(SyncState.Synced));
    }

    [Test]
    public void AllPeersDisconnected_TransitionsToIdle()
    {
        var (svc, _, _, _, _) = CreateSyncService();

        svc.OnPeerConnected("peer-1");
        svc.OnPeerDisconnected("peer-1");

        Assert.That(svc.State, Is.EqualTo(SyncState.Idle));
    }

    [Test]
    public async Task OnGossipBlock_DelegatesProcessing()
    {
        var (svc, _, processor, _, _) = CreateSyncService();
        svc.OnPeerConnected("peer-1");
        await svc.OnPeerStatusAsync("peer-1", headSlot: 0, finalizedSlot: 0);

        var parentRoot = MakeRoot(0x00);
        processor.KnownRoots.Add(parentRoot);

        var block = MakeSignedBlock(parentRoot, slot: 1);
        var root = ComputeRoot(block);

        await svc.OnGossipBlockAsync(block, root, "peer-1");

        Assert.That(processor.ProcessedCount, Is.GreaterThan(0));
    }

    [Test]
    public async Task OnGossipAttestation_StoresAttestation()
    {
        var (svc, _, _, attestations, _) = CreateSyncService();
        svc.OnPeerConnected("peer-1");
        await svc.OnPeerStatusAsync("peer-1", headSlot: 0, finalizedSlot: 0);

        var attestation = MakeAttestation(0, 1, MakeRoot(0x01));
        await svc.OnGossipAttestationAsync(attestation);

        Assert.That(attestations.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task SyncingToSynced_WhenHeadCatchesUp()
    {
        var (svc, _, processor, _, _) = CreateSyncService();

        svc.OnPeerConnected("peer-1");
        await svc.OnPeerStatusAsync("peer-1", headSlot: 20, finalizedSlot: 10);
        Assert.That(svc.State, Is.EqualTo(SyncState.Syncing));

        // Simulate catching up: processor reports head at slot 20
        processor.CurrentHeadSlot = 20;
        svc.RecomputeState();

        Assert.That(svc.State, Is.EqualTo(SyncState.Synced));
    }

    [Test]
    public async Task SyncedToSyncing_WhenNewPeerHasHigherHead()
    {
        var (svc, _, processor, _, _) = CreateSyncService();

        processor.CurrentHeadSlot = 5;
        svc.OnPeerConnected("peer-1");
        await svc.OnPeerStatusAsync("peer-1", headSlot: 5, finalizedSlot: 0);
        Assert.That(svc.State, Is.EqualTo(SyncState.Synced));

        // New peer arrives with much higher head slot
        svc.OnPeerConnected("peer-2");
        await svc.OnPeerStatusAsync("peer-2", headSlot: 100, finalizedSlot: 50);

        Assert.That(svc.State, Is.EqualTo(SyncState.Syncing));
    }

    [Test]
    public async Task StartStop_DoesNotThrow()
    {
        var (svc, _, _, _, _) = CreateSyncService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await svc.StartAsync(cts.Token);
        await svc.StopAsync(CancellationToken.None);
    }

    // --- Helpers ---

    private static (SyncService svc, SyncPeerManager peerMgr, FakeSyncBlockProcessor processor,
        FakeAttestationStore attestations, NewBlockCache cache) CreateSyncService()
    {
        var peerMgr = new SyncPeerManager();
        var processor = new FakeSyncBlockProcessor();
        var cache = new NewBlockCache(capacity: 100);
        var attestations = new FakeAttestationStore();
        var network = new FakeNetworkRequester();
        var svc = new SyncService(processor, peerMgr, cache, attestations, network);
        return (svc, peerMgr, processor, attestations, cache);
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

    private static SignedAttestation MakeAttestation(ulong validatorId, ulong slot, Bytes32 headRoot)
    {
        var data = new AttestationData(
            new Slot(slot),
            new Checkpoint(headRoot, new Slot(slot)),
            Checkpoint.Default(),
            Checkpoint.Default());
        return new SignedAttestation(validatorId, data, XmssSignature.Empty());
    }

    private sealed class FakeSyncBlockProcessor : IBlockProcessor
    {
        public HashSet<Bytes32> KnownRoots { get; } = new();
        public int ProcessedCount { get; private set; }
        public ulong CurrentHeadSlot { get; set; }
        public ulong HeadSlot => CurrentHeadSlot;

        public bool IsBlockKnown(Bytes32 root) => KnownRoots.Contains(root);

        public ForkChoiceApplyResult ProcessBlock(SignedBlockWithAttestation signedBlock)
        {
            ProcessedCount++;
            var root = new Bytes32(signedBlock.Message.Block.HashTreeRoot());
            KnownRoots.Add(root);
            CurrentHeadSlot = Math.Max(CurrentHeadSlot, signedBlock.Message.Block.Slot.Value);
            return ForkChoiceApplyResult.AcceptedResult(false, CurrentHeadSlot, root);
        }
    }

    private sealed class FakeAttestationStore : IAttestationSink
    {
        public List<SignedAttestation> Attestations { get; } = new();
        public int Count => Attestations.Count;
        public void AddAttestation(SignedAttestation attestation) => Attestations.Add(attestation);
    }

    private sealed class FakeNetworkRequester : INetworkRequester
    {
        public Task<List<SignedBlockWithAttestation>> RequestBlocksByRootAsync(
            string peerId, List<Bytes32> roots, CancellationToken ct) =>
            Task.FromResult(new List<SignedBlockWithAttestation>());
    }
}
