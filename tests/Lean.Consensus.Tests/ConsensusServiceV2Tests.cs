using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

[TestFixture]
public sealed class ConsensusServiceV2Tests
{
    private static readonly DateTimeOffset GenesisTime = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void CurrentSlot_DelegatesToSlotClock()
    {
        var (svc, time, _, _) = CreateService();
        time.UtcNow = GenesisTime.AddSeconds(8); // slot 2
        Assert.That(svc.CurrentSlot, Is.EqualTo(2UL));
    }

    [Test]
    public void HeadSlot_DelegatesToStore()
    {
        var (svc, _, _, _) = CreateService();
        Assert.That(svc.HeadSlot, Is.EqualTo(0UL));
    }

    [Test]
    public void HeadRoot_IsNotEmpty()
    {
        var (svc, _, _, _) = CreateService();
        Assert.That(svc.HeadRoot, Is.Not.Null);
        Assert.That(svc.HeadRoot.Length, Is.EqualTo(32));
    }

    [Test]
    public void JustifiedAndFinalizedSlot_AreZeroAtGenesis()
    {
        var (svc, _, _, _) = CreateService();
        Assert.That(svc.JustifiedSlot, Is.EqualTo(0UL));
        Assert.That(svc.FinalizedSlot, Is.EqualTo(0UL));
    }

    [Test]
    public void TryApplyLocalBlock_AcceptsValidBlock()
    {
        var (svc, _, store, _) = CreateService();
        var parentRoot = store.HeadRoot;
        var block = CreateBlock(slot: 1, parentRoot: parentRoot, proposerIndex: 0);

        // Compute the correct state root so the block passes validation.
        Assert.That(svc.TryComputeBlockStateRoot(block, out var stateRoot, out _), Is.True);
        var validBlock = new Block(block.Slot, block.ProposerIndex, block.ParentRoot, stateRoot, block.Body);
        var signed = WrapBlock(validBlock);

        var result = svc.TryApplyLocalBlock(signed, out var reason);

        Assert.That(result, Is.True);
        Assert.That(reason, Is.Empty);
    }

    [Test]
    public void TryApplyLocalBlock_RejectsUnknownParent()
    {
        var (svc, _, _, _) = CreateService();
        var unknownParent = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());
        var block = CreateBlock(slot: 1, parentRoot: unknownParent, proposerIndex: 0);
        var signed = WrapBlock(block);

        var result = svc.TryApplyLocalBlock(signed, out var reason);

