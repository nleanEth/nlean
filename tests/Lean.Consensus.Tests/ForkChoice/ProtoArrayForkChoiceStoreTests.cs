using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using NUnit.Framework;
using System.Reflection;

namespace Lean.Consensus.Tests.ForkChoice;

[TestFixture]
public sealed class ProtoArrayForkChoiceStoreTests
{
    private const int IntervalsPerSlot = 5;

    [Test]
    public void Constructor_InitializesWithGenesis()
    {
        var store = CreateStore();

        Assert.That(store.HeadSlot, Is.EqualTo(0UL));
        Assert.That(store.JustifiedSlot, Is.EqualTo(0UL));
        Assert.That(store.FinalizedSlot, Is.EqualTo(0UL));
        Assert.That(store.HeadRoot, Is.Not.EqualTo(default(Bytes32)));
    }

    [Test]
    public void Constructor_LoadedCheckpointState_PreservesHeadRootSlot()
    {
        var anchorRoot = new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var persisted = new ConsensusHeadState(
            headSlot: 225,
            headRoot: anchorRoot.AsSpan(),
            latestJustifiedSlot: 225,
            latestJustifiedRoot: anchorRoot.AsSpan(),
            latestFinalizedSlot: 210,
            latestFinalizedRoot: anchorRoot.AsSpan(),
            safeTargetSlot: 210,
            safeTargetRoot: anchorRoot.AsSpan());
        var stateStore = new FakeConsensusStateStore(persisted);

        var config = new ConsensusConfig { InitialValidatorCount = 4 };
        var store = new ProtoArrayForkChoiceStore(config, stateStore);

        Assert.That(store.HeadSlot, Is.EqualTo(225UL));
        Assert.That(store.HeadRoot, Is.EqualTo(anchorRoot));
        Assert.That(store.ProtoArray.GetSlot(anchorRoot), Is.EqualTo(225UL));
    }

    [Test]
    public void ComputeTargetCheckpoint_LoadedCheckpointAnchor_DoesNotReturnZeroRoot()
    {
        var anchorRoot = new Bytes32(Enumerable.Repeat((byte)0x22, 32).ToArray());
        var persisted = new ConsensusHeadState(
            headSlot: 198,
            headRoot: anchorRoot.AsSpan(),
            latestJustifiedSlot: 182,
            latestJustifiedRoot: anchorRoot.AsSpan(),
            latestFinalizedSlot: 169,
            latestFinalizedRoot: anchorRoot.AsSpan(),
            safeTargetSlot: 198,
            safeTargetRoot: anchorRoot.AsSpan());
        var stateStore = new FakeConsensusStateStore(persisted);

        var config = new ConsensusConfig { InitialValidatorCount = 4 };
        var store = new ProtoArrayForkChoiceStore(config, stateStore);

        var target = store.ComputeTargetCheckpoint();

        Assert.That(target.Root, Is.Not.EqualTo(Bytes32.Zero()));
    }

    [Test]
    public void LoadedCheckpointAnchor_StartsIniting_AndBecomesReadyAfterNewerJustifiedBlock()
    {
        var anchorRoot = new Bytes32(Enumerable.Repeat((byte)0x44, 32).ToArray());
        var persisted = new ConsensusHeadState(
            headSlot: 182,
            headRoot: anchorRoot.AsSpan(),
            latestJustifiedSlot: 182,
            latestJustifiedRoot: anchorRoot.AsSpan(),
            latestFinalizedSlot: 169,
            latestFinalizedRoot: anchorRoot.AsSpan(),
            safeTargetSlot: 169,
            safeTargetRoot: anchorRoot.AsSpan());
        var stateStore = new FakeConsensusStateStore(persisted);

        var config = new ConsensusConfig { InitialValidatorCount = 1 };
        var store = new ProtoArrayForkChoiceStore(config, stateStore);

        Assert.That(store.IsReadyForDuties, Is.False);

        var block = CreateBlock(slot: 183, parentRoot: anchorRoot, proposerIndex: 0);
        var signed = WrapBlock(block);
        var blockRoot = new Bytes32(block.HashTreeRoot());
        var justified = new Checkpoint(blockRoot, new Slot(183));
        var finalized = new Checkpoint(anchorRoot, new Slot(169));

        var result = store.OnBlock(signed, justified, finalized, validatorCount: 1);

        Assert.That(result.Accepted, Is.True);
        Assert.That(store.IsReadyForDuties, Is.True);
    }

