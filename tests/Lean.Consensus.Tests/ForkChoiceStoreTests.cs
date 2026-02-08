using Lean.Consensus;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class ForkChoiceStoreTests
{
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
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), parentSlot: 0, sourceRoot: Bytes32.Zero(), sourceSlot: 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(2, blockOneRoot, parentSlot: 1, sourceRoot: Bytes32.Zero(), sourceSlot: 0);
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
        var store = new ForkChoiceStore();

        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockOne, blockOneRoot, currentSlot: 1).Accepted, Is.True);

        var branchA = CreateSignedBlock(2, blockOneRoot, 1, Bytes32.Zero(), 0);
        var branchARoot = new Bytes32(branchA.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(branchA, branchARoot, currentSlot: 2).Accepted, Is.True);

        var branchB = CreateSignedBlock(5, blockOneRoot, 1, Bytes32.Zero(), 0);
        var branchBRoot = new Bytes32(branchB.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(branchB, branchBRoot, currentSlot: 5).Accepted, Is.True);

        // Weight the lower-slot branch by voting for branchA root.
        var weightedBranchA = CreateSignedBlock(
            4,
            branchARoot,
            2,
            Bytes32.Zero(),
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
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
        Assert.That(store.ApplyBlock(blockTwo, blockTwoRoot, currentSlot: 2).Accepted, Is.True);

        Assert.That(store.LatestJustified.Slot.Value, Is.EqualTo(1));
        Assert.That(store.LatestJustified.Root, Is.EqualTo(blockOneRoot));
        Assert.That(store.LatestFinalized.Slot.Value, Is.EqualTo(1));
        Assert.That(store.LatestFinalized.Root, Is.EqualTo(blockOneRoot));
        Assert.That(store.SafeTargetSlot, Is.GreaterThanOrEqualTo(0));
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
        IReadOnlyList<bool>? aggregationBits = null)
    {
        var effectiveTargetRoot = targetRoot ?? parentRoot;
        var effectiveTargetSlot = targetSlot ?? parentSlot;
        var effectiveHeadRoot = headRoot ?? parentRoot;
        var effectiveHeadSlot = headSlot ?? parentSlot;

        var proposerAttestationData = new AttestationData(
            new Slot(blockSlot),
            new Checkpoint(effectiveHeadRoot, new Slot(effectiveHeadSlot)),
            new Checkpoint(effectiveTargetRoot, new Slot(effectiveTargetSlot)),
            new Checkpoint(sourceRoot, new Slot(sourceSlot)));

        var aggregatedAttestation = new AggregatedAttestation(
            new AggregationBits(aggregationBits ?? new[] { true, false, true, true }),
            proposerAttestationData);

        var block = new Block(
            new Slot(blockSlot),
            7,
            parentRoot,
            new Bytes32(Enumerable.Repeat((byte)5, 32).ToArray()),
            new BlockBody(new[] { aggregatedAttestation }));

        var blockWithAttestation = new BlockWithAttestation(
            block,
            new Attestation(7, proposerAttestationData));

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
}
