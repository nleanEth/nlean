using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Network;
using Lean.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

[TestFixture]
public class ConsensusServiceTests
{
    [Test]
    public async Task StartAsync_SubscribesToExpectedTopics()
    {
        var network = new FakeNetworkService();
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(
            network.SubscribedTopics,
            Is.EquivalentTo(new[] { GossipTopics.Blocks, GossipTopics.Attestations, GossipTopics.Aggregates }));
    }

    [Test]
    public async Task StartAsync_IsIdempotent()
    {
        var network = new FakeNetworkService();
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(network.SubscribeCalls, Is.EqualTo(3));
    }

    [Test]
    public async Task SlotTicker_AdvancesCurrentSlot()
    {
        var network = new FakeNetworkService();
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false });

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2.2));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.CurrentSlot, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task BlockGossip_UpdatesHeadSlotAndRoot()
    {
        var network = new FakeNetworkService();
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true });

        var signedBlock = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockPayload = SszEncoding.Encode(signedBlock);
        var expectedRoot = signedBlock.Message.Block.HashTreeRoot();

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, blockPayload);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(1));
        Assert.That(service.HeadRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public async Task StartAsync_LoadsPersistedHeadState()
    {
        var persistedHead = new ConsensusHeadState(99, Enumerable.Repeat((byte)0xAB, 32).ToArray());
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        stateStore.Save(persistedHead);

        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(99));
        Assert.That(service.HeadRoot, Is.EqualTo(persistedHead.HeadRoot));
    }

    [Test]
    public async Task BlockGossip_PersistsHeadState()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true });

        var signedBlock = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockPayload = SszEncoding.Encode(signedBlock);

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, blockPayload);
        await service.StopAsync(CancellationToken.None);

        Assert.That(stateStore.TryLoad(out var persisted), Is.True);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.HeadSlot, Is.EqualTo(1));
        Assert.That(persisted.HeadRoot, Is.EqualTo(signedBlock.Message.Block.HashTreeRoot()));
    }

    [Test]
    public async Task InvalidBlockGossip_DoesNotAdvanceHead()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, new byte[] { 0x01, 0x02, 0x03 });
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    [Test]
    public async Task BlockGossip_UnknownParent_DoesNotAdvanceHead()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        var unknownParent = new Bytes32(Enumerable.Repeat((byte)0x77, 32).ToArray());
        var signedBlock = CreateSignedBlock(1, unknownParent, 1, Bytes32.Zero(), 0);

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(signedBlock));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    [Test]
    public async Task BlockGossip_ChainExtension_UpdatesHeadToHighestSlot()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true });

        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(2, blockOneRoot, 1, Bytes32.Zero(), 0);
        var blockTwoRoot = blockTwo.Message.Block.HashTreeRoot();

        await service.StartAsync(CancellationToken.None);
        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 1, TimeSpan.FromSeconds(3));
        Assert.That(advanced, Is.True);
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockOne));
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockTwo));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(2));
        Assert.That(service.HeadRoot, Is.EqualTo(blockTwoRoot));
    }

    [Test]
    public async Task BlockGossip_FutureSlot_DoesNotAdvanceHead()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        var futureBlock = CreateSignedBlock(3, Bytes32.Zero(), 0, Bytes32.Zero(), 0);

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(futureBlock));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    private static SignedBlockWithAttestation CreateSignedBlock(
        ulong blockSlot,
        Bytes32 parentRoot,
        ulong parentSlot,
        Bytes32 sourceRoot,
        ulong sourceSlot)
    {
        var proposerAttestationData = new AttestationData(
            new Slot(blockSlot),
            new Checkpoint(parentRoot, new Slot(parentSlot)),
            new Checkpoint(parentRoot, new Slot(parentSlot)),
            new Checkpoint(sourceRoot, new Slot(sourceSlot)));

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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - startedAt > timeout)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return true;
    }

    private sealed class FakeNetworkService : INetworkService
    {
        private readonly Dictionary<string, List<Action<byte[]>>> _subscriptions = new();
        private readonly object _lock = new();

        public int SubscribeCalls { get; private set; }
        public List<string> SubscribedTopics { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            PublishToTopic(topic, payload.ToArray());
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                SubscribeCalls++;
                SubscribedTopics.Add(topic);
                if (!_subscriptions.TryGetValue(topic, out var handlers))
                {
                    handlers = new List<Action<byte[]>>();
                    _subscriptions[topic] = handlers;
                }

                handlers.Add(handler);
            }

            return Task.CompletedTask;
        }

        public void PublishToTopic(string topic, byte[] payload)
        {
            List<Action<byte[]>> handlers;
            lock (_lock)
            {
                if (!_subscriptions.TryGetValue(topic, out var existing))
                {
                    return;
                }

                handlers = existing.ToList();
            }

            foreach (var handler in handlers)
            {
                handler(payload);
            }
        }
    }
}
