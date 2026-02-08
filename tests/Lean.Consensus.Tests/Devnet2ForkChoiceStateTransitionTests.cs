using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class Devnet2ForkChoiceStateTransitionTests
{
    [Test]
    public void TryTransition_RejectsWhenValidatorCountExceedsConfiguredLimit()
    {
        var transition = new Devnet2ForkChoiceStateTransition(new ConsensusConfig
        {
            MaxValidatorCount = 4
        });
        var parentState = new ForkChoiceNodeState(Checkpoint.Default(), Checkpoint.Default(), 1);
        var block = CreateSignedBlock(
            slot: 1,
            proposerIndex: 7,
            proposerValidatorId: 7,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0);

        var ok = transition.TryTransition(parentState, block, out _, out var reason);

        Assert.That(ok, Is.False);
        Assert.That(reason, Does.Contain("exceeds configured limit"));
    }

    [Test]
    public void TryTransition_UpdatesJustifiedAndFinalizedCheckpoints()
    {
        var transition = new Devnet2ForkChoiceStateTransition(new ConsensusConfig());
        var parentState = new ForkChoiceNodeState(Checkpoint.Default(), Checkpoint.Default(), 1);
        var checkpointRoot = new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var block = CreateSignedBlock(
            slot: 2,
            proposerIndex: 1,
            proposerValidatorId: 1,
            parentRoot: checkpointRoot,
            parentSlot: 1,
            sourceRoot: checkpointRoot,
            sourceSlot: 1,
            targetRoot: checkpointRoot,
            targetSlot: 1,
            headRoot: checkpointRoot,
            headSlot: 1);

        var ok = transition.TryTransition(parentState, block, out var postState, out _);

        Assert.That(ok, Is.True);
        Assert.That(postState.LatestJustified.Slot.Value, Is.EqualTo(1));
        Assert.That(postState.LatestFinalized.Slot.Value, Is.EqualTo(1));
        Assert.That(postState.ValidatorCount, Is.EqualTo(2));
    }

    private static SignedBlockWithAttestation CreateSignedBlock(
        ulong slot,
        ulong proposerIndex,
        ulong proposerValidatorId,
        Bytes32 parentRoot,
        ulong parentSlot,
        Bytes32? sourceRoot = null,
        ulong? sourceSlot = null,
        Bytes32? targetRoot = null,
        ulong? targetSlot = null,
        Bytes32? headRoot = null,
        ulong? headSlot = null)
    {
        var effectiveSourceRoot = sourceRoot ?? parentRoot;
        var effectiveSourceSlot = sourceSlot ?? parentSlot;
        var effectiveTargetRoot = targetRoot ?? parentRoot;
        var effectiveTargetSlot = targetSlot ?? parentSlot;
        var effectiveHeadRoot = headRoot ?? parentRoot;
        var effectiveHeadSlot = headSlot ?? parentSlot;

        var data = new AttestationData(
            new Slot(slot),
            new Checkpoint(effectiveHeadRoot, new Slot(effectiveHeadSlot)),
            new Checkpoint(effectiveTargetRoot, new Slot(effectiveTargetSlot)),
            new Checkpoint(effectiveSourceRoot, new Slot(effectiveSourceSlot)));

        var aggregated = new AggregatedAttestation(
            new AggregationBits(new[] { true, true }),
            data);

        var block = new Block(
            new Slot(slot),
            proposerIndex,
            parentRoot,
            Bytes32.Zero(),
            new BlockBody(new[] { aggregated }));

        var blockWithAttestation = new BlockWithAttestation(block, new Attestation(proposerValidatorId, data));
        var signatures = new BlockSignatures(
            new[]
            {
                new AggregatedSignatureProof(
                    new AggregationBits(new[] { true, true }),
                    new byte[] { 0x01, 0x02 })
            },
            XmssSignature.Empty());

        return new SignedBlockWithAttestation(blockWithAttestation, signatures);
    }
}
