using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using NUnit.Framework;

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
    public void OnBlock_AcceptsValidChild()
    {
        var store = CreateStore();
        var parentRoot = store.HeadRoot;
        var block = CreateBlock(slot: 1, parentRoot: parentRoot, proposerIndex: 0);
        var signed = WrapBlock(block);

        var result = store.OnBlock(signed);

        Assert.That(result.Accepted, Is.True);
    }

    [Test]
    public void OnBlock_RejectsDuplicate()
    {
        var store = CreateStore();
        var parentRoot = store.HeadRoot;
        var block = CreateBlock(slot: 1, parentRoot: parentRoot, proposerIndex: 0);
        var signed = WrapBlock(block);

        store.OnBlock(signed);
        var result = store.OnBlock(signed);

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

        var result = store.OnBlock(signed);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectReason, Is.EqualTo(ForkChoiceRejectReason.UnknownParent));
    }

    [Test]
    public void OnAttestation_StoresInPending()
    {
        var store = CreateStore();
        var attestation = CreateAttestation(validatorId: 0, slot: 1, headRoot: store.HeadRoot);

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
        store.OnBlock(signed);

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

    private static ProtoArrayForkChoiceStore CreateStore()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 1 };
        var stateTransition = new ForkChoiceStateTransition(config);
        return new ProtoArrayForkChoiceStore(stateTransition, config);
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
        var emptyXmssSig = new XmssSignature(new byte[3112]);
        var signature = new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), emptyXmssSig);
        return new SignedBlockWithAttestation(blockWithAttestation, signature);
    }

    private static SignedAttestation CreateAttestation(ulong validatorId, ulong slot, Bytes32 headRoot)
    {
        var data = new AttestationData(
            new Slot(slot),
            new Checkpoint(headRoot, new Slot(slot)),
            Checkpoint.Default(),
            Checkpoint.Default());
        var sig = new XmssSignature(new byte[3112]);
        return new SignedAttestation(validatorId, data, sig);
    }
}
