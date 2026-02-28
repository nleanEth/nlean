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
        var sig = XmssSignature.Empty();

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
    public void OnBlock_WritesBlockBodyProofsIntoKnownPayloads()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var bits = new AggregationBits(new[] { true, false, true, false });
        var aggregated = new AggregatedAttestation(bits, data);
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var block = new Block(
            new Slot(1),
            0,
            store.HeadRoot,
            Bytes32.Zero(),
            new BlockBody(new[] { aggregated }));
        var proposerAttestation = new Attestation(0, data);
        var signed = new SignedBlockWithAttestation(
            new BlockWithAttestation(block, proposerAttestation),
            new BlockSignatures(new[] { proof }, XmssSignature.Empty()));

        var result = ApplyBlock(store, signed);

        Assert.That(result.Accepted, Is.True);
        var pool = store.GetKnownPayloadPool();
        var dataRootKey = Convert.ToHexString(data.HashTreeRoot());
        Assert.That(pool.ContainsKey(dataRootKey), Is.True);
        Assert.That(pool[dataRootKey], Has.Count.EqualTo(1));
        Assert.That(pool[dataRootKey][0], Is.EqualTo(proof));
    }

    [Test]
    public void OnBlock_StoresProposerSignatureForFutureAggregation()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var block = new Block(
            new Slot(1),
            0,
            store.HeadRoot,
            Bytes32.Zero(),
            new BlockBody(Array.Empty<AggregatedAttestation>()));
        var proposerAttestation = new Attestation(0, data);
        var proposerSignature = XmssSignature.Empty();
        var signed = new SignedBlockWithAttestation(
            new BlockWithAttestation(block, proposerAttestation),
            new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), proposerSignature));

        var result = ApplyBlock(store, signed);

        Assert.That(result.Accepted, Is.True);
        var groups = store.CollectAttestationsForAggregation();
        Assert.That(groups, Has.Count.EqualTo(1));
        Assert.That(groups[0].Data, Is.EqualTo(data));
        Assert.That(groups[0].ValidatorIds, Is.EqualTo(new[] { 0UL }));
        Assert.That(groups[0].Signatures, Has.Count.EqualTo(1));
        Assert.That(groups[0].Signatures[0], Is.EqualTo(proposerSignature));
    }

    [Test]
    public void ExtractAllAttestationsFromKnownPayloads_ReturnsPerValidatorAttestations()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var bits = new AggregationBits(new[] { true, false, true, false });
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var signed = new SignedAggregatedAttestation(data, proof);

        store.OnGossipAggregatedAttestation(signed);
        store.TickInterval(1, IntervalsPerSlot - 1);

        var attestations = store.ExtractAllAttestationsFromKnownPayloads(blockSlot: 2);

        Assert.That(attestations, Has.Count.EqualTo(2));
        var validatorIds = attestations.Select(a => (ulong)a.ValidatorId).OrderBy(id => id).ToList();
        Assert.That(validatorIds, Is.EqualTo(new[] { 0UL, 2UL }));
        Assert.That(attestations.All(a => a.Data == data), Is.True);
    }

    [Test]
    public void ExtractAllAttestationsFromKnownPayloads_DeduplicatesPerValidator_KeepsLatest()
    {
        var store = CreateStore();
        var earlyData = MakeAttestationData(store);
        var laterData = earlyData with { Slot = new Slot(0) }; // Same slot, same source/head/target

        // Two proofs both covering validator 0
        var bits1 = new AggregationBits(new[] { true, false, false, false });
        var proof1 = new AggregatedSignatureProof(bits1, new byte[32]);
        var bits2 = new AggregationBits(new[] { true, true, false, false });
        var proof2 = new AggregatedSignatureProof(bits2, new byte[64]);

        store.OnGossipAggregatedAttestation(new SignedAggregatedAttestation(earlyData, proof1));
        store.OnGossipAggregatedAttestation(new SignedAggregatedAttestation(laterData, proof2));
        store.TickInterval(1, IntervalsPerSlot - 1);

        var attestations = store.ExtractAllAttestationsFromKnownPayloads(blockSlot: 2);

        // Validator 0 and 1 should both appear (deduplicated)
        var validatorIds = attestations.Select(a => (ulong)a.ValidatorId).OrderBy(id => id).ToList();
        Assert.That(validatorIds, Is.EqualTo(new[] { 0UL, 1UL }));
    }

    [Test]
    public void GetKnownPayloadPool_ReturnsPoolForGreedyProofSelection()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var bits = new AggregationBits(new[] { true, false, true, false });
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var signed = new SignedAggregatedAttestation(data, proof);

        store.OnGossipAggregatedAttestation(signed);
        store.TickInterval(1, IntervalsPerSlot - 1);

        var pool = store.GetKnownPayloadPool();
        var dataRootKey = Convert.ToHexString(data.HashTreeRoot());

        Assert.That(pool.ContainsKey(dataRootKey), Is.True);
        Assert.That(pool[dataRootKey], Has.Count.EqualTo(1));
        Assert.That(pool[dataRootKey][0], Is.EqualTo(proof));
    }

    [Test]
    public void CollectAttestationsForAggregation_SortsValidatorIdAscending()
    {
        var store = CreateStore();
        var data = MakeAttestationData(store);
        var dataRoot = new Bytes32(data.HashTreeRoot());

        // Register attestation data through the store so CollectAttestationsForAggregation can find it
        var att = new SignedAttestation(0, data, XmssSignature.Empty());
        store.TryOnAttestation(att, storeSignature: false, out _);

        // Insert signatures in descending order to test sorting
        store.OnGossipSignature(3, dataRoot, XmssSignature.Empty());
        store.OnGossipSignature(1, dataRoot, XmssSignature.Empty());
        store.OnGossipSignature(0, dataRoot, XmssSignature.Empty());
        store.OnGossipSignature(2, dataRoot, XmssSignature.Empty());

        var groups = store.CollectAttestationsForAggregation();

        Assert.That(groups, Has.Count.EqualTo(1));
        Assert.That(groups[0].ValidatorIds, Is.EqualTo(new[] { 0UL, 1UL, 2UL, 3UL }));
    }

    [Test]
    public void OnBlock_EmbeddedAttestationVotesAffectForkChoiceHead()
    {
        var store = CreateStore();
        var genesisRoot = store.HeadRoot;
        var genesisData = MakeAttestationData(store);

        // Block A at slot 1 (child of genesis, proposer 0)
        var blockA = new Block(new Slot(1), 0, genesisRoot, Bytes32.Zero(),
            new BlockBody(Array.Empty<AggregatedAttestation>()));
        var signedA = new SignedBlockWithAttestation(
            new BlockWithAttestation(blockA, new Attestation(0, genesisData)),
            new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), XmssSignature.Empty()));
        Assert.That(ApplyBlock(store, signedA).Accepted, Is.True);
        var rootA = new Bytes32(blockA.HashTreeRoot());

        // Block D at slot 1 (fork, also child of genesis, different proposer for different root)
        var blockD = new Block(new Slot(1), 1, genesisRoot, Bytes32.Zero(),
            new BlockBody(Array.Empty<AggregatedAttestation>()));
        var signedD = new SignedBlockWithAttestation(
            new BlockWithAttestation(blockD, new Attestation(1, genesisData)),
            new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), XmssSignature.Empty()));
        Assert.That(ApplyBlock(store, signedD).Accepted, Is.True);
        var rootD = new Bytes32(blockD.HashTreeRoot());

        // Gossip attestation: validator 3 votes for D (head=D)
        var attForD = new AttestationData(new Slot(1),
            new Checkpoint(rootD, new Slot(1)),
            new Checkpoint(genesisRoot, new Slot(0)),
            new Checkpoint(genesisRoot, new Slot(0)));
        store.TryOnAttestation(new SignedAttestation(3, attForD, XmssSignature.Empty()),
            storeSignature: false, out _);

        // Block B at slot 2 (child of A), with attestations from validators [0,1,2] voting for A
        var attForA = new AttestationData(new Slot(1),
            new Checkpoint(rootA, new Slot(1)),
            new Checkpoint(genesisRoot, new Slot(0)),
            new Checkpoint(genesisRoot, new Slot(0)));
        var bits = new AggregationBits(new[] { true, true, true, false });
        var aggregated = new AggregatedAttestation(bits, attForA);
        var proof = new AggregatedSignatureProof(bits, new byte[32]);
        var blockB = new Block(new Slot(2), 2, rootA, Bytes32.Zero(),
            new BlockBody(new[] { aggregated }));
        var signedB = new SignedBlockWithAttestation(
            new BlockWithAttestation(blockB, new Attestation(2, attForA)),
            new BlockSignatures(new[] { proof }, XmssSignature.Empty()));
        Assert.That(ApplyBlock(store, signedB).Accepted, Is.True);
        var rootB = new Bytes32(blockB.HashTreeRoot());

        // Promote pending attestations and recompute head
        store.TickInterval(2, IntervalsPerSlot - 1);

        // Block-embedded votes [0,1,2]→A outweigh gossip vote [3]→D
        // Head should follow the heavier chain: genesis → A → B
        Assert.That(store.HeadRoot, Is.EqualTo(rootB));
        Assert.That(store.HeadSlot, Is.EqualTo(2UL));
    }

    private static ProtoArrayForkChoiceStore CreateStore()
    {
        var config = new ConsensusConfig { InitialValidatorCount = 4 };
        return new ProtoArrayForkChoiceStore(config);
    }

    private static ForkChoiceApplyResult ApplyBlock(
        ProtoArrayForkChoiceStore store,
        SignedBlockWithAttestation signed)
    {
        var genesisCheckpoint = new Checkpoint(store.JustifiedRoot, new Slot(store.JustifiedSlot));
        return store.OnBlock(signed, genesisCheckpoint, genesisCheckpoint, 4);
    }

    private static AttestationData MakeAttestationData(ProtoArrayForkChoiceStore store)
    {
        return new AttestationData(
            new Slot(0),
            new Checkpoint(store.HeadRoot, new Slot(0)),
            new Checkpoint(store.HeadRoot, new Slot(0)),
            new Checkpoint(store.HeadRoot, new Slot(0)));
    }
}
