using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using Lean.Storage;
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
    public void GetFinalizedSignedBlockSsz_AtGenesis_SeedsBlockStore()
    {
        // Regression: hive's rpc_compat "finalized block pairs with finalized state"
        // test queries /lean/v0/blocks/finalized before nlean can finalize a real
        // block. The genesis SignedBlock must be in _blockStore at startup.
        var blockStore = new BlockByRootStore(new InMemoryKeyValueStore());
        var (svc, _, store, _) = CreateService(blockStore: blockStore);

        var ssz = svc.GetFinalizedSignedBlockSsz();

        Assert.That(ssz, Is.Not.Null, "Genesis SignedBlock should be seeded into blockStore");
        var decoded = new SignedBlockGossipDecoder().DecodeAndValidate(ssz!);
        Assert.That(decoded.IsSuccess, Is.True, $"Decoded SignedBlock should be valid SSZ: {decoded.Reason}");
        Assert.That(decoded.BlockMessageRoot, Is.EqualTo(store.FinalizedRoot),
            "Decoded SignedBlock.Block must hash to the finalized root");
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
    public void GetFinalizedStateSsz_ReturnsFinalizedRootState_NotHeadState()
    {
        var chainStateCache = new ChainStateCache();
        var (svc, _, _, _) = CreateService(chainStateCache: chainStateCache);
        var finalizedRoot = new Bytes32(Enumerable.Repeat((byte)0x22, 32).ToArray());
        var headRoot = new Bytes32(Enumerable.Repeat((byte)0x33, 32).ToArray());

        var finalizedState = new State(
            new Config(GenesisTime.ToUnixTimeSeconds() is var gt ? (ulong)gt : 0),
            new Slot(169),
            new BlockHeader(new Slot(169), 0, Bytes32.Zero(), new Bytes32(Enumerable.Repeat((byte)0x44, 32).ToArray()), Bytes32.Zero()),
            new Checkpoint(finalizedRoot, new Slot(169)),
            new Checkpoint(finalizedRoot, new Slot(169)),
            Array.Empty<Bytes32>(),
            Array.Empty<bool>(),
            Array.Empty<Validator>(),
            Array.Empty<Bytes32>(),
            Array.Empty<bool>());
        var headState = finalizedState with
        {
            Slot = new Slot(206),
            LatestBlockHeader = new BlockHeader(new Slot(206), 0, finalizedRoot, new Bytes32(Enumerable.Repeat((byte)0x55, 32).ToArray()), Bytes32.Zero()),
            LatestJustified = new Checkpoint(headRoot, new Slot(182)),
            LatestFinalized = new Checkpoint(finalizedRoot, new Slot(169))
        };

        chainStateCache.Set(ChainStateCache.RootKey(finalizedRoot), finalizedState);
        chainStateCache.Set(ChainStateCache.RootKey(headRoot), headState);
        SetSnapshot(svc, headRoot, 206, headRoot, 182, finalizedRoot, 169);

        var bytes = svc.GetFinalizedStateSsz();

        Assert.That(bytes, Is.Not.Null);
        var decoded = SszDecoding.DecodeState(bytes!);
        Assert.That(decoded.Slot.Value, Is.EqualTo(169UL));
        Assert.That(decoded.LatestBlockHeader.Slot.Value, Is.EqualTo(169UL));
        Assert.That(decoded.LatestFinalized.Root, Is.EqualTo(finalizedRoot));
    }

    [Test]
    public void HasUnknownBlockRootsInFlight_ReturnsTrue_WhenWallClockFarBehindWithPeersAhead()
    {
        // When the node's wall-clock slot is far ahead of its head AND peers
        // report a head much further than our own, duties should be suppressed.
        var syncService = new FakeSyncService(SyncState.Synced, networkHeadSlot: 30);
        var (svc, time, store, clock) = CreateService(syncService);

        // Advance the wall clock so currentSlot ≈ 20 (well beyond headSlot=0 + tolerance=8).
        time.UtcNow = GenesisTime.AddSeconds(20 * 4);
        svc.OnTick(20, 0);

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
        // Apply through the service so block1's post-state lands in the chain
        // state cache; GetKnownAggregatedPayloadsForBlock resolves the chain
        // view from the parent state.
        Assert.That(svc.TryApplyLocalBlock(WrapBlock(validBlock1), out var applyReason), Is.True, applyReason);
        var block1Root = new Bytes32(validBlock1.HashTreeRoot());
        var block1Checkpoint = new Checkpoint(block1Root, new Slot(1));

        var attData = new AttestationData(
            new Slot(1),
            new Checkpoint(block1Root, new Slot(1)),
            block1Checkpoint,
            genesisCheckpoint);

        // Tick into slot 1 so the slot-1 attestation passes the future-slot bound.
        store.TickInterval(1, 0);
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

        var (attestations, proofs) = svc.GetKnownAggregatedPayloadsForBlock(
            slot: 2, parentRoot: block1Root, currentJustifiedSlots: null, currentFinalizedSlot: null);

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
        Assert.That(svc.TryApplyLocalBlock(WrapBlock(validBlock1), out var applyReason), Is.True, applyReason);
        var block1Root = new Bytes32(validBlock1.HashTreeRoot());

        var payloadBackedData = new AttestationData(
            new Slot(1),
            new Checkpoint(block1Root, new Slot(1)),
            new Checkpoint(block1Root, new Slot(1)),
            genesisCheckpoint);

        // Tick into slot 1 so the slot-1 attestation passes the future-slot bound.
        store.TickInterval(1, 0);
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

        var (attestations, proofs) = svc.GetKnownAggregatedPayloadsForBlock(
            slot: 3, parentRoot: block1Root, currentJustifiedSlots: null, currentFinalizedSlot: null);

        Assert.That(attestations, Has.Count.EqualTo(1));
        Assert.That(proofs, Has.Count.EqualTo(1));
        Assert.That(attestations[0].Data, Is.EqualTo(payloadBackedData));
        Assert.That(proofs[0].ProofData, Is.EqualTo(proof.ProofData));
    }

    [Test]
    public void GetKnownAggregatedPayloadsForBlock_AcceptsOlderButJustifiedSource()
    {
        // leanSpec PR #716: build_block must include a gap-closing attestation
        // whose source checkpoint is older than the parent chain's latest
        // justified checkpoint, as long as the source SLOT is justified on
        // that chain. The pre-#716 filter required exact source-checkpoint
        // equality and dropped such attestations, stalling justification.
        var (svc, _, store, _) = CreateService();
        var genesisRoot = store.HeadRoot;
        var genesisCheckpoint = new Checkpoint(genesisRoot, new Slot(0));

        // block1 on genesis (empty body).
        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        Assert.That(svc.TryComputeBlockStateRoot(block1, out var stateRoot1, out _), Is.True);
        var validBlock1 = new Block(block1.Slot, block1.ProposerIndex, block1.ParentRoot, stateRoot1, block1.Body);
        Assert.That(svc.TryApplyLocalBlock(WrapBlock(validBlock1), out var r1), Is.True, r1);
        var block1Root = new Bytes32(validBlock1.HashTreeRoot());

        // block2 on block1, whose body justifies block1 (1 validator => 100%).
        var justifyBlock1 = new AggregatedAttestation(
            new AggregationBits(new[] { true }),
            new AttestationData(
                new Slot(1),
                new Checkpoint(block1Root, new Slot(1)),
                new Checkpoint(block1Root, new Slot(1)),
                genesisCheckpoint));
        var block2 = new Block(new Slot(2), 0, block1Root, Bytes32.Zero(),
            new BlockBody(new[] { justifyBlock1 }));
        Assert.That(svc.TryComputeBlockStateRoot(block2, out var stateRoot2, out var post2Justified, out _), Is.True);
        Assert.That(post2Justified.Slot.Value, Is.EqualTo(1UL),
            "block2's body must justify block1 so the parent chain's justified is slot 1, not genesis");
        var validBlock2 = new Block(block2.Slot, block2.ProposerIndex, block2.ParentRoot, stateRoot2, block2.Body);
        var signedBlock2 = new SignedBlock(
            validBlock2,
            new BlockSignatures(
                new[] { new AggregatedSignatureProof(new AggregationBits(new[] { true }), new byte[] { 0xAB }) },
                XmssSignature.Empty()));
        Assert.That(svc.TryApplyLocalBlock(signedBlock2, out var r2), Is.True, r2);
        var block2Root = new Bytes32(validBlock2.HashTreeRoot());

        // Gap-closing attestation in the pool: source = genesis (slot 0),
        // target = block2 (slot 2). Its source is OLDER than block2's chain
        // justified checkpoint (block1, slot 1) -- but slot 0 is justified.
        var gapData = new AttestationData(
            new Slot(2),
            new Checkpoint(block2Root, new Slot(2)),
            new Checkpoint(block2Root, new Slot(2)),
            genesisCheckpoint);
        store.TickInterval(2, 0);
        Assert.That(store.TryOnAttestation(new SignedAttestation(0, gapData, XmssSignature.Empty()), out var ra), Is.True, ra);
        store.TickInterval(2, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);
        var gapProof = new AggregatedSignatureProof(
            AggregationBits.FromValidatorIndices(new ulong[] { 0 }),
            new byte[] { 0xEE });
        Assert.That(
            store.TryOnGossipAggregatedAttestation(new SignedAggregatedAttestation(gapData, gapProof), out var rg),
            Is.True,
            rg);
        store.TickInterval(2, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);

        var (attestations, proofs) = svc.GetKnownAggregatedPayloadsForBlock(
            slot: 3, parentRoot: block2Root, currentJustifiedSlots: null, currentFinalizedSlot: null);

        Assert.That(attestations, Has.Count.EqualTo(1),
            "gap-closing attestation with older-but-justified source must be selected");
        Assert.That(attestations[0].Data, Is.EqualTo(gapData));
        Assert.That(proofs, Has.Count.EqualTo(1));
    }

    private static (ConsensusServiceV2 svc, FakeTimeSource time,
        ProtoArrayForkChoiceStore store, SlotClock clock) CreateServiceWithSync(SyncState state)
    {
        var (svc, time, store, clock) = CreateService(new FakeSyncService(state));
        return (svc, time, store, clock);
    }

    private static (ConsensusServiceV2 svc, FakeTimeSource time,
        ProtoArrayForkChoiceStore store, SlotClock clock) CreateService(
        ISyncService? syncService = null,
        IConsensusStateStore? stateStore = null,
        ChainStateCache? chainStateCache = null,
        IBlockByRootStore? blockStore = null)
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
        var svc = new ConsensusServiceV2(store, clock, config, syncService,
            chainStateCache: chainStateCache, stateStore: stateStore, blockStore: blockStore);
        return (svc, time, store, clock);
    }

    private static void SetSnapshot(
        ConsensusServiceV2 svc,
        Bytes32 headRoot,
        ulong headSlot,
        Bytes32 justifiedRoot,
        ulong justifiedSlot,
        Bytes32 finalizedRoot,
        ulong finalizedSlot)
    {
        var snapshotType = typeof(ConsensusServiceV2).GetNestedType(
            "ConsensusSnapshot",
            System.Reflection.BindingFlags.NonPublic)!;
        var snapshot = Activator.CreateInstance(
            snapshotType,
            headRoot,
            headSlot,
            justifiedRoot,
            justifiedSlot,
            finalizedRoot,
            finalizedSlot)!;
        typeof(ConsensusServiceV2)
            .GetField("_snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(svc, snapshot);
    }

    private static Block CreateBlock(ulong slot, Bytes32 parentRoot, ulong proposerIndex)
    {
        var body = new BlockBody(Array.Empty<AggregatedAttestation>());
        return new Block(new Slot(slot), proposerIndex, parentRoot, Bytes32.Zero(), body);
    }

    private static SignedBlock WrapBlock(Block block)
    {
        var emptyXmssSig = XmssSignature.Empty();
        var signature = new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), emptyXmssSig);
        return new SignedBlock(block, signature);
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
        public FakeSyncService(SyncState state, ulong networkHeadSlot = 0UL)
        {
            State = state;
            NetworkHeadSlot = networkHeadSlot;
        }

        public SyncState State { get; }
        public bool HasEverHadPeer { get; set; }
        public ulong NetworkHeadSlot { get; set; }
        public ulong GetNetworkHeadSlot() => NetworkHeadSlot;
        public Task OnGossipBlockAsync(SignedBlock block, Bytes32 blockRoot, string? peerId) => Task.CompletedTask;
        public Task OnGossipAttestationAsync(SignedAttestation attestation) => Task.CompletedTask;
        public Task OnPeerStatusAsync(string peerId, ulong headSlot, ulong finalizedSlot, Bytes32? headRoot = null) => Task.CompletedTask;
        public void CascadeAcceptedBlock(Bytes32 blockRoot) { }
        public void TrySyncFromBestPeer() { }
        public void OnPeerConnected(string peerId) { }
        public void OnPeerDisconnected(string peerId) { }
        public void RequestBlockByRoot(Bytes32 blockRoot) { }
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
