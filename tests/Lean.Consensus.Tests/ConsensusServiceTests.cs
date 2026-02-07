using System.Security.Cryptography;
using Lean.Consensus;
using Lean.Network;
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
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
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
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
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
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
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
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        var blockPayload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAA };
        var expectedRoot = SHA256.HashData(blockPayload);

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, blockPayload);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(1));
        Assert.That(service.HeadRoot, Is.EqualTo(expectedRoot));
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