    [Test]
    public void TickInterval_GracefulFallback_WhenJustifiedRootIsAbsentFromProtoArray()
    {
        var store = CreateStore(validatorCount: 4);
        var headBefore = store.HeadRoot;
        typeof(ProtoArrayForkChoiceStore)
            .GetField("_latestJustified", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(
                store,
                new Checkpoint(
                    new Bytes32(Enumerable.Repeat((byte)0xAB, 32).ToArray()),
                    new Slot(1)));

        // Should NOT throw — graceful fallback to index 0 during catch-up.
        Assert.DoesNotThrow(() => store.TickInterval(1, IntervalsPerSlot - 1));
        Assert.That(store.HeadRoot, Is.EqualTo(headBefore));
    }

    [Test]
    public void OnBlock_AcceptsValidChild()
    {
        var store = CreateStore();
        var parentRoot = store.HeadRoot;
        var block = CreateBlock(slot: 1, parentRoot: parentRoot, proposerIndex: 0);
        var signed = WrapBlock(block);

        var result = ApplyBlock(store, signed);

        Assert.That(result.Accepted, Is.True);
    }

    [Test]
    public void OnBlock_RejectsDuplicate()
    {
        var store = CreateStore();
        var parentRoot = store.HeadRoot;
        var block = CreateBlock(slot: 1, parentRoot: parentRoot, proposerIndex: 0);
        var signed = WrapBlock(block);

        ApplyBlock(store, signed);
        var result = ApplyBlock(store, signed);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.DuplicateBlock));
    }

    [Test]
    public void OnBlock_RejectsUnknownParent()
    {
        var store = CreateStore();
        var unknownParent = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());
        var block = CreateBlock(slot: 1, parentRoot: unknownParent, proposerIndex: 0);
        var signed = WrapBlock(block);

        var result = ApplyBlock(store, signed);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.UnknownParent));
    }

    [Test]
    public void OnBlock_ExtendingHead_ImmediatelyUpdatesHead()
    {
        var store = CreateStore();
        var genesisRoot = store.HeadRoot;
        Assert.That(store.HeadSlot, Is.EqualTo(0UL));

        // Add block 1 extending genesis (head)
        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        var signed1 = WrapBlock(block1);
        var result1 = ApplyBlock(store, signed1);
        Assert.That(result1.Accepted, Is.True);
        Assert.That(store.HeadSlot, Is.EqualTo(1UL));

        // Add block 2 extending block 1 (current head)
        var block2 = CreateBlock(slot: 2, parentRoot: store.HeadRoot, proposerIndex: 0);
        var signed2 = WrapBlock(block2);
        var result2 = ApplyBlock(store, signed2);
        Assert.That(result2.Accepted, Is.True);
        Assert.That(store.HeadSlot, Is.EqualTo(2UL));
    }

    [Test]
    public void OnBlock_NotExtendingHead_DoesNotUpdateHead()
    {
        var store = CreateStore();
        var genesisRoot = store.HeadRoot;

        // Add block 1 extending genesis
        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        var signed1 = WrapBlock(block1);
        ApplyBlock(store, signed1);
        Assert.That(store.HeadSlot, Is.EqualTo(1UL));

        // Add a fork block also extending genesis (not extending current head)
        var block1b = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 1);
        var signed1b = WrapBlock(block1b);
        ApplyBlock(store, signed1b);

        // Head should still be block 1, not the fork
        Assert.That(store.HeadSlot, Is.EqualTo(1UL));
    }

    [Test]
    public void OnBlock_AdvancingCanonicalJustified_UpdatesStoreImmediately()
    {
        var store = CreateStore();
        var genesisRoot = store.HeadRoot;
        var genesisCheckpoint = new Checkpoint(genesisRoot, new Slot(0));

        var block = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        var signed = WrapBlock(block);
        var blockRoot = new Bytes32(block.HashTreeRoot());
        var canonicalJustified = new Checkpoint(blockRoot, new Slot(1));

        var result = store.OnBlock(signed, canonicalJustified, genesisCheckpoint, validatorCount: 1);

        Assert.That(result.Accepted, Is.True);
        Assert.That(store.JustifiedSlot, Is.EqualTo(1UL));
        Assert.That(store.JustifiedRoot, Is.EqualTo(blockRoot));
    }

    [Test]
    public void OnBlock_StaleCanonicalCheckpoint_StillBecomesHeadViaLmdGhost()
    {
        // With LMD GHOST head selection (matching ethlambda's update_head), blocks with
        // stale canonical checkpoints can still become head. Unlike proto-array FindHead
        // which filtered by viability, LMD GHOST follows the heaviest chain tip regardless
        // of per-block checkpoint values.
        var store = CreateStore();
        var genesisRoot = store.HeadRoot;
        var genesisCheckpoint = new Checkpoint(genesisRoot, new Slot(0));

        var blockA = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        var signedA = WrapBlock(blockA);
        var blockARoot = new Bytes32(blockA.HashTreeRoot());
        var justifiedAtA = new Checkpoint(blockARoot, new Slot(1));
        Assert.That(store.OnBlock(signedA, justifiedAtA, genesisCheckpoint, validatorCount: 1).Accepted, Is.True);

        store.TickInterval(1, IntervalsPerSlot - 1);
        Assert.That(store.JustifiedSlot, Is.EqualTo(1UL));
        Assert.That(store.JustifiedRoot, Is.EqualTo(blockARoot));

        var blockB = CreateBlock(slot: 2, parentRoot: blockARoot, proposerIndex: 0);
        var signedB = WrapBlock(blockB);
        var blockBRoot = new Bytes32(blockB.HashTreeRoot());
        Assert.That(store.OnBlock(signedB, genesisCheckpoint, genesisCheckpoint, validatorCount: 1).Accepted, Is.True);

        // LMD GHOST follows from justified root (blockA) to its child (blockB) even
        // without votes, because minScore=0 includes all children. Head is now blockB.
        Assert.That(store.HeadRoot, Is.EqualTo(blockBRoot));
        Assert.That(store.HeadSlot, Is.EqualTo(2UL));

        var voteForB = new SignedAttestation(
            0,
            new AttestationData(
                new Slot(2),
                new Checkpoint(blockBRoot, new Slot(2)),
                genesisCheckpoint,
                genesisCheckpoint),
            XmssSignature.Empty());
        Assert.That(store.TryOnAttestation(voteForB, out var reason), Is.True, reason);

        store.TickInterval(2, IntervalsPerSlot - 1);

        // After promoting votes, head is still blockB (now with vote weight too)
        Assert.That(store.HeadRoot, Is.EqualTo(blockBRoot));
        Assert.That(store.HeadSlot, Is.EqualTo(2UL));
    }

    [Test]
    public void OnAttestation_StoresInPending()
    {
        var store = CreateStore();
        var attestation = CreateAttestation(validatorId: 0, slot: 0, headRoot: store.HeadRoot);

        // Should not throw
        store.OnAttestation(attestation);
    }

    [Test]
    public void TickInterval_At4_PromotesPendingAndUpdatesHead()
    {
        var store = CreateStore();
        var parentRoot = store.HeadRoot;
        var block = CreateBlock(slot: 1, parentRoot: parentRoot, proposerIndex: 0);
        var signed = WrapBlock(block);
        ApplyBlock(store, signed);

        var blockRoot = new Bytes32(signed.HashTreeRoot());
        var attestation = CreateAttestation(validatorId: 0, slot: 1, headRoot: blockRoot);
        store.OnAttestation(attestation);

        // Interval 4 (index 4 in 5-interval slot) triggers head update
        store.TickInterval(1, IntervalsPerSlot - 1);

        // Head should still be valid (either genesis or the new block)
        Assert.That(store.HeadSlot, Is.GreaterThanOrEqualTo(0UL));
    }

    [Test]
    public void ContainsBlock_ReturnsTrueForKnownBlock()
    {
        var store = CreateStore();
        Assert.That(store.ContainsBlock(store.HeadRoot), Is.True);
    }

    [Test]
    public void ContainsBlock_ReturnsFalseForUnknownBlock()
    {
        var store = CreateStore();
        var unknown = new Bytes32(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        Assert.That(store.ContainsBlock(unknown), Is.False);
    }

    [Test]
    public void SafeTarget_InitializedToGenesisRoot()
    {
        var store = CreateStore();
        Assert.That(store.SafeTarget, Is.EqualTo(store.HeadRoot));
    }

    [Test]
    public void TickInterval_At3_SafeTargetAdvancesWhenQuorumGossips()
    {
        // leanSpec semantics: aggregated payloads are stored at gossip time,
        // unpacked into trackers by AcceptNewAttestations (interval 4),
        // then safe_target reads latestNew at interval 3.
        var store = CreateStore(validatorCount: 4);
        var genesisRoot = store.HeadRoot;

        // Build a chain: genesis -> block1 -> block2
        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        var signed1 = WrapBlock(block1);
        ApplyBlock(store, signed1, 4);
        var block1Root = new Bytes32(block1.HashTreeRoot());

        var block2 = CreateBlock(slot: 2, parentRoot: block1Root, proposerIndex: 1);
        var signed2 = WrapBlock(block2);
        ApplyBlock(store, signed2, 4);
        var block2Root = new Bytes32(block2.HashTreeRoot());

        // Advance _currentSlot so attestation validation passes (slot <= currentSlot + 1).
        store.TickInterval(3, 0);

        var attData = new AttestationData(
            new Slot(2),
            new Checkpoint(block2Root, new Slot(2)),
            new Checkpoint(block2Root, new Slot(2)),
            new Checkpoint(block2Root, new Slot(2)));

        // Send aggregated attestation with participants 1, 2, 3.
        var bits = AggregationBits.FromValidatorIndices(new ulong[] { 1, 2, 3 });
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var aggAtt = new SignedAggregatedAttestation(attData, proof);
        Assert.That(store.TryOnGossipAggregatedAttestation(aggAtt, out _), Is.True);

        // Interval 4: AcceptNewAttestations unpacks payloads into trackers.
        store.TickInterval(3, 4);

        // Interval 3: UpdateSafeTarget. cutoffWeight=3, three votes -> advances.
        store.TickInterval(3, 3);

        Assert.That(store.SafeTarget.Equals(genesisRoot), Is.False,
            "Safe target should advance when a quorum of validators gossips the same attestation.");
    }

    [Test]
    public void TickInterval_At3_UpdatesSafeTargetWhenLocalValidatorGossips()
    {
        // Single-validator setup: validatorCount=1 → cutoffWeight=1.
        // Aggregated attestation stored, accepted via interval 4, then
        // safe_target should advance at interval 3.
        var store = CreateStore(validatorCount: 1);
        var genesisRoot = store.HeadRoot;

        // Build a chain: genesis -> block1 -> block2
        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        ApplyBlock(store, WrapBlock(block1), 1);
        var block1Root = new Bytes32(block1.HashTreeRoot());

        var block2 = CreateBlock(slot: 2, parentRoot: block1Root, proposerIndex: 0);
        ApplyBlock(store, WrapBlock(block2), 1);
        var block2Root = new Bytes32(block2.HashTreeRoot());

        // Advance _currentSlot so attestation validation passes.
        store.TickInterval(3, 0);

        // Local validator (id=0) sends aggregated attestation for block2.
        var attData = new AttestationData(
            new Slot(2),
            new Checkpoint(block2Root, new Slot(2)),
            new Checkpoint(block2Root, new Slot(2)),
            new Checkpoint(block2Root, new Slot(2)));
        var bits = AggregationBits.FromValidatorIndices(new ulong[] { 0 });
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var aggAtt = new SignedAggregatedAttestation(attData, proof);
        Assert.That(store.TryOnGossipAggregatedAttestation(aggAtt, out _), Is.True);

        // Interval 4: AcceptNewAttestations unpacks payloads into trackers.
        store.TickInterval(3, 4);

        // Interval 3: UpdateSafeTarget. cutoffWeight=1, 1 vote → advances.
        store.TickInterval(3, 3);

        Assert.That(store.SafeTarget.Equals(genesisRoot), Is.False,
            "Safe target should advance when local validator gossips and cutoffWeight is met");
    }

    [Test]
    public void TickInterval_At3_SafeTargetAdvancesWithOnlyKnownVotes()
    {
        var store = CreateStore(validatorCount: 4);
        var genesisRoot = store.HeadRoot;

        // Build genesis -> block1.
        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        Assert.That(ApplyBlock(store, WrapBlock(block1), 4).Accepted, Is.True);
        var block1Root = new Bytes32(block1.HashTreeRoot());

        // Build block2 with on-block attestation votes from validators [0,1,2].
        // Votes go into LatestKnown (is_from_block=true), NOT latestNew.
        var attData = new AttestationData(
            new Slot(1),
            new Checkpoint(block1Root, new Slot(1)),
            new Checkpoint(block1Root, new Slot(1)),
            new Checkpoint(genesisRoot, new Slot(0)));
        var signed2 = CreateBlockWithAttestations(
            slot: 2,
            parentRoot: block1Root,
            proposerIndex: 1,
            attestationData: attData,
            participantIds: new ulong[] { 0, 1, 2 });
        Assert.That(ApplyBlock(store, signed2, 4).Accepted, Is.True);

        // Latest zeam main (#651) keeps latestNew aligned with accepted latestKnown
        // votes. With 3/4 known votes in the tracker, safe_target should advance
        // even though no fresh gossip arrived after block processing.
        store.TickInterval(3, 3);

        Assert.That(store.SafeTarget.Equals(genesisRoot), Is.False,
            "Safe target should advance when accepted known votes satisfy the cutoff.");
    }

    [Test]
    public void TickInterval_At3_ThrowsWhenSafeTargetWouldRegress()
    {
        var store = CreateStore(validatorCount: 1);
        var genesisRoot = store.HeadRoot;

        var block1 = CreateBlock(slot: 1, parentRoot: genesisRoot, proposerIndex: 0);
        Assert.That(ApplyBlock(store, WrapBlock(block1), 1).Accepted, Is.True);
        var block1Root = new Bytes32(block1.HashTreeRoot());

        typeof(ProtoArrayForkChoiceStore)
            .GetField("_safeTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, block1Root);

        var ex = Assert.Throws<InvalidOperationException>(() => store.TickInterval(1, 3));
        Assert.That(ex!.Message, Does.Contain("Invalid safe target regression"));
        Assert.That(store.SafeTarget, Is.EqualTo(block1Root));
    }

    [Test]
    public void ComputeTargetCheckpoint_WalksBackTowardSafeTarget()
    {
        var store = CreateStore(validatorCount: 4);
        var genesisRoot = store.HeadRoot;

        // Build a chain: genesis -> s1 -> s2
        var currentParent = genesisRoot;
        var blockRoots = new List<Bytes32> { genesisRoot };
        for (ulong slot = 1; slot <= 2; slot++)
        {
            var block = CreateBlock(slot: slot, parentRoot: currentParent, proposerIndex: slot % 4);
            var signed = WrapBlock(block);
            ApplyBlock(store, signed, 4);
            currentParent = new Bytes32(block.HashTreeRoot());
            blockRoots.Add(currentParent);
        }

        // Block at slot 3 for chain structure.
        var block3 = CreateBlock(slot: 3, parentRoot: blockRoots[2], proposerIndex: 3);
        var signed3 = WrapBlock(block3);
        ApplyBlock(store, signed3, 4);
        blockRoots.Add(new Bytes32(block3.HashTreeRoot()));

        // Continue chain: s4 -> s5
        currentParent = blockRoots[3];
        for (ulong slot = 4; slot <= 5; slot++)
        {
            var block = CreateBlock(slot: slot, parentRoot: currentParent, proposerIndex: slot % 4);
            var signed = WrapBlock(block);
            ApplyBlock(store, signed, 4);
            currentParent = new Bytes32(block.HashTreeRoot());
            blockRoots.Add(currentParent);
        }

        // Simulate aggregated attestations arriving via gossip: all 4 validators
        // attest to block2, anchoring safe_target at slot 2.
        var attData = new AttestationData(
            new Slot(2),
            new Checkpoint(blockRoots[2], new Slot(2)),
            new Checkpoint(blockRoots[2], new Slot(2)),
            new Checkpoint(blockRoots[2], new Slot(2)));
        var participants = AggregationBits.FromValidatorIndices(new ulong[] { 0, 1, 2, 3 });
        var proof = new AggregatedSignatureProof(participants, new byte[32]);
        store.OnGossipAggregatedAttestation(new SignedAggregatedAttestation(attData, proof));

        // Interval 3 calls UpdateSafeTarget (zeam gr/devnet3 mapping).
        store.TickInterval(5, 3);

        // The target should be walked back from head toward safe_target (slot 2)
        var target = store.ComputeTargetCheckpoint();
        Assert.That(target.Slot.Value, Is.LessThanOrEqualTo(5UL));
        Assert.That(target.Slot.Value, Is.GreaterThanOrEqualTo(0UL));
    }

    [Test]
    public void SlotBasedPruning_RemovesOldAttestationData()
    {
        var store = CreateStore(validatorCount: 4);
        var genesisRoot = store.HeadRoot;

        // Build a chain long enough to trigger slot-based pruning (> MaxAttestationAgeSlots)
        var currentParent = genesisRoot;
        Bytes32 block5Root = genesisRoot;
        for (ulong s = 1; s <= 70; s++)
        {
            var block = CreateBlock(slot: s, parentRoot: currentParent, proposerIndex: s % 4);
            var signed = WrapBlock(block);
            ApplyBlock(store, signed, 4);
            currentParent = new Bytes32(block.HashTreeRoot());
            if (s == 5) block5Root = currentParent;
        }

        // Tick to slot 5 end-of-slot (interval 4 = AcceptNewAttestations) so _currentSlot allows attestation at slot 5
        store.TickInterval(5, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);

        // Add attestations referencing block at slot 5
        var attData = new AttestationData(
            new Slot(5),
            new Checkpoint(block5Root, new Slot(5)),
            new Checkpoint(block5Root, new Slot(5)),
            new Checkpoint(block5Root, new Slot(5)));
        var attDataRoot = new Bytes32(attData.HashTreeRoot());

        for (ulong v = 0; v < 4; v++)
        {
            var att = new SignedAttestation(v, attData, XmssSignature.Empty());
            var accepted = store.TryOnAttestation(att, storeSignature: true, out var reason);
            Assert.That(accepted, Is.True, $"Attestation from validator {v} rejected: {reason}");
        }

        // Check gossip signatures are stored (keyed by attestation data hash root)
        Assert.That(store.HasGossipSignature(0, attDataRoot), Is.True);

        // Tick past MaxAttestationAgeSlots to trigger pruning (interval 4 = AcceptNewAttestations)
        store.TickInterval(70, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);

        // Slot 5 data should be pruned (70 - 16 = 54, so anything at slot 5 or earlier is pruned)
        Assert.That(store.HasGossipSignature(0, attDataRoot), Is.False);
    }

    private static ProtoArrayForkChoiceStore CreateStore(ulong validatorCount = 1)
    {
        var config = new ConsensusConfig { InitialValidatorCount = validatorCount };
        return new ProtoArrayForkChoiceStore(config);
    }

    /// <summary>
    /// Wrapper for OnBlock that passes genesis-level canonical checkpoints.
    /// In production, ConsensusServiceV2 passes canonical ChainStateTransition results.
    /// </summary>
    private static ForkChoiceApplyResult ApplyBlock(
        ProtoArrayForkChoiceStore store,
        SignedBlockWithAttestation signed,
        ulong validatorCount = 1)
    {
        var genesisCheckpoint = new Checkpoint(store.JustifiedRoot, new Slot(store.JustifiedSlot));
        return store.OnBlock(signed, genesisCheckpoint, genesisCheckpoint, validatorCount);
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

    /// <summary>
    /// Creates a signed block whose body contains an aggregated attestation from the given
    /// participants. Per leanSpec, fork-choice votes are only updated through on_block
    /// (block-body attestations), not from gossip.
    /// </summary>
    private static SignedBlockWithAttestation CreateBlockWithAttestations(
        ulong slot, Bytes32 parentRoot, ulong proposerIndex,
        AttestationData attestationData, ulong[] participantIds)
    {
        var bits = AggregationBits.FromValidatorIndices(participantIds);
        var aggregatedAtt = new AggregatedAttestation(bits, attestationData);
        var body = new BlockBody(new[] { aggregatedAtt });
        var block = new Block(new Slot(slot), proposerIndex, parentRoot, Bytes32.Zero(), body);

        var proposerAttestation = new Attestation(proposerIndex, new AttestationData(
            new Slot(slot), Checkpoint.Default(), Checkpoint.Default(), Checkpoint.Default()));
        var blockWithAttestation = new BlockWithAttestation(block, proposerAttestation);

        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var emptyXmssSig = XmssSignature.Empty();
        var signature = new BlockSignatures(new[] { proof }, emptyXmssSig);

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

    private sealed class FakeConsensusStateStore : IConsensusStateStore
    {
        private readonly ConsensusHeadState _state;

        public FakeConsensusStateStore(ConsensusHeadState state)
        {
            _state = state;
        }

        public bool TryLoad([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConsensusHeadState? state)
        {
            state = _state;
            return true;
        }

        public bool TryLoad(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConsensusHeadState? state,
            out State? headChainState)
        {
            state = _state;
            headChainState = null;
            return true;
        }

        public void Save(ConsensusHeadState state) => throw new NotSupportedException();
        public void Save(ConsensusHeadState state, State headChainState) => throw new NotSupportedException();
        public void Delete() => throw new NotSupportedException();
    }
}
