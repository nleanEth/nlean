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
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        var signedBlock = CreateSignedBlock(1);
        var blockPayload = SszEncoding.Encode(signedBlock);
        var expectedRoot = signedBlock.Message.HashTreeRoot();

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
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        var signedBlock = CreateSignedBlock(7);
        var blockPayload = SszEncoding.Encode(signedBlock);

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, blockPayload);
        await service.StopAsync(CancellationToken.None);

        Assert.That(stateStore.TryLoad(out var persisted), Is.True);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.HeadSlot, Is.EqualTo(7));
        Assert.That(persisted.HeadRoot, Is.EqualTo(signedBlock.Message.HashTreeRoot()));
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
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, new byte[] { 0x01, 0x02, 0x03 });
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    private static SignedBlockWithAttestation CreateSignedBlock(ulong blockSlot)
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
            new Bytes32(Enumerable.Repeat((byte)4, 32).ToArray()),
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
