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

    [Test]
    public async Task OnPeerStatusAsync_DoesNotBackfillFromPeerHeadRoot()
    {
        var peerMgr = new SyncPeerManager();
        var processor = new FakeSyncBlockProcessor();
        var cache = new NewBlockCache(capacity: 100);
        var attestations = new FakeAttestationStore();
        var network = new RecordingNetworkRequester();
        var svc = new SyncService(processor, peerMgr, cache, attestations, network);
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        svc.OnPeerConnected("peer-1");
        await svc.OnPeerStatusAsync("peer-1", headSlot: 100, finalizedSlot: 50, headRoot: MakeRoot(0x42));
        await Task.Delay(100);
        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        Assert.That(network.Requests.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task TrySyncFromBestPeer_DoesNotBackfillFromPeerHeadRoot()
    {
        var peerMgr = new SyncPeerManager();
        var processor = new FakeSyncBlockProcessor();
        var cache = new NewBlockCache(capacity: 100);
        var attestations = new FakeAttestationStore();
        var network = new RecordingNetworkRequester();
        var svc = new SyncService(processor, peerMgr, cache, attestations, network);
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        svc.OnPeerConnected("peer-1");
        await svc.OnPeerStatusAsync("peer-1", headSlot: 100, finalizedSlot: 50, headRoot: MakeRoot(0x43));
        svc.TrySyncFromBestPeer();
        await Task.Delay(100);
        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        Assert.That(network.Requests.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task PendingAttestations_ReplayedOnBlockArrival()
    {
        var attestationSink = new RejectThenAcceptAttestationStore();
        var peerMgr = new SyncPeerManager();
        var processor = new FakeSyncBlockProcessor();
        var cache = new NewBlockCache(capacity: 100);
        var network = new FakeNetworkRequester();
        var svc = new SyncService(processor, peerMgr, cache, attestationSink, network);

        var unknownRoot = MakeRoot(0xAA);
        var att = MakeAttestation(0, 1, unknownRoot);

        // First attempt: sink rejects (unknown root), attestation is buffered
        await svc.OnGossipAttestationAsync(att);
        Assert.That(attestationSink.AcceptedCount, Is.EqualTo(0));

        // Now allow the sink to accept
        attestationSink.ShouldAccept = true;

        // Simulate a block arriving — triggers drain of pending attestations
        processor.KnownRoots.Add(MakeRoot(0x00));
        var block = MakeSignedBlock(MakeRoot(0x00), 1);
        await svc.OnGossipBlockAsync(block, ComputeRoot(block), "peer-1");

        // The pending attestation should now have been replayed and accepted
        Assert.That(attestationSink.AcceptedCount, Is.EqualTo(1));
    }

    [Test]
    public void ResolvePreferredPeerId_PrefersHint_ThenBestPeer()
    {
        Assert.That(
            InvokeResolvePreferredPeerId("peer-hint", "peer-best"),
            Is.EqualTo("peer-hint"));
        Assert.That(
            InvokeResolvePreferredPeerId(null, "peer-best"),
            Is.EqualTo("peer-best"));
        Assert.That(
            InvokeResolvePreferredPeerId("", null),
            Is.Null);
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

    private static Bytes32 ComputeRoot(SignedBlock signedBlock) =>
        new(signedBlock.Block.HashTreeRoot());

    private static SignedBlock MakeSignedBlock(Bytes32 parentRoot, ulong slot)
    {
        var body = new BlockBody(Array.Empty<AggregatedAttestation>());
        var block = new Block(new Slot(slot), 0, parentRoot, Bytes32.Zero(), body);
        var sig = new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), XmssSignature.Empty());
        return new SignedBlock(block, sig);
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

    private static string? InvokeResolvePreferredPeerId(string? hintedPeerId, string? bestPeerId)
    {
        var method = typeof(SyncService).GetMethod(
            "ResolvePreferredPeerId",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.That(method, Is.Not.Null);
        return (string?)method!.Invoke(null, new object?[] { hintedPeerId, bestPeerId });
    }

    private sealed class FakeSyncBlockProcessor : IBlockProcessor
    {
        public HashSet<Bytes32> KnownRoots { get; } = new();
        public int ProcessedCount { get; private set; }
        public ulong CurrentHeadSlot { get; set; }
        public ulong HeadSlot => CurrentHeadSlot;

        public bool IsBlockKnown(Bytes32 root) => KnownRoots.Contains(root);
        public bool HasState(Bytes32 root) => KnownRoots.Contains(root);

        public ForkChoiceApplyResult ProcessBlock(SignedBlock signedBlock)
        {
            ProcessedCount++;
            var root = new Bytes32(signedBlock.Block.HashTreeRoot());
            KnownRoots.Add(root);
            CurrentHeadSlot = Math.Max(CurrentHeadSlot, signedBlock.Block.Slot.Value);
            return ForkChoiceApplyResult.AcceptedResult(false, CurrentHeadSlot, root);
        }
    }

    private sealed class FakeAttestationStore : IAttestationSink
    {
        public List<SignedAttestation> Attestations { get; } = new();
        public int Count => Attestations.Count;
        public void AddAttestation(SignedAttestation attestation) => Attestations.Add(attestation);
        public bool TryAddAttestation(SignedAttestation attestation)
        {
            Attestations.Add(attestation);
            return true;
        }
    }

    private sealed class RejectThenAcceptAttestationStore : IAttestationSink
    {
        public bool ShouldAccept { get; set; }
        public int AcceptedCount { get; private set; }

        public void AddAttestation(SignedAttestation attestation) => AcceptedCount++;
        public bool TryAddAttestation(SignedAttestation attestation)
        {
            if (!ShouldAccept)
                return false;
            AcceptedCount++;
            return true;
        }
    }

    private sealed class FakeNetworkRequester : INetworkRequester
    {
        public Task<List<SignedBlock>> RequestBlocksByRootAsync(
            string peerId, List<Bytes32> roots, CancellationToken ct) =>
            Task.FromResult(new List<SignedBlock>());
    }

    private sealed class RecordingNetworkRequester : INetworkRequester
    {
        public List<(string PeerId, List<Bytes32> Roots)> Requests { get; } = new();

        public Task<List<SignedBlock>> RequestBlocksByRootAsync(
            string peerId, List<Bytes32> roots, CancellationToken ct)
        {
            Requests.Add((peerId, new List<Bytes32>(roots)));
            return Task.FromResult(new List<SignedBlock>());
        }
    }
}
