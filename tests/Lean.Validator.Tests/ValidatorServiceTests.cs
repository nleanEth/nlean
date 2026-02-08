using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Crypto;
using Lean.Network;
using Lean.Validator;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Lean.Validator.Tests;

public sealed class ValidatorServiceTests
{
    [Test]
    public async Task StartAsync_InitializesLeanMultiSigContexts()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(1));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task StartAsync_IsIdempotent_WhileRunning()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(1));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task DutyLoop_TicksUntilStop()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);

        var observedDutyTick = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > 0,
            TimeSpan.FromSeconds(3));
        Assert.That(observedDutyTick, Is.True);

        await service.StopAsync(CancellationToken.None);
        var readCallsAtStop = consensus.CurrentSlotReadCalls;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.That(consensus.CurrentSlotReadCalls, Is.EqualTo(readCallsAtStop));
    }

    [Test]
    public async Task DutyLoop_PublishesAttestationAndAggregate()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Attestations),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.Aggregates), Is.True);
    }

    [Test]
    public async Task DutyLoop_PublishedAttestationIsConsensusDecodable()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Attestations),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        var payload = network.PublishedMessages.First(message => message.Topic == GossipTopics.Attestations).Payload;
        var decodeResult = new SignedAttestationGossipDecoder().DecodeAndValidate(payload);
        Assert.That(decodeResult.IsSuccess, Is.True);
        Assert.That(decodeResult.Attestation, Is.Not.Null);
    }

    [Test]
    public async Task DutyLoop_SecretOnlyConfiguration_DisablesAggregatePublishing()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig
            {
                SecretKeyHex = "0x" + new string('A', 64),
                PublishAggregates = true
            },
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Attestations),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.Aggregates), Is.False);
        Assert.That(multiSig.AggregateCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task StopAsync_AllowsStartToInitializeAgain()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        var firstLifecycleTicked = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > 0,
            TimeSpan.FromSeconds(3));
        Assert.That(firstLifecycleTicked, Is.True);

        await service.StopAsync(CancellationToken.None);
        var readCallsAfterFirstLifecycle = consensus.CurrentSlotReadCalls;
        await service.StartAsync(CancellationToken.None);
        var secondLifecycleTicked = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > readCallsAfterFirstLifecycle,
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(secondLifecycleTicked, Is.True);
        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(2));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task StopAsync_IsIdempotent()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StopAsync_WithCancelledToken_StillStopsDutyLoop()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var observedDutyTick = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > 0,
            TimeSpan.FromSeconds(3));
        Assert.That(observedDutyTick, Is.True);

        using var cancelledStopToken = new CancellationTokenSource();
        cancelledStopToken.Cancel();
        await service.StopAsync(cancelledStopToken.Token);

        var readCallsAtStop = consensus.CurrentSlotReadCalls;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.That(consensus.CurrentSlotReadCalls, Is.EqualTo(readCallsAtStop));
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

    private sealed class FakeLeanSig : ILeanSig
    {
        public LeanSigKeyPair GenerateKeyPair(uint activationEpoch, uint numActiveEpochs)
        {
            return new LeanSigKeyPair(
                Enumerable.Repeat((byte)0x11, 64).ToArray(),
                Enumerable.Repeat((byte)0x22, 128).ToArray());
        }

        public byte[] Sign(ReadOnlySpan<byte> secretKey, uint epoch, ReadOnlySpan<byte> message)
        {
            return Enumerable.Repeat((byte)0x33, XmssSignature.Length).ToArray();
        }

        public bool Verify(ReadOnlySpan<byte> publicKey, uint epoch, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
        {
            return true;
        }
    }

    private sealed class FakeConsensusService : IConsensusService
    {
        private long _currentSlotReadCalls;

        public long CurrentSlotReadCalls => Interlocked.Read(ref _currentSlotReadCalls);

        public ulong CurrentSlot
        {
            get
            {
                Interlocked.Increment(ref _currentSlotReadCalls);
                return 1;
            }
        }

        public ulong HeadSlot => 1;

        public byte[] HeadRoot => Enumerable.Repeat((byte)0x44, 32).ToArray();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLeanMultiSig : ILeanMultiSig
    {
        public int SetupProverCalls { get; private set; }
        public int SetupVerifierCalls { get; private set; }
        public int AggregateCalls { get; private set; }

        public void SetupProver()
        {
            SetupProverCalls++;
        }

        public void SetupVerifier()
        {
            SetupVerifierCalls++;
        }

        public byte[] AggregateSignatures(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
            IReadOnlyList<ReadOnlyMemory<byte>> signatures,
            ReadOnlySpan<byte> message,
            uint epoch)
        {
            AggregateCalls++;
            return new byte[] { 0xAA, 0xBB, 0xCC };
        }

        public bool VerifyAggregate(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
            ReadOnlySpan<byte> message,
            ReadOnlySpan<byte> aggregateSignature,
            uint epoch)
        {
            return true;
        }
    }

    private sealed class FakeNetworkService : INetworkService
    {
        public List<(string Topic, byte[] Payload)> PublishedMessages { get; } = new();

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
            PublishedMessages.Add((topic, payload.ToArray()));
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<byte[]?>(null);
        }
    }
}
