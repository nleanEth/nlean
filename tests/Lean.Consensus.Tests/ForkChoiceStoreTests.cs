using Lean.Consensus;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class ForkChoiceStoreTests
{
    private static readonly Bytes32 CanonicalGenesisRoot = BuildCanonicalGenesisRoot();

    [Test]
    public void ApplyBlock_RejectsUnknownParent()
    {
        var store = new ForkChoiceStore();
        var unknownParent = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());
        var block = CreateSignedBlock(1, unknownParent, parentSlot: 1, sourceRoot: Bytes32.Zero(), sourceSlot: 0);
        var root = new Bytes32(block.Message.Block.HashTreeRoot());

        var result = store.ApplyBlock(block, root, currentSlot: 1);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.UnknownParent));
        Assert.That(store.HeadSlot, Is.EqualTo(0));
    }

    [Test]
    public void ApplyBlock_SelectsHigherSlotAsHead()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 4 };
        var store = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var genesisRoot = store.HeadRoot;
        var blockOne = CreateSignedBlock(1, genesisRoot, parentSlot: 0, sourceRoot: genesisRoot, sourceSlot: 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(2, blockOneRoot, parentSlot: 1, sourceRoot: genesisRoot, sourceSlot: 0);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());

        var resultOne = store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1);
        var resultTwo = store.ApplyBlock(blockTwo, blockTwoRoot, currentSlot: 2);

        Assert.That(resultOne.Accepted, Is.True);
        Assert.That(resultOne.HeadChanged, Is.True);
        Assert.That(resultTwo.Accepted, Is.True);
        Assert.That(resultTwo.HeadChanged, Is.True);
        Assert.That(store.HeadSlot, Is.EqualTo(2));
        Assert.That(store.HeadRoot, Is.EqualTo(blockTwoRoot));
    }

    [Test]
    public void TryComputeBlockStateRoot_AllowsImportedParentChainAcrossMultipleSlots()
    {
        var store = new ForkChoiceStore();
        var remoteStateRootOne = new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var remoteStateRootTwo = new Bytes32(Enumerable.Repeat((byte)0x22, 32).ToArray());

        var blockOne = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            stateRoot: remoteStateRootOne);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var blockTwo = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1,
            stateRoot: remoteStateRootTwo);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockTwo, blockTwoRoot, currentSlot: 2).Accepted, Is.True);

        var candidate = new Block(
            new Slot(3),
            ProposerIndex: 2,
            blockTwoRoot,
            Bytes32.Zero(),
            new BlockBody(Array.Empty<AggregatedAttestation>()));

        var canComputeStateRoot = store.TryComputeBlockStateRoot(candidate, out _, out var reason);

        Assert.That(canComputeStateRoot, Is.True, reason);
    }

    [Test]
    public void ApplyBlock_Duplicate_IsRejected()
    {
        var store = new ForkChoiceStore();
        var block = CreateSignedBlock(1, Bytes32.Zero(), parentSlot: 0, sourceRoot: Bytes32.Zero(), sourceSlot: 0);
        var root = new Bytes32(block.Message.Block.HashTreeRoot());

        var first = store.ApplyBlock(block, root, currentSlot: 1);
        var duplicate = store.ApplyBlock(block, root, currentSlot: 1);

        Assert.That(first.Accepted, Is.True);
        Assert.That(duplicate.Accepted, Is.False);
        Assert.That(duplicate.RejectReason, Is.EqualTo(ForkChoiceRejectReason.DuplicateBlock));
    }

    [Test]
    public void ApplyBlock_AllowsAttestationCheckpointOffParentChain()
    {
        var store = new ForkChoiceStore();

        var blockOne = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            proposerIndex: 0,
            proposerValidatorId: 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var canonicalBlockTwo = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            proposerIndex: 1,
            proposerValidatorId: 1);
        var canonicalBlockTwoRoot = new Bytes32(canonicalBlockTwo.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(canonicalBlockTwo, canonicalBlockTwoRoot, currentSlot: 2).Accepted, Is.True);

        var forkBlockTwo = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            proposerIndex: 2,
            proposerValidatorId: 2);
        var forkBlockTwoRoot = new Bytes32(forkBlockTwo.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(forkBlockTwo, forkBlockTwoRoot, currentSlot: 2).Accepted, Is.True);

        var invalidBlockThree = CreateSignedBlock(
            blockSlot: 3,
            parentRoot: canonicalBlockTwoRoot,
            parentSlot: 2,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: forkBlockTwoRoot,
            targetSlot: 2,
            headRoot: forkBlockTwoRoot,
            headSlot: 2,
            proposerIndex: 0,
            proposerValidatorId: 0);

        var invalidBlockThreeRoot = new Bytes32(invalidBlockThree.Message.Block.HashTreeRoot());
        var result = store.ApplyBlock(invalidBlockThree, invalidBlockThreeRoot, currentSlot: 3);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.None));
        Assert.That(store.ContainsBlock(invalidBlockThreeRoot), Is.True);
    }

    [Test]
    public void InitializeHead_UsesPersistedAnchor()
    {
        var store = new ForkChoiceStore();
        var anchorRoot = new Bytes32(Enumerable.Repeat((byte)0x22, 32).ToArray());
        store.InitializeHead(new ConsensusHeadState(12, anchorRoot.AsSpan()));

        var child = CreateSignedBlock(13, anchorRoot, parentSlot: 12, sourceRoot: anchorRoot, sourceSlot: 12);
        var childRoot = new Bytes32(child.Message.Block.HashTreeRoot());
        var result = store.ApplyBlock(child, childRoot, currentSlot: 13);

        Assert.That(result.Accepted, Is.True);
        Assert.That(store.HeadSlot, Is.EqualTo(13));
        Assert.That(store.HeadRoot, Is.EqualTo(childRoot));
    }

    [Test]
    public void InitializeHead_WithChainSnapshot_RestoresNonZeroHeadTransitionContext()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 4 };
        var original = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var genesisRoot = original.HeadRoot;
        var blockOne = CreateSignedBlock(1, genesisRoot, parentSlot: 0, sourceRoot: genesisRoot, sourceSlot: 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(original.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var blockTwo = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: genesisRoot,
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            proposerIndex: 1,
            proposerValidatorId: 1);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
        Assert.That(original.ApplyBlock(blockTwo, blockTwoRoot, currentSlot: 2).Accepted, Is.True);

        var persistedHeadState = original.CreateHeadState();
        Assert.That(original.TryGetHeadChainState(out var persistedHeadChainState), Is.True);
        Assert.That(persistedHeadChainState, Is.Not.Null);

        var candidate = CreateSignedBlock(
            blockSlot: 3,
            parentRoot: blockTwoRoot,
            parentSlot: 2,
            sourceRoot: genesisRoot,
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockTwoRoot,
            headSlot: 2,
            proposerIndex: 2,
            proposerValidatorId: 2);

        var withoutSnapshot = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        withoutSnapshot.InitializeHead(persistedHeadState);
        var computedWithoutSnapshot = withoutSnapshot.TryComputeBlockStateRoot(candidate.Message.Block, out _, out var reasonWithoutSnapshot);
        Assert.That(computedWithoutSnapshot, Is.False);
        Assert.That(reasonWithoutSnapshot, Does.Contain("Missing chain state snapshot"));

        var restored = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        restored.InitializeHead(persistedHeadState, persistedHeadChainState);
        var computedWithSnapshot = restored.TryComputeBlockStateRoot(candidate.Message.Block, out _, out var reasonWithSnapshot);
        Assert.That(computedWithSnapshot, Is.True, reasonWithSnapshot);
    }

    [Test]
    public void InitializeHead_RestoresCheckpointsAndSafeTarget()
    {
        var store = new ForkChoiceStore();
        var headRoot = new Bytes32(Enumerable.Repeat((byte)0x21, 32).ToArray());
        var justifiedRoot = new Bytes32(Enumerable.Repeat((byte)0x31, 32).ToArray());
        var finalizedRoot = new Bytes32(Enumerable.Repeat((byte)0x41, 32).ToArray());
        var safeTargetRoot = new Bytes32(Enumerable.Repeat((byte)0x51, 32).ToArray());
        store.InitializeHead(new ConsensusHeadState(
            headSlot: 50,
            headRoot: headRoot.AsSpan(),
            latestJustifiedSlot: 32,
            latestJustifiedRoot: justifiedRoot.AsSpan(),
            latestFinalizedSlot: 24,
            latestFinalizedRoot: finalizedRoot.AsSpan(),
            safeTargetSlot: 40,
            safeTargetRoot: safeTargetRoot.AsSpan()));

        Assert.That(store.HeadSlot, Is.EqualTo(50));
        Assert.That(store.HeadRoot, Is.EqualTo(headRoot));
        Assert.That(store.LatestJustified.Slot.Value, Is.EqualTo(32));
        Assert.That(store.LatestJustified.Root, Is.EqualTo(justifiedRoot));
        Assert.That(store.LatestFinalized.Slot.Value, Is.EqualTo(24));
        Assert.That(store.LatestFinalized.Root, Is.EqualTo(finalizedRoot));
        Assert.That(store.SafeTargetSlot, Is.EqualTo(40));
        Assert.That(store.SafeTarget, Is.EqualTo(safeTargetRoot));
    }

    [Test]
    public void ApplyBlock_RejectsFutureSlot()
    {
        var store = new ForkChoiceStore();
        var block = CreateSignedBlock(4, Bytes32.Zero(), parentSlot: 0, sourceRoot: Bytes32.Zero(), sourceSlot: 0);
        var root = new Bytes32(block.Message.Block.HashTreeRoot());

        var result = store.ApplyBlock(block, root, currentSlot: 1);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.FutureSlot));
    }

    [Test]
    public void CreateAttestationData_UsesLatestJustifiedAsSource()
    {
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var attestationData = store.CreateAttestationData(slot: 2, safeTargetLookbackSlots: 3);

        Assert.That(attestationData.Source.Slot.Value, Is.EqualTo(store.LatestJustified.Slot.Value));
        Assert.That(attestationData.Source.Root, Is.EqualTo(store.LatestJustified.Root));
        Assert.That(attestationData.Head.Slot.Value, Is.EqualTo(store.HeadSlot));
        Assert.That(attestationData.Head.Root, Is.EqualTo(store.HeadRoot));
        Assert.That(attestationData.Target.Slot.Value, Is.GreaterThanOrEqualTo(attestationData.Source.Slot.Value));
        Assert.That(attestationData.Target.Slot.Value, Is.LessThanOrEqualTo(attestationData.Head.Slot.Value));
    }

    [Test]
    public void ApplyBlock_UsesConfiguredInitialValidatorCountForJustificationThreshold()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 3 };
        var store = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var genesisRoot = store.HeadRoot;

        var blockOne = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: genesisRoot,
            parentSlot: 0,
            sourceRoot: genesisRoot,
            sourceSlot: 0,
            aggregationBits: new[] { true, false, false },
            proposerIndex: 0,
            proposerValidatorId: 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var blockTwo = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: genesisRoot,
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1,
            aggregationBits: new[] { true, false, false },
            proposerIndex: 0,
            proposerValidatorId: 0);

        var result = store.ApplyBlock(blockTwo, new Bytes32(blockTwo.Message.Block.HashTreeRoot()), currentSlot: 2);

        Assert.That(result.Accepted, Is.True);
        Assert.That(store.LatestJustified.Slot.Value, Is.EqualTo(0));
    }

    [Test]
    public void ApplyBlock_JustifiesWithSingleVoteWhenInitialValidatorCountIsOne()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 1 };
        var store = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);

        var blockOne = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            aggregationBits: new[] { true },
            proposerIndex: 0,
            proposerValidatorId: 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var blockTwo = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1,
            aggregationBits: new[] { true },
            proposerIndex: 0,
            proposerValidatorId: 0);

        var result = store.ApplyBlock(blockTwo, new Bytes32(blockTwo.Message.Block.HashTreeRoot()), currentSlot: 2);

        Assert.That(result.Accepted, Is.True);
        Assert.That(store.LatestJustified.Slot.Value, Is.EqualTo(1));
        Assert.That(store.LatestJustified.Root, Is.EqualTo(blockOneRoot));
    }

    [Test]
    public void ApplyBlock_UsesInjectedStateTransitionOutput()
    {
        var transition = new StubTransition();
        var store = new ForkChoiceStore(transition);
        var block = CreateSignedBlock(1, Bytes32.Zero(), parentSlot: 0, sourceRoot: Bytes32.Zero(), sourceSlot: 0);
        var root = new Bytes32(block.Message.Block.HashTreeRoot());

        var result = store.ApplyBlock(block, root, currentSlot: 1);

        Assert.That(result.Accepted, Is.True);
        Assert.That(transition.Calls, Is.EqualTo(1));
        Assert.That(store.LatestJustified.Slot.Value, Is.EqualTo(1));
        Assert.That(store.LatestFinalized.Slot.Value, Is.EqualTo(1));
    }

    [Test]
    public void ApplyBlock_RejectsWhenTransitionFails()
    {
        var store = new ForkChoiceStore(new FailingTransition());
        var block = CreateSignedBlock(1, Bytes32.Zero(), parentSlot: 0, sourceRoot: Bytes32.Zero(), sourceSlot: 0);
        var root = new Bytes32(block.Message.Block.HashTreeRoot());

        var result = store.ApplyBlock(block, root, currentSlot: 1);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.StateTransitionFailed));
    }

    [Test]
    public void ApplyBlock_RejectsInvalidCheckpointRelationship()
    {
        var store = new ForkChoiceStore();
        var badSource = new Checkpoint(Bytes32.Zero(), new Slot(2));
        var badTarget = new Checkpoint(Bytes32.Zero(), new Slot(1));
        var block = CreateSignedBlock(
            1,
            Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: badSource.Root,
            sourceSlot: badSource.Slot.Value,
            targetRoot: badTarget.Root,
            targetSlot: badTarget.Slot.Value);
        var root = new Bytes32(block.Message.Block.HashTreeRoot());

        var result = store.ApplyBlock(block, root, currentSlot: 1);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.InvalidAttestation));
    }

    [Test]
    public void ApplyBlock_UsesAttestationWeightOverHigherSlotBranch()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 8 };
        var store = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var genesisRoot = store.HeadRoot;

        var blockOne = CreateSignedBlock(1, genesisRoot, 0, genesisRoot, 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var branchA = CreateSignedBlock(2, blockOneRoot, 1, genesisRoot, 0);
        var branchARoot = new Bytes32(branchA.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(branchA, branchARoot, currentSlot: 2).Accepted, Is.True);

        var branchB = CreateSignedBlock(5, blockOneRoot, 1, genesisRoot, 0);
        var branchBRoot = new Bytes32(branchB.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(branchB, branchBRoot, currentSlot: 5).Accepted, Is.True);

        // Weight the lower-slot branch by voting for branchA root.
        var weightedBranchA = CreateSignedBlock(
            4,
            branchARoot,
            2,
            genesisRoot,
            0,
            targetRoot: branchARoot,
            targetSlot: 2,
            headRoot: branchARoot,
            headSlot: 2,
            aggregationBits: new[] { true, true, true, true, true });
        var weightedBranchARoot = new Bytes32(weightedBranchA.Message.Block.HashTreeRoot());
        var weightedResult = store.ApplyBlock(weightedBranchA, weightedBranchARoot, currentSlot: 5);

        Assert.That(weightedResult.Accepted, Is.True);
        Assert.That(store.HeadSlot, Is.EqualTo(4));
        Assert.That(store.HeadRoot, Is.EqualTo(weightedBranchARoot));
    }

    [Test]
    public void ApplyBlock_TieBreaksEqualWeightByLexicographicallyHigherRoot()
    {
        var store = new ForkChoiceStore();

        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var lowBranch = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            proposerIndex: 0,
            proposerValidatorId: 0,
            aggregationBits: new[] { true },
            stateRoot: new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray()));
        var lowBranchRoot = new Bytes32(lowBranch.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(lowBranch, lowBranchRoot, currentSlot: 2).Accepted, Is.True);

        var lowBranchRootKey = Convert.ToHexString(lowBranchRoot.AsSpan());
        SignedBlockWithAttestation? highBranch = null;
        Bytes32 highBranchRoot = default;
        for (var nonce = 0; nonce <= byte.MaxValue; nonce++)
        {
            var candidate = CreateSignedBlock(
                blockSlot: 5,
                parentRoot: blockOneRoot,
                parentSlot: 1,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerIndex: 0,
                proposerValidatorId: 0,
                aggregationBits: new[] { true },
                stateRoot: new Bytes32(Enumerable.Repeat((byte)nonce, 32).ToArray()));
            var candidateRoot = new Bytes32(candidate.Message.Block.HashTreeRoot());
            var candidateRootKey = Convert.ToHexString(candidateRoot.AsSpan());
            if (StringComparer.Ordinal.Compare(candidateRootKey, lowBranchRootKey) < 0)
            {
                highBranch = candidate;
                highBranchRoot = candidateRoot;
                break;
            }
        }

        Assert.That(highBranch, Is.Not.Null, "Failed to construct deterministic root ordering for tie-break test.");
        Assert.That(store.ApplyBlock(highBranch!, highBranchRoot, currentSlot: 5).Accepted, Is.True);
        Assert.That(store.HeadSlot, Is.EqualTo(2));
        Assert.That(store.HeadRoot, Is.EqualTo(lowBranchRoot));
    }

    [Test]
    public void ApplyBlock_UpdatesJustifiedAndFinalizedFromTransitionOutput()
    {
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var blockTwo = CreateSignedBlock(
            2,
            blockOneRoot,
            1,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1,
            aggregationBits: new[] { true });
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockTwo, blockTwoRoot, currentSlot: 2).Accepted, Is.True);

        var blockThree = CreateSignedBlock(
            3,
            blockTwoRoot,
            2,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: blockTwoRoot,
            targetSlot: 2,
            headRoot: blockTwoRoot,
            headSlot: 2,
            aggregationBits: new[] { true });
        Assert.That(store.ApplyBlock(blockThree, new Bytes32(blockThree.Message.Block.HashTreeRoot()), currentSlot: 3).Accepted, Is.True);

        Assert.That(store.LatestJustified.Slot.Value, Is.EqualTo(2));
        Assert.That(store.LatestJustified.Root, Is.EqualTo(blockTwoRoot));
        Assert.That(store.LatestFinalized.Slot.Value, Is.EqualTo(1));
        Assert.That(store.LatestFinalized.Root, Is.EqualTo(blockOneRoot));
        Assert.That(store.SafeTargetSlot, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void ApplyGossipAttestation_AcceptsValidVote()
    {
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var signedAttestation = CreateSignedAttestation(
            validatorId: 0,
            attestationSlot: 1,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1);

        var result = store.ApplyGossipAttestation(signedAttestation, currentSlot: 1);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.None));
    }

    [Test]
    public void ApplyGossipAttestation_RejectsValidatorIdBeyondCurrentCount()
    {
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var signedAttestation = CreateSignedAttestation(
            validatorId: 1024,
            attestationSlot: 1,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1);

        var result = store.ApplyGossipAttestation(signedAttestation, currentSlot: 1);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.InvalidAttestation));
        Assert.That(result.Reason, Does.Contain("out of range"));
    }

    [Test]
    public void OnSlotTick_DoesNotResetSafeTarget_WhenVotesWerePromoted()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 3 };
        var store = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var genesisRoot = store.HeadRoot;

        var blockOne = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: genesisRoot,
            parentSlot: 0,
            sourceRoot: genesisRoot,
            sourceSlot: 0,
            aggregationBits: new[] { true, false, false },
            proposerIndex: 0,
            proposerValidatorId: 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var validatorOneVote = CreateSignedAttestation(
            validatorId: 1,
            attestationSlot: 1,
            sourceRoot: genesisRoot,
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1);
        Assert.That(store.ApplyGossipAttestation(validatorOneVote, currentSlot: 1).Accepted, Is.True);

        var validatorTwoVote = CreateSignedAttestation(
            validatorId: 2,
            attestationSlot: 1,
            sourceRoot: genesisRoot,
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1);
        Assert.That(store.ApplyGossipAttestation(validatorTwoVote, currentSlot: 1).Accepted, Is.True);

        Assert.That(store.SafeTargetSlot, Is.EqualTo(0));
        Assert.That(store.SafeTarget, Is.EqualTo(genesisRoot));

        var tickResult = store.OnSlotTick(currentSlot: 1);

        Assert.That(tickResult.Accepted, Is.True);
        Assert.That(store.SafeTargetSlot, Is.EqualTo(1));
        Assert.That(store.SafeTarget, Is.EqualTo(blockOneRoot));
    }

    [Test]
    public void ApplyBlock_RejectsUnknownHeadRootInBlockAttestation()
    {
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var unknownHeadRoot = new Bytes32(Enumerable.Repeat((byte)0x99, 32).ToArray());
        var blockTwo = CreateSignedBlock(
            2,
            blockOneRoot,
            1,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: unknownHeadRoot,
            headSlot: 2);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());

        var result = store.ApplyBlock(blockTwo, blockTwoRoot, currentSlot: 2);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.InvalidAttestation));
        Assert.That(result.Reason, Does.Contain("Unknown head root"));
        Assert.That(store.HeadSlot, Is.EqualTo(1));
    }

    [Test]
    public void ApplyBlock_AcceptsCurrentBlockAsAttestationHeadRoot()
    {
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var blockTwo = CreateSignedBlock(
            2,
            blockOneRoot,
            1,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: blockOneRoot,
            targetSlot: 1);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());

        // Match Zeam/Ream behavior: attestation head in block payload can point to the block itself.
        var selfHeadData = new AttestationData(
            new Slot(2),
            new Checkpoint(blockTwoRoot, new Slot(2)),
            new Checkpoint(blockOneRoot, new Slot(1)),
            new Checkpoint(blockOneRoot, new Slot(1)));
        var selfHeadAggregated = new AggregatedAttestation(
            blockTwo.Message.Block.Body.Attestations[0].AggregationBits,
            selfHeadData);
        var selfHeadProposerAttestation = new Attestation(
            blockTwo.Message.ProposerAttestation.ValidatorId,
            selfHeadData);
        var selfHeadMessage = new BlockWithAttestation(
            new Block(
                blockTwo.Message.Block.Slot,
                blockTwo.Message.Block.ProposerIndex,
                blockTwo.Message.Block.ParentRoot,
                blockTwo.Message.Block.StateRoot,
                new BlockBody(new[] { selfHeadAggregated })),
            selfHeadProposerAttestation);
        var selfHeadSignedBlock = new SignedBlockWithAttestation(selfHeadMessage, blockTwo.Signature);

        var result = store.ApplyBlock(selfHeadSignedBlock, blockTwoRoot, currentSlot: 2);

        Assert.That(result.Accepted, Is.True);
        Assert.That(store.HeadSlot, Is.EqualTo(2));
        Assert.That(store.HeadRoot, Is.EqualTo(blockTwoRoot));
    }

    [Test]
    public void ApplyGossipAttestation_RejectsUnknownHeadRoot()
    {
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var unknownHeadRoot = new Bytes32(Enumerable.Repeat((byte)0x88, 32).ToArray());
        var signedAttestation = CreateSignedAttestation(
            validatorId: 3,
            attestationSlot: 1,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: unknownHeadRoot,
            headSlot: 1);

        var result = store.ApplyGossipAttestation(signedAttestation, currentSlot: 1);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.InvalidAttestation));
        Assert.That(result.Reason, Does.Contain("Unknown head root"));
    }

    [Test]
    public void ApplyGossipAttestation_AllowsCheckpointOffHeadChain()
    {
        var store = new ForkChoiceStore();

        var blockOne = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            proposerIndex: 0,
            proposerValidatorId: 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var canonicalBlockTwo = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            proposerIndex: 1,
            proposerValidatorId: 1);
        var canonicalBlockTwoRoot = new Bytes32(canonicalBlockTwo.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(canonicalBlockTwo, canonicalBlockTwoRoot, currentSlot: 2).Accepted, Is.True);

        var forkBlockTwo = CreateSignedBlock(
            blockSlot: 2,
            parentRoot: blockOneRoot,
            parentSlot: 1,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            proposerIndex: 2,
            proposerValidatorId: 2);
        var forkBlockTwoRoot = new Bytes32(forkBlockTwo.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(forkBlockTwo, forkBlockTwoRoot, currentSlot: 2).Accepted, Is.True);

        var signedAttestation = CreateSignedAttestation(
            validatorId: 0,
            attestationSlot: 2,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: forkBlockTwoRoot,
            targetSlot: 2,
            headRoot: canonicalBlockTwoRoot,
            headSlot: 2);

        var result = store.ApplyGossipAttestation(signedAttestation, currentSlot: 2);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.None));
    }

    [Test]
    public void ApplyBlock_RejectsAggregatedAttestationWithEmptyParticipantsBitlist()
    {
        var store = new ForkChoiceStore();
        var block = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            aggregationBits: new[] { false, false, false },
            proposerIndex: 1,
            proposerValidatorId: 1);
        var blockRoot = new Bytes32(block.Message.Block.HashTreeRoot());

        var result = store.ApplyBlock(block, blockRoot, currentSlot: 1);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.InvalidAttestation));
        Assert.That(result.Reason, Does.Contain("participants bitlist cannot be empty"));
    }

    private static SignedBlockWithAttestation CreateSignedBlock(
        ulong blockSlot,
        Bytes32 parentRoot,
        ulong parentSlot,
        Bytes32 sourceRoot,
        ulong sourceSlot,
        Bytes32? targetRoot = null,
        ulong? targetSlot = null,
        Bytes32? headRoot = null,
        ulong? headSlot = null,
        IReadOnlyList<bool>? aggregationBits = null,
        ulong proposerIndex = 7,
        ulong proposerValidatorId = 7,
        Bytes32? stateRoot = null)
    {
        var normalizedParentRoot = NormalizeRootAtSlot(parentRoot, parentSlot);
        var normalizedSourceRoot = NormalizeRootAtSlot(sourceRoot, sourceSlot);
        var effectiveTargetSlot = targetSlot ?? parentSlot;
        var effectiveTargetRoot = NormalizeRootAtSlot(targetRoot ?? normalizedParentRoot, effectiveTargetSlot);
        var effectiveHeadSlot = headSlot ?? parentSlot;
        var effectiveHeadRoot = NormalizeRootAtSlot(headRoot ?? normalizedParentRoot, effectiveHeadSlot);
        var effectiveStateRoot = stateRoot ?? new Bytes32(Enumerable.Repeat((byte)5, 32).ToArray());

        var proposerAttestationData = new AttestationData(
            new Slot(blockSlot),
            new Checkpoint(effectiveHeadRoot, new Slot(effectiveHeadSlot)),
            new Checkpoint(effectiveTargetRoot, new Slot(effectiveTargetSlot)),
            new Checkpoint(normalizedSourceRoot, new Slot(sourceSlot)));

        var aggregatedAttestation = new AggregatedAttestation(
            new AggregationBits(aggregationBits ?? new[] { true, false, true, true }),
            proposerAttestationData);

        var block = new Block(
            new Slot(blockSlot),
            proposerIndex,
            normalizedParentRoot,
            effectiveStateRoot,
            new BlockBody(new[] { aggregatedAttestation }));

        var blockWithAttestation = new BlockWithAttestation(
            block,
            new Attestation(proposerValidatorId, proposerAttestationData));

        var signatures = new BlockSignatures(
            new[]
            {
                new AggregatedSignatureProof(
                    new AggregationBits(new[] { true, true, false, true }),
                    new byte[] { 0xAA, 0xBB, 0xCC })
            },
            XmssSignature.Empty());

        return new SignedBlockWithAttestation(blockWithAttestation, signatures);
    }

    private static Bytes32 NormalizeRootAtSlot(Bytes32 root, ulong slot)
    {
        return slot == 0 && root.Equals(Bytes32.Zero()) ? CanonicalGenesisRoot : root;
    }

    private static Bytes32 BuildCanonicalGenesisRoot()
    {
        return new ForkChoiceStore().HeadRoot;
    }

    private static SignedAttestation CreateSignedAttestation(
        ulong validatorId,
        ulong attestationSlot,
        Bytes32 sourceRoot,
        ulong sourceSlot,
        Bytes32 targetRoot,
        ulong targetSlot,
        Bytes32 headRoot,
        ulong headSlot)
    {
        var normalizedSourceRoot = NormalizeRootAtSlot(sourceRoot, sourceSlot);
        var normalizedTargetRoot = NormalizeRootAtSlot(targetRoot, targetSlot);
        var normalizedHeadRoot = NormalizeRootAtSlot(headRoot, headSlot);
        var data = new AttestationData(
            new Slot(attestationSlot),
            new Checkpoint(normalizedHeadRoot, new Slot(headSlot)),
            new Checkpoint(normalizedTargetRoot, new Slot(targetSlot)),
            new Checkpoint(normalizedSourceRoot, new Slot(sourceSlot)));

        return new SignedAttestation(validatorId, data, XmssSignature.Empty());
    }

    private sealed class StubTransition : IForkChoiceStateTransition
    {
        public int Calls { get; private set; }

        public bool TryTransition(
            ForkChoiceNodeState parentState,
            SignedBlockWithAttestation signedBlock,
            out ForkChoiceNodeState postState,
            out string reason)
        {
            Calls++;
            var checkpoint = new Checkpoint(parentState.LatestJustified.Root, signedBlock.Message.Block.Slot);
            postState = new ForkChoiceNodeState(checkpoint, checkpoint, parentState.ValidatorCount + 1);
            reason = string.Empty;
            return true;
        }
    }

    private sealed class FailingTransition : IForkChoiceStateTransition
    {
        public bool TryTransition(
            ForkChoiceNodeState parentState,
            SignedBlockWithAttestation signedBlock,
            out ForkChoiceNodeState postState,
            out string reason)
        {
            postState = parentState;
            reason = "transition failed";
            return false;
        }
    }
}
