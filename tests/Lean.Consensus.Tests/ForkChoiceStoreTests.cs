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
        var block = CreateSignedBlock(1, new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray()));
        var root = new Bytes32(block.Message.Block.HashTreeRoot());

        var result = store.ApplyBlock(block, root);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.UnknownParent));
        Assert.That(store.HeadSlot, Is.EqualTo(0));
    }

    [Test]
    public void ApplyBlock_SelectsHigherSlotAsHead()
    {
        var store = new ForkChoiceStore();
        var blockOne = CreateSignedBlock(1, Bytes32.Zero());
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(2, blockOneRoot);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());

        var resultOne = store.ApplyBlock(blockOne, blockOneRoot);
        var resultTwo = store.ApplyBlock(blockTwo, blockTwoRoot);

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
        var block = CreateSignedBlock(1, Bytes32.Zero());
        var root = new Bytes32(block.Message.Block.HashTreeRoot());

        var first = store.ApplyBlock(block, root);
        var duplicate = store.ApplyBlock(block, root);

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

        var child = CreateSignedBlock(13, anchorRoot);
        var childRoot = new Bytes32(child.Message.Block.HashTreeRoot());
        var result = store.ApplyBlock(child, childRoot);

        Assert.That(result.Accepted, Is.True);
        Assert.That(store.HeadSlot, Is.EqualTo(13));
        Assert.That(store.HeadRoot, Is.EqualTo(childRoot));
    }

    private static SignedBlockWithAttestation CreateSignedBlock(ulong blockSlot, Bytes32 parentRoot)
    {
        var proposerAttestationData = new AttestationData(
            new Slot(blockSlot),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)1, 32).ToArray()), new Slot(9)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)2, 32).ToArray()), new Slot(10)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)3, 32).ToArray()), new Slot(8)));

        var aggregatedAttestation = new AggregatedAttestation(
            new AggregationBits(new[] { true, false, true, true }),
            proposerAttestationData);

        var block = new Block(
            new Slot(blockSlot),
            7,
            parentRoot,
            new Bytes32(Enumerable.Repeat((byte)5, 32).ToArray()),
            new BlockBody(new[] { aggregatedAttestation }));

        var blockWithAttestation = new BlockWithAttestation(
            block,
            new Attestation(42, proposerAttestationData));

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
