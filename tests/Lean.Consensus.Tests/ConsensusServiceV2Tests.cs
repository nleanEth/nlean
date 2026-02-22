using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;
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
    public void TryApplyLocalBlock_AcceptsValidBlock()
    {
        var (svc, _, store, _) = CreateService();
        var parentRoot = store.HeadRoot;
        var block = CreateBlock(slot: 1, parentRoot: parentRoot, proposerIndex: 0);
        var signed = WrapBlock(block);

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
        var attestation = CreateAttestation(0, 1, store.HeadRoot);

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

    private static (ConsensusServiceV2 svc, FakeTimeSource time,
        ProtoArrayForkChoiceStore store, SlotClock clock) CreateService()
    {
        var config = new ConsensusConfig
        {
            InitialValidatorCount = 1,
            SecondsPerSlot = 4,
            GenesisTimeUnix = (ulong)GenesisTime.ToUnixTimeSeconds()
        };
        var stateTransition = new ForkChoiceStateTransition(config);
        var store = new ProtoArrayForkChoiceStore(stateTransition, config);
        var time = new FakeTimeSource(GenesisTime);
        var clock = new SlotClock(config.GenesisTimeUnix, config.SecondsPerSlot,
            ProtoArrayForkChoiceStore.IntervalsPerSlot, time);
        var svc = new ConsensusServiceV2(store, clock, config);
        return (svc, time, store, clock);
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

    private sealed class FakeTimeSource : ITimeSource
    {
        public FakeTimeSource(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; set; }
    }
}
