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
    private static readonly Bytes32 CanonicalGenesisRoot = BuildCanonicalGenesisRoot();

    [Test]
    public async Task StartAsync_SubscribesToExpectedTopics()
    {
        var network = new FakeNetworkService();
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(
            network.SubscribedTopics,
            Is.EquivalentTo(new[]
            {
                GossipTopics.Block(GossipTopics.DefaultNetwork),
                GossipTopics.Attestation(GossipTopics.DefaultNetwork),
                GossipTopics.Aggregate(GossipTopics.DefaultNetwork)
            }));
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
            new SignedAttestationGossipDecoder(),
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
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false });

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2.2));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.CurrentSlot, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task StartAsync_ContinuesWhenStatusProbeHangs()
    {
        var network = new FakeNetworkService
        {
            ProbePeerStatusesHandler = _ => new TaskCompletionSource().Task
        };
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false });

        var startedAt = DateTime.UtcNow;
        await service.StartAsync(CancellationToken.None);
        var startupDuration = DateTime.UtcNow - startedAt;
        var slotAdvanced = await WaitUntilAsync(() => service.CurrentSlot >= 1, TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(startupDuration, Is.LessThan(TimeSpan.FromSeconds(7)));
        Assert.That(slotAdvanced, Is.True);
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
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true, InitialValidatorCount = 8 });

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
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(99));
        Assert.That(service.HeadRoot, Is.EqualTo(persistedHead.HeadRoot));
        Assert.That(service.CurrentSlot, Is.EqualTo(99));
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
            new SignedAttestationGossipDecoder(),
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
    public async Task BlockGossip_PersistsCheckpointState()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var config = new ConsensusConfig
        {
            SecondsPerSlot = 1,
            EnableGossipProcessing = true,
            InitialValidatorCount = 8
        };
        var forkChoiceStore = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            forkChoiceStore,
            config);

        var genesisRoot = forkChoiceStore.HeadRoot;
        var blockOne = CreateSignedBlock(1, genesisRoot, 0, genesisRoot, 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(
            2,
            blockOneRoot,
            1,
            genesisRoot,
            0,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: blockOneRoot,
            headSlot: 1,
            aggregationBits: Enumerable.Repeat(true, 8).ToArray());
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
        var blockThree = CreateSignedBlock(
            3,
            blockTwoRoot,
            2,
            blockOneRoot,
            1,
            targetRoot: blockTwoRoot,
            targetSlot: 2,
            headRoot: blockTwoRoot,
            headSlot: 2,
            aggregationBits: Enumerable.Repeat(true, 8).ToArray());
        var blockThreeRoot = new Bytes32(blockThree.Message.Block.HashTreeRoot());

        await service.StartAsync(CancellationToken.None);
        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 3, TimeSpan.FromSeconds(5));
        Assert.That(advanced, Is.True);
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockOne));
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockTwo));
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockThree));
        await service.StopAsync(CancellationToken.None);

        Assert.That(stateStore.TryLoad(out var persisted), Is.True);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.HeadSlot, Is.EqualTo(3));
        Assert.That(persisted.HeadRoot, Is.EqualTo(blockThreeRoot.AsSpan().ToArray()));
        Assert.That(persisted.LatestJustifiedSlot, Is.EqualTo(2));
        Assert.That(persisted.LatestJustifiedRoot, Is.EqualTo(blockTwoRoot.AsSpan().ToArray()));
        Assert.That(persisted.LatestFinalizedSlot, Is.EqualTo(1));
        Assert.That(persisted.LatestFinalizedRoot, Is.EqualTo(blockOneRoot.AsSpan().ToArray()));
        Assert.That(persisted.SafeTargetRoot.Length, Is.EqualTo(32));
        Assert.That(persisted.SafeTargetSlot, Is.LessThanOrEqualTo(persisted.HeadSlot));
    }

    [Test]
    public async Task BlockGossip_PersistsBlockPayloadByRoot()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var blockStore = new BlockByRootStore(keyValueStore);
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true },
            blockStore);

        var block = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockRoot = new Bytes32(block.Message.Block.HashTreeRoot());
        var payload = SszEncoding.Encode(block);

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, payload);
        await service.StopAsync(CancellationToken.None);

        Assert.That(blockStore.TryLoad(blockRoot, out var storedPayload), Is.True);
        Assert.That(storedPayload, Is.Not.Null);
        Assert.That(storedPayload!, Is.EqualTo(payload));
    }

    [Test]
    public async Task StatusRpcPeerAnchor_AdoptsPeerGenesisHeadWhenLocalHeadIsZero()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false },
            statusRpcRouter: statusRouter);
        var remoteRoot = Enumerable.Repeat((byte)0xA5, 32).ToArray();

        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: remoteRoot,
                finalizedSlot: 0,
                headRoot: remoteRoot,
                headSlot: 0),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(service.HeadRoot, Is.EqualTo(CanonicalGenesisRoot.AsSpan().ToArray()));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_GossipChainFromGenesis_DoesNotRequireBlocksByRoot()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var config = new ConsensusConfig
        {
            SecondsPerSlot = 1,
            EnableGossipProcessing = true,
            InitialValidatorCount = 4
        };
        var forkChoiceStore = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            forkChoiceStore,
            config,
            statusRpcRouter: statusRouter);

        var genesisRoot = forkChoiceStore.HeadRoot;

        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: genesisRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: genesisRoot.AsSpan(),
                headSlot: 0),
            CancellationToken.None);

        var blockOne = CreateSignedBlock(1, genesisRoot, 0, genesisRoot, 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(2, blockOneRoot, 1, genesisRoot, 0, targetRoot: blockOneRoot, targetSlot: 1, headRoot: blockOneRoot, headSlot: 1);

        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 2, TimeSpan.FromSeconds(5));
        Assert.That(advanced, Is.True);

        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockOne));
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockTwo));

        var synced = await WaitUntilAsync(() => service.HeadSlot == 2, TimeSpan.FromSeconds(5));
        Assert.That(synced, Is.True);
        Assert.That(service.HeadRoot, Is.EqualTo(blockTwo.Message.Block.HashTreeRoot()));
        Assert.That(network.BlockByRootRequestCount, Is.EqualTo(0));

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_DoesNotOverrideExistingHead()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true },
            statusRpcRouter: statusRouter);
        var block = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockRoot = block.Message.Block.HashTreeRoot();
        var remoteRoot = Enumerable.Repeat((byte)0xC3, 32).ToArray();

        await service.StartAsync(CancellationToken.None);
        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 1, TimeSpan.FromSeconds(3));
        Assert.That(advanced, Is.True);
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(block));

        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: remoteRoot,
                finalizedSlot: 5,
                headRoot: remoteRoot,
                headSlot: 5),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(1));
        Assert.That(service.HeadRoot, Is.EqualTo(blockRoot));
    }

    [Test]
    public async Task StatusRpcPeerAnchor_AdoptsPeerFinalizedCheckpointWhenLocalHeadSlotIsZero()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false },
            statusRpcRouter: statusRouter);

        var remoteFinalizedRoot = Enumerable.Repeat((byte)0xCC, 32).ToArray();
        var remoteHeadRoot = Enumerable.Repeat((byte)0xDD, 32).ToArray();
        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: remoteFinalizedRoot,
                finalizedSlot: 2,
                headRoot: remoteHeadRoot,
                headSlot: 5),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(service.HeadRoot, Is.EqualTo(CanonicalGenesisRoot.AsSpan().ToArray()));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_DoesNotAdoptUnfinalizedPeerHeadWhenFinalizedUnavailable()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false },
            statusRpcRouter: statusRouter);

        var remoteHeadRoot = Enumerable.Repeat((byte)0xDD, 32).ToArray();
        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: new byte[32],
                finalizedSlot: 0,
                headRoot: remoteHeadRoot,
                headSlot: 5),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(service.HeadRoot, Is.EqualTo(CanonicalGenesisRoot.AsSpan().ToArray()));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_PrefersSlotZeroFinalizedRootOverUnfinalizedHead()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false },
            statusRpcRouter: statusRouter);

        var remoteFinalizedRoot = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        var remoteHeadRoot = Enumerable.Repeat((byte)0xCD, 32).ToArray();
        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: remoteFinalizedRoot,
                finalizedSlot: 0,
                headRoot: remoteHeadRoot,
                headSlot: 5),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(service.HeadRoot, Is.EqualTo(CanonicalGenesisRoot.AsSpan().ToArray()));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_TriggersBlocksByRootSyncForRemoteHead()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false },
            statusRpcRouter: statusRouter);

        var remoteFinalizedRoot = CanonicalGenesisRoot;
        var remoteHeadBlock = CreateSignedBlock(1, remoteFinalizedRoot, 0, remoteFinalizedRoot, 0);
        var remoteHeadRoot = new Bytes32(remoteHeadBlock.Message.Block.HashTreeRoot());
        network.SetBlockByRootResponse(remoteHeadRoot, SszEncoding.Encode(remoteHeadBlock));

        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: remoteFinalizedRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: remoteHeadRoot.AsSpan(),
                headSlot: 1),
            CancellationToken.None);

        var advanced = await WaitUntilAsync(() => service.HeadSlot == 1, TimeSpan.FromSeconds(3));
        Assert.That(advanced, Is.True);
        await service.StopAsync(CancellationToken.None);

        Assert.That(network.BlockByRootRequestCount, Is.GreaterThan(0));
        Assert.That(service.HeadRoot, Is.EqualTo(remoteHeadRoot.AsSpan().ToArray()));
    }

    [Test]
    public async Task StatusRpcPeerAnchor_TriggersBlocksByRootSyncWithoutAnchorAdoption()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true },
            statusRpcRouter: statusRouter);

        var localBlock = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var localRoot = new Bytes32(localBlock.Message.Block.HashTreeRoot());
        var remoteHeadBlock = CreateSignedBlock(2, localRoot, 1, localRoot, 1);
        var remoteHeadRoot = new Bytes32(remoteHeadBlock.Message.Block.HashTreeRoot());
        network.SetBlockByRootResponse(remoteHeadRoot, SszEncoding.Encode(remoteHeadBlock));

        await service.StartAsync(CancellationToken.None);
        var advancedToOne = await WaitUntilAsync(() => service.CurrentSlot >= 1, TimeSpan.FromSeconds(3));
        Assert.That(advancedToOne, Is.True);
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(localBlock));
        var localApplied = await WaitUntilAsync(() => service.HeadSlot == 1, TimeSpan.FromSeconds(3));
        Assert.That(localApplied, Is.True);

        var advancedToTwo = await WaitUntilAsync(() => service.CurrentSlot >= 2, TimeSpan.FromSeconds(3));
        Assert.That(advancedToTwo, Is.True);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: localRoot.AsSpan(),
                finalizedSlot: 1,
                headRoot: remoteHeadRoot.AsSpan(),
                headSlot: 2),
            CancellationToken.None);

        var synced = await WaitUntilAsync(() => service.HeadSlot == 2, TimeSpan.FromSeconds(3));
        Assert.That(synced, Is.True);
        await service.StopAsync(CancellationToken.None);

        Assert.That(network.BlockByRootRequestCount, Is.GreaterThan(0));
        Assert.That(service.HeadRoot, Is.EqualTo(remoteHeadRoot.AsSpan().ToArray()));
    }

    [Test]
    public async Task StatusRpcPeerAnchor_DoesNotRetriggerBlocksByRootSyncForSmallHeadAdvances()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false },
            statusRpcRouter: statusRouter);

        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(2, blockOneRoot, 1, blockOneRoot, 1);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
        network.SetBlockByRootResponse(blockOneRoot, SszEncoding.Encode(blockOne));
        network.SetBlockByRootResponse(blockTwoRoot, SszEncoding.Encode(blockTwo));

        await service.StartAsync(CancellationToken.None);

        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: blockOne.Message.Block.ParentRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: blockOneRoot.AsSpan(),
                headSlot: 1),
            CancellationToken.None);
        var firstSynced = await WaitUntilAsync(() => service.HeadSlot == 1, TimeSpan.FromSeconds(3));
        Assert.That(firstSynced, Is.True);
        var requestCountAfterFirstSync = network.BlockByRootRequestCount;

        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: blockOneRoot.AsSpan(),
                finalizedSlot: 1,
                headRoot: blockTwoRoot.AsSpan(),
                headSlot: 2),
            CancellationToken.None);
        await Task.Delay(250);

        Assert.That(network.BlockByRootRequestCount, Is.EqualTo(requestCountAfterFirstSync));
        Assert.That(service.HeadSlot, Is.EqualTo(1));
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_RetriggersBlocksByRootSyncForMajorHeadAdvance()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false, SlotsPerEpoch = 2 },
            statusRpcRouter: statusRouter);

        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockThree = CreateSignedBlock(3, blockOneRoot, 1, blockOneRoot, 1);
        var blockThreeRoot = new Bytes32(blockThree.Message.Block.HashTreeRoot());
        network.SetBlockByRootResponse(blockOneRoot, SszEncoding.Encode(blockOne));
        network.SetBlockByRootResponse(blockThreeRoot, SszEncoding.Encode(blockThree));

        await service.StartAsync(CancellationToken.None);

        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: blockOne.Message.Block.ParentRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: blockOneRoot.AsSpan(),
                headSlot: 1),
            CancellationToken.None);
        var firstSynced = await WaitUntilAsync(() => service.HeadSlot == 1, TimeSpan.FromSeconds(3));
        Assert.That(firstSynced, Is.True);
        var requestCountAfterFirstSync = network.BlockByRootRequestCount;

        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: blockOneRoot.AsSpan(),
                finalizedSlot: 1,
                headRoot: blockThreeRoot.AsSpan(),
                headSlot: 3),
            CancellationToken.None);
        var retriggered = await WaitUntilAsync(
            () => network.BlockByRootRequestCount > requestCountAfterFirstSync,
            TimeSpan.FromSeconds(3));

        Assert.That(retriggered, Is.True);
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_RetriesMissingTargetWithoutMajorHeadAdvance()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, SlotsPerEpoch = 32 },
            statusRpcRouter: statusRouter);

        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());

        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: blockOne.Message.Block.ParentRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: blockOneRoot.AsSpan(),
                headSlot: 1),
            CancellationToken.None);
        await Task.Delay(250);

        var firstRequestCount = network.BlockByRootRequestCount;
        Assert.That(firstRequestCount, Is.GreaterThan(0));
        Assert.That(service.HeadSlot, Is.EqualTo(0));

        network.SetBlockByRootResponse(blockOneRoot, SszEncoding.Encode(blockOne));
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: blockOne.Message.Block.ParentRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: blockOneRoot.AsSpan(),
                headSlot: 1),
            CancellationToken.None);

        var recovered = await WaitUntilAsync(() => service.HeadSlot == 1, TimeSpan.FromSeconds(3));
        Assert.That(recovered, Is.True);
        Assert.That(network.BlockByRootRequestCount, Is.GreaterThan(firstRequestCount));
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_UsesStatusPeerAsBlocksByRootPreference()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, SlotsPerEpoch = 32 },
            statusRpcRouter: statusRouter);

        var peerA = "/ip4/127.0.0.1/udp/9000/quic-v1/p2p/12D3KooWPeerA";
        var remoteHeadBlock = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var remoteHeadRoot = new Bytes32(remoteHeadBlock.Message.Block.HashTreeRoot());
        network.SetBlockByRootResponseForPeer(remoteHeadRoot, peerA, SszEncoding.Encode(remoteHeadBlock));

        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: remoteHeadBlock.Message.Block.ParentRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: remoteHeadRoot.AsSpan(),
                headSlot: 1),
            peerA,
            CancellationToken.None);

        var advanced = await WaitUntilAsync(() => service.HeadSlot == 1, TimeSpan.FromSeconds(3));
        Assert.That(advanced, Is.True);
        await service.StopAsync(CancellationToken.None);

        Assert.That(network.RequestedPreferredPeers.Count, Is.GreaterThan(0));
        Assert.That(network.RequestedPreferredPeers[0], Is.EqualTo(peerA));
    }

    [Test]
    public async Task StatusRpcPeerAnchor_MissingTargetRetryKeepsOriginalPeerPreference()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, SlotsPerEpoch = 32 },
            statusRpcRouter: statusRouter);

        var peerA = "/ip4/127.0.0.1/udp/9000/quic-v1/p2p/12D3KooWPeerA";
        var peerB = "/ip4/127.0.0.1/udp/9001/quic-v1/p2p/12D3KooWPeerB";
        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());

        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: blockOne.Message.Block.ParentRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: blockOneRoot.AsSpan(),
                headSlot: 1),
            peerA,
            CancellationToken.None);
        await Task.Delay(250);

        var firstRequestCount = network.BlockByRootRequestCount;
        Assert.That(firstRequestCount, Is.GreaterThan(0));
        Assert.That(network.RequestedPreferredPeers.Count, Is.EqualTo(firstRequestCount));
        Assert.That(network.RequestedPreferredPeers.All(key => key == peerA), Is.True);

        network.SetBlockByRootResponseForPeer(blockOneRoot, peerA, SszEncoding.Encode(blockOne));
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: blockOne.Message.Block.ParentRoot.AsSpan(),
                finalizedSlot: 0,
                headRoot: blockOneRoot.AsSpan(),
                headSlot: 1),
            peerB,
            CancellationToken.None);

        var recovered = await WaitUntilAsync(() => service.HeadSlot == 1, TimeSpan.FromSeconds(3));
        Assert.That(recovered, Is.True);
        await service.StopAsync(CancellationToken.None);

        var retryPreferredPeers = network.RequestedPreferredPeers.Skip(firstRequestCount).ToArray();
        Assert.That(retryPreferredPeers.Length, Is.GreaterThan(0));
        Assert.That(retryPreferredPeers.All(key => key == peerA), Is.True);
    }

    [Test]
    public async Task StatusRpcPeerAnchor_DoesNotOverrideExistingGenesisAnchor()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var localRoot = Enumerable.Repeat((byte)0x11, 32).ToArray();
        stateStore.Save(new ConsensusHeadState(0, localRoot));

        var statusRouter = new StatusRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = false },
            statusRpcRouter: statusRouter);
        var remoteRoot = Enumerable.Repeat((byte)0xCC, 32).ToArray();

        await service.StartAsync(CancellationToken.None);
        await statusRouter.HandlePeerStatusAsync(
            new LeanStatusMessage(
                finalizedRoot: remoteRoot,
                finalizedSlot: 0,
                headRoot: remoteRoot,
                headSlot: 0),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(service.HeadRoot, Is.EqualTo(localRoot));
    }

    [Test]
    public async Task StartAsync_BindsBlocksByRootRpcHandler()
    {
        var keyValueStore = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(keyValueStore);
        var blockStore = new BlockByRootStore(keyValueStore);
        var rpcRouter = new BlocksByRootRpcRouter();
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true },
            blockStore,
            rpcRouter);

        var block = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockRoot = new Bytes32(block.Message.Block.HashTreeRoot());
        var payload = SszEncoding.Encode(block);

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, payload);

        var resolved = await rpcRouter.ResolveAsync(blockRoot.AsSpan().ToArray(), CancellationToken.None);
        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!, Is.EqualTo(payload));

        await service.StopAsync(CancellationToken.None);
        var afterStop = await rpcRouter.ResolveAsync(blockRoot.AsSpan().ToArray(), CancellationToken.None);
        Assert.That(afterStop, Is.Null);
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
            new SignedAttestationGossipDecoder(),
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
            new SignedAttestationGossipDecoder(),
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
    public async Task BlockGossip_UnknownParent_RequestsBlocksByRootAndRecovers()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var config = new ConsensusConfig
        {
            SecondsPerSlot = 1,
            EnableGossipProcessing = true,
            MaxOrphanBlocks = 64,
            InitialValidatorCount = 4
        };
        var forkChoiceStore = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            forkChoiceStore,
            config);

        var genesisRoot = forkChoiceStore.HeadRoot;
        var parentBlock = CreateSignedBlock(1, genesisRoot, 0, genesisRoot, 0);
        var parentRoot = new Bytes32(parentBlock.Message.Block.HashTreeRoot());
        var childBlock = CreateSignedBlock(2, parentRoot, 1, genesisRoot, 0);
        var childRoot = childBlock.Message.Block.HashTreeRoot();

        network.SetBlockByRootResponse(parentRoot, SszEncoding.Encode(parentBlock));

        await service.StartAsync(CancellationToken.None);
        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 2, TimeSpan.FromSeconds(4));
        Assert.That(advanced, Is.True);

        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(childBlock));
        var recovered = await WaitUntilAsync(() => service.HeadSlot == 2, TimeSpan.FromSeconds(3));
        Assert.That(recovered, Is.True);
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadRoot, Is.EqualTo(childRoot));
        Assert.That(network.BlockByRootRequestCount, Is.GreaterThan(0));
        Assert.That(stateStore.TryLoad(out var persisted), Is.True);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.HeadSlot, Is.EqualTo(2));
    }

    [Test]
    public async Task BlockGossip_UnknownAttestationHead_RequestsBlocksByRootAndRecovers()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true, MaxOrphanBlocks = 64, InitialValidatorCount = 4 });

        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var unknownHeadBlock = CreateSignedBlock(2, blockOneRoot, 1, blockOneRoot, 1);
        var unknownHeadRoot = new Bytes32(unknownHeadBlock.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(
            2,
            blockOneRoot,
            1,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: blockOneRoot,
            targetSlot: 1,
            headRoot: unknownHeadRoot,
            headSlot: 2);

        network.SetBlockByRootResponse(unknownHeadRoot, SszEncoding.Encode(unknownHeadBlock));

        await service.StartAsync(CancellationToken.None);
        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 2, TimeSpan.FromSeconds(4));
        Assert.That(advanced, Is.True);

        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockOne));
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockTwo));
        var requested = await WaitUntilAsync(() => network.BlockByRootRequestCount > 0, TimeSpan.FromSeconds(3));
        var recovered = await WaitUntilAsync(() => service.HeadSlot == 2, TimeSpan.FromSeconds(3));

        await service.StopAsync(CancellationToken.None);
        Assert.That(requested, Is.True);
        Assert.That(recovered, Is.True);
    }

    [Test]
    public async Task BlockGossip_RecoversQueuedOrphanWhenParentArrives()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var config = new ConsensusConfig
        {
            SecondsPerSlot = 1,
            EnableGossipProcessing = true,
            MaxOrphanBlocks = 64,
            InitialValidatorCount = 4
        };
        var forkChoiceStore = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            forkChoiceStore,
            config);

        var genesisRoot = forkChoiceStore.HeadRoot;
        var parentBlock = CreateSignedBlock(1, genesisRoot, 0, genesisRoot, 0);
        var parentRoot = new Bytes32(parentBlock.Message.Block.HashTreeRoot());
        var orphanChild = CreateSignedBlock(2, parentRoot, 1, genesisRoot, 0);
        var orphanChildRoot = orphanChild.Message.Block.HashTreeRoot();

        await service.StartAsync(CancellationToken.None);
        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 1, TimeSpan.FromSeconds(3));
        Assert.That(advanced, Is.True);

        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(orphanChild));
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(parentBlock));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(2));
        Assert.That(service.HeadRoot, Is.EqualTo(orphanChildRoot));
        Assert.That(stateStore.TryLoad(out var persisted), Is.True);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.HeadSlot, Is.EqualTo(2));
    }

    [Test]
    public async Task BlockGossip_ChainExtension_UpdatesHeadToHighestSlot()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var config = new ConsensusConfig
        {
            SecondsPerSlot = 1,
            EnableGossipProcessing = true,
            InitialValidatorCount = 4
        };
        var forkChoiceStore = new ForkChoiceStore(new ForkChoiceStateTransition(config), config);
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            forkChoiceStore,
            config);

        var genesisRoot = forkChoiceStore.HeadRoot;
        var blockOne = CreateSignedBlock(1, genesisRoot, 0, genesisRoot, 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(2, blockOneRoot, 1, genesisRoot, 0);
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
            new SignedAttestationGossipDecoder(),
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

    [Test]
    public async Task BlockGossip_ProposerMismatch_DoesNotAdvanceHead()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 60, EnableGossipProcessing = true });

        var invalidBlock = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0, proposerAttesterId: 42);

        await service.StartAsync(CancellationToken.None);
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(invalidBlock));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.HeadSlot, Is.EqualTo(0));
        Assert.That(stateStore.TryLoad(out _), Is.False);
    }

    [Test]
    public async Task AttestationGossip_UnknownRoots_RequestsBlocksByRootAndRecovers()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true, MaxOrphanBlocks = 64, InitialValidatorCount = 4 });

        var blockOne = CreateSignedBlock(1, Bytes32.Zero(), 0, Bytes32.Zero(), 0);
        var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
        var blockTwo = CreateSignedBlock(2, blockOneRoot, 1, blockOneRoot, 1);
        var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
        var signedAttestation = CreateSignedAttestation(
            validatorId: 0,
            attestationSlot: 2,
            sourceRoot: blockOneRoot,
            sourceSlot: 1,
            targetRoot: blockTwoRoot,
            targetSlot: 2,
            headRoot: blockTwoRoot,
            headSlot: 2);

        network.SetBlockByRootResponse(blockOneRoot, SszEncoding.Encode(blockOne));
        network.SetBlockByRootResponse(blockTwoRoot, SszEncoding.Encode(blockTwo));

        await service.StartAsync(CancellationToken.None);
        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 2, TimeSpan.FromSeconds(4));
        Assert.That(advanced, Is.True);

        network.PublishToTopic(GossipTopics.Attestations, SszEncoding.Encode(signedAttestation));
        var recovered = await WaitUntilAsync(() => service.HeadSlot == 2, TimeSpan.FromSeconds(3));
        Assert.That(recovered, Is.True);
        await service.StopAsync(CancellationToken.None);

        Assert.That(network.BlockByRootRequestCount, Is.GreaterThan(0));
        Assert.That(stateStore.TryLoad(out var persisted), Is.True);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.HeadSlot, Is.EqualTo(2));
    }

    [Test]
    public async Task AttestationGossip_UnknownRoots_ReplaysAfterRootsArriveFromGossip()
    {
        var stateStore = new ConsensusStateStore(new InMemoryKeyValueStore());
        var network = new FakeNetworkService();
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            new ForkChoiceStore(),
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = true, MaxOrphanBlocks = 64 });
        var blockA = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            proposerAttesterId: 0,
            proposerIndex: 0,
            aggregationBits: new[] { true });
        var blockARoot = new Bytes32(blockA.Message.Block.HashTreeRoot());
        var blockB = CreateSignedBlock(
            blockSlot: 1,
            parentRoot: Bytes32.Zero(),
            parentSlot: 0,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            proposerAttesterId: 0,
            proposerIndex: 0,
            aggregationBits: new[] { true, false });
        var blockBRoot = new Bytes32(blockB.Message.Block.HashTreeRoot());

        var rootAHex = Convert.ToHexString(blockARoot.AsSpan());
        var rootBHex = Convert.ToHexString(blockBRoot.AsSpan());
        var votedRoot = string.CompareOrdinal(rootAHex, rootBHex) < 0 ? blockARoot : blockBRoot;
        var nonVotedRoot = votedRoot.Equals(blockARoot) ? blockBRoot : blockARoot;
        var voteAttestation = CreateSignedAttestation(
            validatorId: 0,
            attestationSlot: 2,
            sourceRoot: Bytes32.Zero(),
            sourceSlot: 0,
            targetRoot: votedRoot,
            targetSlot: 1,
            headRoot: votedRoot,
            headSlot: 1);

        Assert.That(
            string.CompareOrdinal(
                Convert.ToHexString(votedRoot.AsSpan()),
                Convert.ToHexString(nonVotedRoot.AsSpan())),
            Is.LessThan(0));

        await service.StartAsync(CancellationToken.None);
        var advanced = await WaitUntilAsync(() => service.CurrentSlot >= 2, TimeSpan.FromSeconds(4));
        Assert.That(advanced, Is.True);

        network.PublishToTopic(GossipTopics.Attestations, SszEncoding.Encode(voteAttestation));
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockA));
        network.PublishToTopic(GossipTopics.Blocks, SszEncoding.Encode(blockB));

        var replayed = await WaitUntilAsync(
            () => service.CurrentSlot >= 2 &&
                  service.HeadSlot == 1 &&
                  service.HeadRoot.SequenceEqual(votedRoot.AsSpan().ToArray()),
            TimeSpan.FromSeconds(4));

        await service.StopAsync(CancellationToken.None);
        Assert.That(replayed, Is.True);
    }

    private static SignedBlockWithAttestation CreateSignedBlock(
        ulong blockSlot,
        Bytes32 parentRoot,
        ulong parentSlot,
        Bytes32 sourceRoot,
        ulong sourceSlot,
        ulong proposerAttesterId = 7,
        ulong proposerIndex = 7,
        Bytes32? targetRoot = null,
        ulong? targetSlot = null,
        Bytes32? headRoot = null,
        ulong? headSlot = null,
        IReadOnlyList<bool>? aggregationBits = null)
    {
        var normalizedParentRoot = NormalizeRootAtSlot(parentRoot, parentSlot);
        var normalizedSourceRoot = NormalizeRootAtSlot(sourceRoot, sourceSlot);
        var effectiveTargetSlot = targetSlot ?? parentSlot;
        var effectiveTargetRoot = NormalizeRootAtSlot(targetRoot ?? normalizedParentRoot, effectiveTargetSlot);
        var effectiveHeadSlot = headSlot ?? parentSlot;
        var effectiveHeadRoot = NormalizeRootAtSlot(headRoot ?? normalizedParentRoot, effectiveHeadSlot);
        var proposerAttestationData = new AttestationData(
            new Slot(blockSlot),
            new Checkpoint(effectiveHeadRoot, new Slot(effectiveHeadSlot)),
            new Checkpoint(effectiveTargetRoot, new Slot(effectiveTargetSlot)),
            new Checkpoint(normalizedSourceRoot, new Slot(sourceSlot)));

        var effectiveAggregationBits = aggregationBits ?? new[] { true, false, true, true };
        var aggregatedAttestation = new AggregatedAttestation(
            new AggregationBits(effectiveAggregationBits),
            proposerAttestationData);

        var block = new Block(
            new Slot(blockSlot),
            proposerIndex,
            normalizedParentRoot,
            new Bytes32(Enumerable.Repeat((byte)5, 32).ToArray()),
            new BlockBody(new[] { aggregatedAttestation }));

        var blockWithAttestation = new BlockWithAttestation(
            block,
            new Attestation(proposerAttesterId, proposerAttestationData));

        var signatures = new BlockSignatures(
            new[]
            {
                new AggregatedSignatureProof(
                    new AggregationBits(effectiveAggregationBits),
                    new byte[] { 0xAA, 0xBB, 0xCC })
            },
            XmssSignature.Empty());

        return new SignedBlockWithAttestation(blockWithAttestation, signatures);
    }

    private static SignedAttestation CreateSignedAttestation(
        ulong validatorId,
        ulong attestationSlot,
        Bytes32 sourceRoot,
        ulong sourceSlot,
        Bytes32 targetRoot,
        ulong targetSlot,
        Bytes32 headRoot,
        ulong headSlot)
    {
        var normalizedSourceRoot = NormalizeRootAtSlot(sourceRoot, sourceSlot);
        var normalizedTargetRoot = NormalizeRootAtSlot(targetRoot, targetSlot);
        var normalizedHeadRoot = NormalizeRootAtSlot(headRoot, headSlot);
        var data = new AttestationData(
            new Slot(attestationSlot),
            new Checkpoint(normalizedHeadRoot, new Slot(headSlot)),
            new Checkpoint(normalizedTargetRoot, new Slot(targetSlot)),
            new Checkpoint(normalizedSourceRoot, new Slot(sourceSlot)));

        return new SignedAttestation(validatorId, data, XmssSignature.Empty());
    }

    private static Bytes32 NormalizeRootAtSlot(Bytes32 root, ulong slot)
    {
        return slot == 0 && root.Equals(Bytes32.Zero()) ? CanonicalGenesisRoot : root;
    }

    private static Bytes32 BuildCanonicalGenesisRoot()
    {
        return new ForkChoiceStore().HeadRoot;
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
        private readonly Dictionary<string, byte[]> _blockByRoot = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _blockByRootByPreferredPeer = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private int _blockByRootRequestCount;

        public int SubscribeCalls { get; private set; }
        public int BlockByRootRequestCount => _blockByRootRequestCount;
        public List<string> SubscribedTopics { get; } = new();
        public List<string?> RequestedPreferredPeers { get; } = new();
        public Func<CancellationToken, Task>? ProbePeerStatusesHandler { get; set; }

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

        public Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken = default)
        {
            return RequestBlockByRootInternalAsync(blockRoot, preferredPeerKey: null, cancellationToken);
        }

        public Task<byte[]?> RequestBlockByRootAsync(
            ReadOnlyMemory<byte> blockRoot,
            string preferredPeerKey,
            CancellationToken cancellationToken = default)
        {
            return RequestBlockByRootInternalAsync(
                blockRoot,
                string.IsNullOrWhiteSpace(preferredPeerKey) ? null : preferredPeerKey.Trim(),
                cancellationToken);
        }

        public Task ProbePeerStatusesAsync(CancellationToken cancellationToken = default)
        {
            if (ProbePeerStatusesHandler is not null)
            {
                return ProbePeerStatusesHandler(cancellationToken);
            }

            return Task.CompletedTask;
        }

        private Task<byte[]?> RequestBlockByRootInternalAsync(
            ReadOnlyMemory<byte> blockRoot,
            string? preferredPeerKey,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _blockByRootRequestCount);
            lock (_lock)
            {
                RequestedPreferredPeers.Add(preferredPeerKey);
                var key = Convert.ToHexString(blockRoot.Span);
                if (!string.IsNullOrWhiteSpace(preferredPeerKey))
                {
                    var preferredKey = BuildPeerResponseKey(key, preferredPeerKey);
                    if (_blockByRootByPreferredPeer.TryGetValue(preferredKey, out var preferredPayload))
                    {
                        return Task.FromResult<byte[]?>(preferredPayload);
                    }
                }

                return Task.FromResult(_blockByRoot.TryGetValue(key, out var payload) ? payload : null);
            }
        }

        public void SetBlockByRootResponse(Bytes32 root, byte[] payload)
        {
            lock (_lock)
            {
                _blockByRoot[Convert.ToHexString(root.AsSpan())] = payload;
            }
        }

        public void SetBlockByRootResponseForPeer(Bytes32 root, string peerKey, byte[] payload)
        {
            lock (_lock)
            {
                var rootKey = Convert.ToHexString(root.AsSpan());
                _blockByRootByPreferredPeer[BuildPeerResponseKey(rootKey, peerKey)] = payload;
            }
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

        private static string BuildPeerResponseKey(string blockRootHex, string peerKey)
        {
            return $"{blockRootHex}|{peerKey.Trim()}";
        }
    }
}
