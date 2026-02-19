using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class ForkChoiceStateTransitionTests
{
    [Test]
    public void TryTransition_RejectsWhenValidatorCountExceedsConfiguredLimit()
    {
        var transition = new ForkChoiceStateTransition(new ConsensusConfig
        {
            MaxValidatorCount = 4
        });
        var parentState = new ForkChoiceNodeState(Checkpoint.Default(), Checkpoint.Default(), 1);
        var block = CreateSignedBlock(
            slot: 1,
            proposerIndex: 7,
            proposerValidatorId: 7,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: NewRoot(0x11),
            targetSlot: 1,
            headRoot: NewRoot(0x11),
            headSlot: 1,
            aggregationBits: new[] { false, false, false, false, false, false, false, true });

        var ok = transition.TryTransition(parentState, block, out _, out var reason);

        Assert.That(ok, Is.False);
        Assert.That(reason, Does.Contain("exceeds configured limit"));
    }

    [Test]
    public void TryTransition_RejectsEmptyAggregatedAttestationParticipants()
    {
        var transition = new ForkChoiceStateTransition(new ConsensusConfig());
        var parentState = new ForkChoiceNodeState(Checkpoint.Default(), Checkpoint.Default(), 1);
        var block = CreateSignedBlock(
            slot: 1,
            proposerIndex: 0,
            proposerValidatorId: 0,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: NewRoot(0xAB),
            targetSlot: 1,
            headRoot: NewRoot(0xAB),
            headSlot: 1,
            aggregationBits: new[] { false, false, false });

        var ok = transition.TryTransition(parentState, block, out _, out var reason);

        Assert.That(ok, Is.False);
        Assert.That(reason, Is.EqualTo("Aggregated attestation must reference at least one validator."));
    }

    [Test]
    public void TryTransition_AccumulatesVotesAcrossBlocksAndJustifiesAtThreshold()
    {
        var transition = new ForkChoiceStateTransition(new ConsensusConfig());
        var targetRoot = NewRoot(0x22);

        var parentState = new ForkChoiceNodeState(
            Checkpoint.Default(),
            Checkpoint.Default(),
            3);

        var firstBlock = CreateSignedBlock(
            slot: 1,
            proposerIndex: 0,
            proposerValidatorId: 0,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: targetRoot,
            targetSlot: 1,
            headRoot: targetRoot,
            headSlot: 1,
            aggregationBits: new[] { true, false, false });

        var firstOk = transition.TryTransition(parentState, firstBlock, out var firstPostState, out var firstReason);

        Assert.That(firstOk, Is.True, firstReason);
        Assert.That(firstPostState.LatestJustified.Slot.Value, Is.EqualTo(0));
        Assert.That(firstPostState.LatestFinalized.Slot.Value, Is.EqualTo(0));
        Assert.That(firstPostState.JustificationVotes, Is.Not.Null);
        Assert.That(firstPostState.JustificationVotes!, Contains.Key(ToKey(targetRoot)));
        Assert.That(firstPostState.JustificationVotes![ToKey(targetRoot)].ValidatorIds, Is.EquivalentTo(new[] { 0UL }));

        var secondBlock = CreateSignedBlock(
            slot: 2,
            proposerIndex: 1,
            proposerValidatorId: 1,
            parentRoot: targetRoot,
            parentSlot: 1,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: targetRoot,
            targetSlot: 1,
            headRoot: targetRoot,
            headSlot: 1,
            aggregationBits: new[] { false, true, false });

        var secondOk = transition.TryTransition(firstPostState, secondBlock, out var secondPostState, out var secondReason);

        Assert.That(secondOk, Is.True, secondReason);
        Assert.That(secondPostState.LatestJustified.Slot.Value, Is.EqualTo(1));
        Assert.That(secondPostState.LatestJustified.Root, Is.EqualTo(targetRoot));
        Assert.That(secondPostState.LatestFinalized.Slot.Value, Is.EqualTo(0));
        Assert.That(secondPostState.JustificationVotes, Is.Null.Or.Empty);
        Assert.That(secondPostState.JustifiedSlots, Is.EquivalentTo(new[] { 1UL }));
    }

    [Test]
    public void TryTransition_DoesNotUseProposerAttestationVoteForJustificationThreshold()
    {
        var transition = new ForkChoiceStateTransition(new ConsensusConfig());
        var targetRoot = NewRoot(0x2A);

        var parentState = new ForkChoiceNodeState(
            Checkpoint.Default(),
            Checkpoint.Default(),
            3);

        var block = CreateSignedBlock(
            slot: 1,
            proposerIndex: 1,
            proposerValidatorId: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: targetRoot,
            targetSlot: 1,
            headRoot: targetRoot,
            headSlot: 1,
            aggregationBits: new[] { true, false, false });

        var ok = transition.TryTransition(parentState, block, out var postState, out var reason);

        Assert.That(ok, Is.True, reason);
        Assert.That(postState.LatestJustified.Slot.Value, Is.EqualTo(0));
        Assert.That(postState.LatestFinalized.Slot.Value, Is.EqualTo(0));
        Assert.That(postState.JustificationVotes, Is.Not.Null);
        Assert.That(postState.JustificationVotes!, Contains.Key(ToKey(targetRoot)));
        Assert.That(postState.JustificationVotes![ToKey(targetRoot)].ValidatorIds, Is.EquivalentTo(new[] { 0UL }));
        Assert.That(postState.JustifiedSlots, Is.Null.Or.Empty);
    }

    [Test]
    public void TryTransition_FinalizesSourceWhenTargetIsNextJustifiableSlot()
    {
        var transition = new ForkChoiceStateTransition(new ConsensusConfig());
        var sourceRoot = NewRoot(0x33);
        var targetRoot = NewRoot(0x44);
        var targetKey = ToKey(targetRoot);

        var parentState = new ForkChoiceNodeState(
            new Checkpoint(sourceRoot, new Slot(1)),
            Checkpoint.Default(),
            3,
            new Dictionary<string, ForkChoiceJustificationVote>(StringComparer.Ordinal)
            {
                [targetKey] = new ForkChoiceJustificationVote(2, new[] { 0UL })
            },
            new[] { 1UL });

        var block = CreateSignedBlock(
            slot: 3,
            proposerIndex: 1,
            proposerValidatorId: 1,
            parentRoot: sourceRoot,
            parentSlot: 1,
            sourceRoot: sourceRoot,
            sourceSlot: 1,
            targetRoot: targetRoot,
            targetSlot: 2,
            headRoot: targetRoot,
            headSlot: 2,
            aggregationBits: new[] { false, true, false });

        var ok = transition.TryTransition(parentState, block, out var postState, out var reason);

        Assert.That(ok, Is.True, reason);
        Assert.That(postState.LatestJustified.Slot.Value, Is.EqualTo(2));
        Assert.That(postState.LatestJustified.Root, Is.EqualTo(targetRoot));
        Assert.That(postState.LatestFinalized.Slot.Value, Is.EqualTo(1));
        Assert.That(postState.LatestFinalized.Root, Is.EqualTo(sourceRoot));
        Assert.That(postState.JustificationVotes, Is.Null.Or.Empty);
        Assert.That(postState.JustifiedSlots, Is.EquivalentTo(new[] { 2UL }));
    }

    [Test]
    public void TryTransition_DoesNotFinalizeWhenInterveningJustifiableSlotExists()
    {
        var transition = new ForkChoiceStateTransition(new ConsensusConfig());
        var sourceRoot = NewRoot(0x55);
        var targetRoot = NewRoot(0x66);

        var parentState = new ForkChoiceNodeState(
            new Checkpoint(sourceRoot, new Slot(1)),
            Checkpoint.Default(),
            3,
            new Dictionary<string, ForkChoiceJustificationVote>(StringComparer.Ordinal)
            {
                [ToKey(targetRoot)] = new ForkChoiceJustificationVote(4, new[] { 0UL })
            },
            new[] { 1UL });

        var block = CreateSignedBlock(
            slot: 5,
            proposerIndex: 1,
            proposerValidatorId: 1,
            parentRoot: sourceRoot,
            parentSlot: 1,
            sourceRoot: sourceRoot,
            sourceSlot: 1,
            targetRoot: targetRoot,
            targetSlot: 4,
            headRoot: targetRoot,
            headSlot: 4,
            aggregationBits: new[] { false, true, false });

        var ok = transition.TryTransition(parentState, block, out var postState, out var reason);

        Assert.That(ok, Is.True, reason);
        Assert.That(postState.LatestJustified.Slot.Value, Is.EqualTo(4));
        Assert.That(postState.LatestFinalized.Slot.Value, Is.EqualTo(0));
        Assert.That(postState.LatestFinalized.Root, Is.EqualTo(Bytes32.Zero()));
        Assert.That(postState.JustifiedSlots, Is.EquivalentTo(new[] { 1UL, 4UL }));
    }

    [Test]
    public void TryTransition_RejectsWhenTargetRootMapsToDifferentTargetSlots()
    {
        var transition = new ForkChoiceStateTransition(new ConsensusConfig());
        var targetRoot = NewRoot(0x77);
        var targetKey = ToKey(targetRoot);
        var parentState = new ForkChoiceNodeState(
            Checkpoint.Default(),
            Checkpoint.Default(),
            3,
            new Dictionary<string, ForkChoiceJustificationVote>(StringComparer.Ordinal)
            {
                [targetKey] = new ForkChoiceJustificationVote(1, new[] { 0UL })
            },
            null);

        var block = CreateSignedBlock(
            slot: 2,
            proposerIndex: 1,
            proposerValidatorId: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: targetRoot,
            targetSlot: 2,
            headRoot: targetRoot,
            headSlot: 2,
            aggregationBits: new[] { false, true, false });

        var ok = transition.TryTransition(parentState, block, out _, out var reason);

        Assert.That(ok, Is.False);
        Assert.That(reason, Is.EqualTo("Attestation target root maps to inconsistent target slots."));
    }

    private static SignedBlockWithAttestation CreateSignedBlock(
        ulong slot,
        ulong proposerIndex,
        ulong proposerValidatorId,
        Bytes32 parentRoot,
        ulong parentSlot,
        Bytes32 sourceRoot,
        ulong sourceSlot,
        Bytes32 targetRoot,
        ulong targetSlot,
        Bytes32 headRoot,
        ulong headSlot,
        IReadOnlyList<bool> aggregationBits)
    {
        var data = new AttestationData(
            new Slot(slot),
            new Checkpoint(headRoot, new Slot(headSlot)),
            new Checkpoint(targetRoot, new Slot(targetSlot)),
            new Checkpoint(sourceRoot, new Slot(sourceSlot)));

        var aggregated = new AggregatedAttestation(
            new AggregationBits(aggregationBits),
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
                    new AggregationBits(aggregationBits),
                    new byte[] { 0x01, 0x02 })
            },
            XmssSignature.Empty());

        return new SignedBlockWithAttestation(blockWithAttestation, signatures);
    }

    private static Bytes32 NewRoot(byte seed)
    {
        return new Bytes32(Enumerable.Repeat(seed, 32).ToArray());
    }

    private static string ToKey(Bytes32 root)
    {
        return Convert.ToHexString(root.AsSpan());
    }
}
