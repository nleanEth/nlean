using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.ForkChoice;

[TestFixture]
public sealed class ProtoArrayForkChoiceStoreAggregatorTests
{
    private const int IntervalsPerSlot = 5;

    [Test]
    public void OnGossipSignature_StoresSignature()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var dataRoot = new Bytes32(data.HashTreeRoot());
        var sig = new XmssSignature(new byte[XmssSignature.Length]);

        store.OnGossipSignature(0, dataRoot, sig);

        Assert.That(store.HasGossipSignature(0, dataRoot), Is.True);
        Assert.That(store.HasGossipSignature(1, dataRoot), Is.False);
    }

    [Test]
    public void OnGossipAggregatedAttestation_IncreasesPendingCount()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var bits = new AggregationBits(new[] { true, false, true, false });
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var signed = new SignedAggregatedAttestation(data, proof);

        store.OnGossipAggregatedAttestation(signed);

        Assert.That(store.PendingAggregatedPayloadCount, Is.EqualTo(1));
    }

    [Test]
    public void TickInterval_PromotesAggregatedPayloads()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var bits = new AggregationBits(new[] { true, true, false, false });
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var signed = new SignedAggregatedAttestation(data, proof);

        store.OnGossipAggregatedAttestation(signed);
        Assert.That(store.PendingAggregatedPayloadCount, Is.EqualTo(1));

        store.TickInterval(1, IntervalsPerSlot - 1);

        Assert.That(store.PendingAggregatedPayloadCount, Is.EqualTo(0));
    }

    [Test]
    public void ExtractAttestationsForBlock_ReturnsKnownPayloads()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var bits = new AggregationBits(new[] { true, false, true, false });
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var signed = new SignedAggregatedAttestation(data, proof);

        store.OnGossipAggregatedAttestation(signed);
        store.TickInterval(1, IntervalsPerSlot - 1);

        var attestations = store.ExtractAttestationsForBlock();

        Assert.That(attestations, Has.Count.EqualTo(1));
        Assert.That(attestations[0].Data, Is.EqualTo(data));
        Assert.That(attestations[0].AggregationBits, Is.EqualTo(bits));
    }

    [Test]
    public void ExtractAttestationsForBlock_EmptyWhenNothingPromoted()
    {
        var store = CreateStore();

        var attestations = store.ExtractAttestationsForBlock();

        Assert.That(attestations, Is.Empty);
    }

    private static ProtoArrayForkChoiceStore CreateStore()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 4 };
        return new ProtoArrayForkChoiceStore(new FakeStateTransition(), config);
    }

    private static AttestationData MakeAttestationData(ProtoArrayForkChoiceStore store)
    {
        return new AttestationData(
            new Slot(0),
            new Checkpoint(store.HeadRoot, new Slot(0)),
            new Checkpoint(store.HeadRoot, new Slot(0)),
            new Checkpoint(store.HeadRoot, new Slot(0)));
    }

    private sealed class FakeStateTransition : IForkChoiceStateTransition
    {
        public bool TryTransition(ForkChoiceNodeState parentState,
            SignedBlockWithAttestation signedBlock,
            out ForkChoiceNodeState postState, out string reason)
        {
            postState = parentState;
            reason = string.Empty;
            return true;
        }
    }
}