        Assert.That(result, Is.False);
        Assert.That(reason, Is.Not.Empty);
    }

    [Test]
    public void TryApplyLocalAttestation_DoesNotThrow()
    {
        var (svc, _, store, _) = CreateService();
        var attestation = CreateAttestation(0, 0, store.HeadRoot);

        var result = svc.TryApplyLocalAttestation(attestation, out var reason);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task StartStop_DoesNotThrow()
    {
        var (svc, _, _, _) = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await svc.StartAsync(cts.Token);
        await Task.Delay(50);
        await svc.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StopAsync_SavesFinalState()
    {
        var stateStore = new FakeConsensusStateStore();
        var (svc, _, _, _) = CreateService(stateStore: stateStore);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await svc.StartAsync(cts.Token);
        await Task.Delay(100);
        await svc.StopAsync(CancellationToken.None);

        Assert.That(stateStore.SaveCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(stateStore.LastSavedState, Is.Not.Null);
        Assert.That(stateStore.LastSavedState!.HeadSlot, Is.EqualTo(0UL));
    }

    [Test]
    public void HasUnknownBlockRootsInFlight_ReturnsTrue_DuringCheckpointInitUntilReady()
    {
        var headRoot = new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var persisted = new ConsensusHeadState(
            headSlot: 335,
            headRoot: headRoot.AsSpan(),
            latestJustifiedSlot: 182,
            latestJustifiedRoot: headRoot.AsSpan(),
            latestFinalizedSlot: 169,
            latestFinalizedRoot: headRoot.AsSpan(),
            safeTargetSlot: 169,
            safeTargetRoot: headRoot.AsSpan());
        var stateStore = new FakeConsensusStateStore(persisted);
        var config = new ConsensusConfig
        {
            InitialValidatorCount = 1,
            SecondsPerSlot = 4,
            GenesisTimeUnix = (ulong)GenesisTime.ToUnixTimeSeconds()
        };
        var store = new ProtoArrayForkChoiceStore(config, stateStore);
        var time = new FakeTimeSource(GenesisTime);
        var clock = new SlotClock(config.GenesisTimeUnix, config.SecondsPerSlot,
            ProtoArrayForkChoiceStore.IntervalsPerSlot, time);
        var svc = new ConsensusServiceV2(store, clock, config, stateStore: stateStore);

        Assert.That(svc.HasUnknownBlockRootsInFlight, Is.True);
    }

    [Test]
    public void HasUnknownBlockRootsInFlight_RemainsFalse_ForGenesisAndSyncStateOnly()
    {
        var (svc1, _, _, _) = CreateService();
        Assert.That(svc1.HasUnknownBlockRootsInFlight, Is.False);

        var (svc2, _, _, _) = CreateServiceWithSync(SyncState.Syncing);
        Assert.That(svc2.HasUnknownBlockRootsInFlight, Is.False);

        var (svc3, _, _, _) = CreateServiceWithSync(SyncState.Synced);
        Assert.That(svc3.HasUnknownBlockRootsInFlight, Is.False);
    }

    [Test]
    public void GetKnownAggregatedPayloadsForBlock_UsesExactValidatorProofGate()
    {
        var (svc, _, store, _) = CreateService();
        var genesisRoot = store.HeadRoot;
        var genesisCheckpoint = new Checkpoint(genesisRoot, new Slot(0));

        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        Assert.That(svc.TryComputeBlockStateRoot(block1, out var stateRoot, out _), Is.True);
        var validBlock1 = new Block(block1.Slot, block1.ProposerIndex, block1.ParentRoot, stateRoot, block1.Body);
        Assert.That(store.OnBlock(WrapBlock(validBlock1), genesisCheckpoint, genesisCheckpoint, validatorCount: 2).Accepted, Is.True);
        var block1Root = new Bytes32(validBlock1.HashTreeRoot());
        var block1Checkpoint = new Checkpoint(block1Root, new Slot(1));

        var attData = new AttestationData(
            new Slot(1),
            new Checkpoint(block1Root, new Slot(1)),
            block1Checkpoint,
            genesisCheckpoint);

        Assert.That(store.TryOnAttestation(new SignedAttestation(0, attData, XmssSignature.Empty()), out var reason0), Is.True, reason0);
        Assert.That(store.TryOnAttestation(new SignedAttestation(1, attData, XmssSignature.Empty()), out var reason1), Is.True, reason1);
        store.TickInterval(1, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);

        var proofForOnlyValidator1 = new AggregatedSignatureProof(
            AggregationBits.FromValidatorIndices(new ulong[] { 1 }),
            new byte[] { 0xAB });
        Assert.That(
            store.TryOnGossipAggregatedAttestation(new SignedAggregatedAttestation(attData, proofForOnlyValidator1), out var aggReason),
            Is.True,
            aggReason);
        store.TickInterval(1, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);

        var (attestations, proofs) = svc.GetKnownAggregatedPayloadsForBlock(slot: 2, requiredSource: genesisCheckpoint);

        Assert.That(attestations, Has.Count.EqualTo(1));
        Assert.That(proofs, Has.Count.EqualTo(1));
        Assert.That(proofs[0].ProofData, Is.EqualTo(proofForOnlyValidator1.ProofData));
        Assert.That(attestations[0].AggregationBits.TryToValidatorIndices(out var validatorIds), Is.True);
        Assert.That(validatorIds, Is.EquivalentTo(new[] { 1UL }));
    }

    [Test]
    public void GetKnownAggregatedPayloadsForBlock_UsesPayloadBackedDataEvenIfTrackerMovedOn()
    {
        var (svc, _, store, _) = CreateService();
        var genesisRoot = store.HeadRoot;
        var genesisCheckpoint = new Checkpoint(genesisRoot, new Slot(0));

        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        Assert.That(svc.TryComputeBlockStateRoot(block1, out var stateRoot1, out _), Is.True);
        var validBlock1 = new Block(block1.Slot, block1.ProposerIndex, block1.ParentRoot, stateRoot1, block1.Body);
        Assert.That(store.OnBlock(WrapBlock(validBlock1), genesisCheckpoint, genesisCheckpoint, validatorCount: 1).Accepted, Is.True);
        var block1Root = new Bytes32(validBlock1.HashTreeRoot());

        var payloadBackedData = new AttestationData(
            new Slot(1),
            new Checkpoint(block1Root, new Slot(1)),
            new Checkpoint(block1Root, new Slot(1)),
            genesisCheckpoint);

        Assert.That(store.TryOnAttestation(new SignedAttestation(0, payloadBackedData, XmssSignature.Empty()), out var reason0), Is.True, reason0);
        store.TickInterval(1, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);

        var proof = new AggregatedSignatureProof(
            AggregationBits.FromValidatorIndices(new ulong[] { 0 }),
            new byte[] { 0xCD });
        Assert.That(
            store.TryOnGossipAggregatedAttestation(new SignedAggregatedAttestation(payloadBackedData, proof), out var aggReason),
            Is.True,
            aggReason);
        store.TickInterval(1, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);

        var newerUnprovenData = new AttestationData(
            new Slot(2),
            new Checkpoint(block1Root, new Slot(1)),
            new Checkpoint(block1Root, new Slot(1)),
            genesisCheckpoint);
        Assert.That(store.TryOnAttestation(new SignedAttestation(0, newerUnprovenData, XmssSignature.Empty()), out var reason1), Is.True, reason1);
        store.TickInterval(2, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);

        var (attestations, proofs) = svc.GetKnownAggregatedPayloadsForBlock(slot: 3, requiredSource: genesisCheckpoint);

        Assert.That(attestations, Has.Count.EqualTo(1));
        Assert.That(proofs, Has.Count.EqualTo(1));
        Assert.That(attestations[0].Data, Is.EqualTo(payloadBackedData));
        Assert.That(proofs[0].ProofData, Is.EqualTo(proof.ProofData));
    }

    private static (ConsensusServiceV2 svc, FakeTimeSource time,
        ProtoArrayForkChoiceStore store, SlotClock clock) CreateServiceWithSync(SyncState state)
    {
        var (svc, time, store, clock) = CreateService(new FakeSyncService(state));
        return (svc, time, store, clock);
    }

    private static (ConsensusServiceV2 svc, FakeTimeSource time,
        ProtoArrayForkChoiceStore store, SlotClock clock) CreateService(
        ISyncService? syncService = null, IConsensusStateStore? stateStore = null)
    {
        var config = new ConsensusConfig
        {
            InitialValidatorCount = 1,
            SecondsPerSlot = 4,
            GenesisTimeUnix = (ulong)GenesisTime.ToUnixTimeSeconds()
        };
        var store = new ProtoArrayForkChoiceStore(config);
        var time = new FakeTimeSource(GenesisTime);
        var clock = new SlotClock(config.GenesisTimeUnix, config.SecondsPerSlot,
            ProtoArrayForkChoiceStore.IntervalsPerSlot, time);
        var svc = new ConsensusServiceV2(store, clock, config, syncService, stateStore: stateStore);
        return (svc, time, store, clock);
    }

    private static Block CreateBlock(ulong slot, Bytes32 parentRoot, ulong proposerIndex)
    {
        var body = new BlockBody(Array.Empty<AggregatedAttestation>());
        return new Block(new Slot(slot), proposerIndex, parentRoot, Bytes32.Zero(), body);
    }

    private static SignedBlockWithAttestation WrapBlock(Block block)
    {
        var attestation = new Attestation(0, new AttestationData(
            block.Slot, Checkpoint.Default(), Checkpoint.Default(), Checkpoint.Default()));
        var blockWithAttestation = new BlockWithAttestation(block, attestation);
        var emptyXmssSig = XmssSignature.Empty();
        var signature = new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), emptyXmssSig);
        return new SignedBlockWithAttestation(blockWithAttestation, signature);
    }

    private static SignedAttestation CreateAttestation(ulong validatorId, ulong slot, Bytes32 headRoot)
    {
        var data = new AttestationData(
            new Slot(slot),
            new Checkpoint(headRoot, new Slot(slot)),
            new Checkpoint(headRoot, new Slot(slot)),
            new Checkpoint(headRoot, new Slot(slot)));
        var sig = XmssSignature.Empty();
        return new SignedAttestation(validatorId, data, sig);
    }

    private sealed class FakeTimeSource : ITimeSource
    {
        public FakeTimeSource(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class FakeSyncService : ISyncService
    {
        public FakeSyncService(SyncState state) => State = state;
        public SyncState State { get; }
        public Task OnGossipBlockAsync(SignedBlockWithAttestation block, Bytes32 blockRoot, string? peerId) => Task.CompletedTask;
        public Task OnGossipAttestationAsync(SignedAttestation attestation) => Task.CompletedTask;
        public Task OnPeerStatusAsync(string peerId, ulong headSlot, ulong finalizedSlot, Bytes32? headRoot = null) => Task.CompletedTask;
        public void CascadeAcceptedBlock(Bytes32 blockRoot) { }
        public void TrySyncFromBestPeer() { }
        public void OnPeerConnected(string peerId) { }
        public void OnPeerDisconnected(string peerId) { }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeConsensusStateStore : IConsensusStateStore
    {
        private readonly ConsensusHeadState? _loadedState;

        public FakeConsensusStateStore(ConsensusHeadState? loadedState = null)
        {
            _loadedState = loadedState;
        }

        public int SaveCount { get; private set; }
        public ConsensusHeadState? LastSavedState { get; private set; }

        public bool TryLoad([NotNullWhen(true)] out ConsensusHeadState? state)
        {
            state = _loadedState;
            return state is not null;
        }

        public bool TryLoad([NotNullWhen(true)] out ConsensusHeadState? state, out State? headChainState)
        {
            state = _loadedState;
            headChainState = null;
            return state is not null;
        }

        public void Save(ConsensusHeadState state)
        {
            SaveCount++;
            LastSavedState = state;
        }

        public void Save(ConsensusHeadState state, State headChainState)
        {
            SaveCount++;
            LastSavedState = state;
        }

        public void Delete() { }
    }
}
