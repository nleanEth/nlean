using Lean.Consensus.Types;
using Lean.Network;
using Lean.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

[TestFixture]
public sealed class ConsensusMultiNodeFinalizationTests
{
    [TestCase(4)]
    [TestCase(8)]
    public async Task MultiNodeGossip_FromGenesis_FinalizesConsistently(int validatorCount)
    {
        var bus = new InMemoryGossipBus();
        var canonicalGenesisRoot = BuildCanonicalGenesisRoot(validatorCount);
        var nodes = Enumerable.Range(0, validatorCount)
            .Select(index => CreateNode(bus, validatorCount, $"n{index}"))
            .ToArray();

        try
        {
            foreach (var node in nodes)
            {
                await node.Service.StartAsync(CancellationToken.None);
            }

            var atSlotOne = await WaitUntilAsync(
                () => nodes.All(node => node.Service.CurrentSlot >= 1),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotOne, Is.True);

            var blockOne = CreateSignedBlock(
                blockSlot: 1,
                parentRoot: Bytes32.Zero(),
                parentSlot: 0,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerAttesterId: 0,
                proposerIndex: 0,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
            await nodes[0].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockOne));

            var atSlotTwo = await WaitUntilAsync(
                () => nodes.All(node => node.Service.CurrentSlot >= 2),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotTwo, Is.True);

            var blockTwo = CreateSignedBlock(
                blockSlot: 2,
                parentRoot: blockOneRoot,
                parentSlot: 1,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerAttesterId: 1,
                proposerIndex: 1,
                targetRoot: blockOneRoot,
                targetSlot: 1,
                headRoot: blockOneRoot,
                headSlot: 1,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
            await nodes[1].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockTwo));

            var atSlotThree = await WaitUntilAsync(
                () => nodes.All(node => node.Service.CurrentSlot >= 3),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotThree, Is.True);

            var blockThree = CreateSignedBlock(
                blockSlot: 3,
                parentRoot: blockTwoRoot,
                parentSlot: 2,
                sourceRoot: blockOneRoot,
                sourceSlot: 1,
                proposerAttesterId: 2,
                proposerIndex: 2,
                targetRoot: blockTwoRoot,
                targetSlot: 2,
                headRoot: blockTwoRoot,
                headSlot: 2,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            await nodes[2].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockThree));

            var converged = await WaitUntilAsync(
                () => nodes.All(node => node.Service.HeadSlot == 3),
                TimeSpan.FromSeconds(8));
            Assert.That(converged, Is.True);

            foreach (var node in nodes)
            {
                Assert.That(node.StateStore.TryLoad(out var persisted), Is.True);
                Assert.That(persisted, Is.Not.Null);
                Assert.That(persisted!.HeadSlot, Is.EqualTo(3));
                Assert.That(persisted.LatestJustifiedSlot, Is.EqualTo(2));
                Assert.That(persisted.LatestJustifiedRoot, Is.EqualTo(blockTwoRoot.AsSpan().ToArray()));
                Assert.That(persisted.LatestFinalizedSlot, Is.EqualTo(1));
                Assert.That(persisted.LatestFinalizedRoot, Is.EqualTo(blockOneRoot.AsSpan().ToArray()));
            }
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.Service.StopAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task LateJoiner_StatusSyncThenGossip_Finalizes()
    {
        const int validatorCount = 8;
        var bus = new InMemoryGossipBus();
        var canonicalGenesisRoot = BuildCanonicalGenesisRoot(validatorCount);
        var earlyNodes = new[]
        {
            CreateNode(bus, validatorCount, "n0"),
            CreateNode(bus, validatorCount, "n1")
        };
        var lateNode = CreateNode(bus, validatorCount, "n-late");

        try
        {
            foreach (var node in earlyNodes)
            {
                await node.Service.StartAsync(CancellationToken.None);
            }

            var earlyAtSlotTwo = await WaitUntilAsync(
                () => earlyNodes.All(node => node.Service.CurrentSlot >= 2),
                TimeSpan.FromSeconds(8));
            Assert.That(earlyAtSlotTwo, Is.True);

            var blockOne = CreateSignedBlock(
                blockSlot: 1,
                parentRoot: Bytes32.Zero(),
                parentSlot: 0,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerAttesterId: 0,
                proposerIndex: 0,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
            await earlyNodes[0].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockOne));

            var earlyHeadOne = await WaitUntilAsync(
                () => earlyNodes.All(node => node.Service.HeadSlot >= 1),
                TimeSpan.FromSeconds(8));
            Assert.That(earlyHeadOne, Is.True);

            var blockTwo = CreateSignedBlock(
                blockSlot: 2,
                parentRoot: blockOneRoot,
                parentSlot: 1,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerAttesterId: 1,
                proposerIndex: 1,
                targetRoot: blockOneRoot,
                targetSlot: 1,
                headRoot: blockOneRoot,
                headSlot: 1,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
            await earlyNodes[1].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockTwo));

            var earlyHeadTwo = await WaitUntilAsync(
                () => earlyNodes.All(node => node.Service.HeadSlot >= 2),
                TimeSpan.FromSeconds(8));
            Assert.That(earlyHeadTwo, Is.True);

            await lateNode.Service.StartAsync(CancellationToken.None);
            var lateAtSlotTwoBeforeSync = await WaitUntilAsync(
                () => lateNode.Service.CurrentSlot >= 2,
                TimeSpan.FromSeconds(8));
            Assert.That(lateAtSlotTwoBeforeSync, Is.True);
            await lateNode.StatusRouter.HandlePeerStatusAsync(
                new LeanStatusMessage(
                    finalizedRoot: Bytes32.Zero().AsSpan(),
                    finalizedSlot: 0,
                    headRoot: blockTwoRoot.AsSpan(),
                    headSlot: 2),
                CancellationToken.None);

            var syncedToTwo = await WaitUntilAsync(
                () => lateNode.Service.HeadSlot == 2,
                TimeSpan.FromSeconds(8));
            Assert.That(syncedToTwo, Is.True);

            var blockThree = CreateSignedBlock(
                blockSlot: 3,
                parentRoot: blockTwoRoot,
                parentSlot: 2,
                sourceRoot: blockOneRoot,
                sourceSlot: 1,
                proposerAttesterId: 2,
                proposerIndex: 2,
                targetRoot: blockTwoRoot,
                targetSlot: 2,
                headRoot: blockTwoRoot,
                headSlot: 2,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockThreeRoot = new Bytes32(blockThree.Message.Block.HashTreeRoot());
            await earlyNodes[0].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockThree));

            var lateFinalized = await WaitUntilAsync(
                () => lateNode.Service.HeadSlot == 3 &&
                      lateNode.StateStore.TryLoad(out var persisted) &&
                      persisted is not null &&
                      persisted.LatestJustifiedSlot == 2 &&
                      persisted.LatestFinalizedSlot == 1,
                TimeSpan.FromSeconds(8));
            Assert.That(lateFinalized, Is.True);
            Assert.That(lateNode.Service.HeadRoot, Is.EqualTo(blockThreeRoot.AsSpan().ToArray()));
        }
        finally
        {
            await lateNode.Service.StopAsync(CancellationToken.None);
            foreach (var node in earlyNodes)
            {
                await node.Service.StopAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task MultiNodeGossip_WithMessageLoss_RecoversViaBlocksByRootAndFinalizes()
    {
        const int validatorCount = 8;
        var bus = new InMemoryGossipBus();
        var canonicalGenesisRoot = BuildCanonicalGenesisRoot(validatorCount);
        var nodes = Enumerable.Range(0, validatorCount)
            .Select(index => CreateNode(bus, validatorCount, $"n{index}"))
            .ToArray();

        // Drop a subset of first deliveries to force missing-parent recovery via blocks-by-root.
        bus.DropNextDeliveries(GossipTopics.Blocks, "n0", "n4");
        bus.DropNextDeliveries(GossipTopics.Blocks, "n0", "n5");
        bus.DropNextDeliveries(GossipTopics.Blocks, "n1", "n0");
        bus.DropNextDeliveries(GossipTopics.Blocks, "n1", "n2");

        try
        {
            foreach (var node in nodes)
            {
                await node.Service.StartAsync(CancellationToken.None);
            }

            var atSlotThree = await WaitUntilAsync(
                () => nodes.All(node => node.Service.CurrentSlot >= 3),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotThree, Is.True);

            var blockOne = CreateSignedBlock(
                blockSlot: 1,
                parentRoot: Bytes32.Zero(),
                parentSlot: 0,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerAttesterId: 0,
                proposerIndex: 0,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
            await nodes[0].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockOne));

            var blockTwo = CreateSignedBlock(
                blockSlot: 2,
                parentRoot: blockOneRoot,
                parentSlot: 1,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerAttesterId: 1,
                proposerIndex: 1,
                targetRoot: blockOneRoot,
                targetSlot: 1,
                headRoot: blockOneRoot,
                headSlot: 1,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
            await nodes[1].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockTwo));

            var blockThree = CreateSignedBlock(
                blockSlot: 3,
                parentRoot: blockTwoRoot,
                parentSlot: 2,
                sourceRoot: blockOneRoot,
                sourceSlot: 1,
                proposerAttesterId: 2,
                proposerIndex: 2,
                targetRoot: blockTwoRoot,
                targetSlot: 2,
                headRoot: blockTwoRoot,
                headSlot: 2,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            await nodes[2].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockThree));

            var converged = await WaitUntilAsync(
                () => nodes.All(node => node.Service.HeadSlot == 3),
                TimeSpan.FromSeconds(8));
            Assert.That(converged, Is.True);

            foreach (var node in nodes)
            {
                Assert.That(node.StateStore.TryLoad(out var persisted), Is.True);
                Assert.That(persisted, Is.Not.Null);
                Assert.That(persisted!.LatestJustifiedSlot, Is.EqualTo(2));
                Assert.That(persisted.LatestFinalizedSlot, Is.EqualTo(1));
            }

            var totalRecoveryRequests = nodes.Sum(node => node.Network.BlockByRootRequestCount);
            Assert.That(totalRecoveryRequests, Is.GreaterThan(0));
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.Service.StopAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task MultiNodeGossip_WithNetworkPartition_HealsAndFinalizes()
    {
        const int validatorCount = 8;
        var bus = new InMemoryGossipBus();
        var canonicalGenesisRoot = BuildCanonicalGenesisRoot(validatorCount);
        var nodes = Enumerable.Range(0, validatorCount)
            .Select(index => CreateNode(bus, validatorCount, $"n{index}"))
            .ToArray();

        var partitionA = new[] { "n0", "n1", "n2", "n3" };
        var partitionB = new[] { "n4", "n5", "n6", "n7" };

        try
        {
            foreach (var node in nodes)
            {
                await node.Service.StartAsync(CancellationToken.None);
            }

            var atSlotThree = await WaitUntilAsync(
                () => nodes.All(node => node.Service.CurrentSlot >= 3),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotThree, Is.True);

            bus.PartitionBidirectional(partitionA, partitionB);

            var blockOne = CreateSignedBlock(
                blockSlot: 1,
                parentRoot: Bytes32.Zero(),
                parentSlot: 0,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerAttesterId: 0,
                proposerIndex: 0,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
            await nodes[0].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockOne));

            var blockTwo = CreateSignedBlock(
                blockSlot: 2,
                parentRoot: blockOneRoot,
                parentSlot: 1,
                sourceRoot: Bytes32.Zero(),
                sourceSlot: 0,
                proposerAttesterId: 1,
                proposerIndex: 1,
                targetRoot: blockOneRoot,
                targetSlot: 1,
                headRoot: blockOneRoot,
                headSlot: 1,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
            await nodes[1].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockTwo));

            var partitionDiverged = await WaitUntilAsync(
                () =>
                    nodes.Where(node => partitionA.Contains(node.NodeId)).All(node => node.Service.HeadSlot >= 2) &&
                    nodes.Where(node => partitionB.Contains(node.NodeId)).All(node => node.Service.HeadSlot < 2),
                TimeSpan.FromSeconds(8));
            Assert.That(partitionDiverged, Is.True);

            bus.ClearPartitions();

            var blockThree = CreateSignedBlock(
                blockSlot: 3,
                parentRoot: blockTwoRoot,
                parentSlot: 2,
                sourceRoot: blockOneRoot,
                sourceSlot: 1,
                proposerAttesterId: 2,
                proposerIndex: 2,
                targetRoot: blockTwoRoot,
                targetSlot: 2,
                headRoot: blockTwoRoot,
                headSlot: 2,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            await nodes[2].Network.PublishAsync(GossipTopics.Blocks, SszEncoding.Encode(blockThree));

            var converged = await WaitUntilAsync(
                () => nodes.All(node => node.Service.HeadSlot == 3),
                TimeSpan.FromSeconds(8));
            Assert.That(converged, Is.True);

            foreach (var node in nodes)
            {
                Assert.That(node.StateStore.TryLoad(out var persisted), Is.True);
                Assert.That(persisted, Is.Not.Null);
                Assert.That(persisted!.LatestJustifiedSlot, Is.EqualTo(2));
                Assert.That(persisted.LatestFinalizedSlot, Is.EqualTo(1));
            }

            var partitionBRecoveryRequests = nodes
                .Where(node => partitionB.Contains(node.NodeId))
                .Sum(node => node.Network.BlockByRootRequestCount);
            Assert.That(partitionBRecoveryRequests, Is.GreaterThan(0));
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.Service.StopAsync(CancellationToken.None);
            }
        }
    }

    private static TestNode CreateNode(InMemoryGossipBus bus, int validatorCount, string? nodeId = null)
    {
        var resolvedNodeId = string.IsNullOrWhiteSpace(nodeId) ? Guid.NewGuid().ToString("N") : nodeId.Trim();
        var store = new InMemoryKeyValueStore();
        var stateStore = new ConsensusStateStore(store);
        var network = new InMemoryBusNetworkService(bus, resolvedNodeId);
        var statusRouter = new StatusRpcRouter();
        var consensusConfig = new ConsensusConfig
        {
            SecondsPerSlot = 1,
            EnableGossipProcessing = true,
            InitialValidatorCount = (ulong)Math.Max(1, validatorCount)
        };
        var forkChoice = new ForkChoiceStore(new ForkChoiceStateTransition(consensusConfig), consensusConfig);
        var service = new ConsensusService(
            NullLogger<ConsensusService>.Instance,
            network,
            new SignedBlockWithAttestationGossipDecoder(),
            new SignedAttestationGossipDecoder(),
            stateStore,
            forkChoice,
            consensusConfig,
            statusRpcRouter: statusRouter);

        return new TestNode(resolvedNodeId, service, network, stateStore, statusRouter);
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
        IReadOnlyList<bool>? aggregationBits = null,
        Bytes32? canonicalGenesisRoot = null)
    {
        var effectiveCanonicalGenesisRoot = canonicalGenesisRoot ?? BuildCanonicalGenesisRoot(1);
        var normalizedParentRoot = NormalizeRootAtSlot(parentRoot, parentSlot, effectiveCanonicalGenesisRoot);
        var normalizedSourceRoot = NormalizeRootAtSlot(sourceRoot, sourceSlot, effectiveCanonicalGenesisRoot);
        var effectiveTargetSlot = targetSlot ?? parentSlot;
        var effectiveTargetRoot = NormalizeRootAtSlot(targetRoot ?? normalizedParentRoot, effectiveTargetSlot, effectiveCanonicalGenesisRoot);
        var effectiveHeadSlot = headSlot ?? parentSlot;
        var effectiveHeadRoot = NormalizeRootAtSlot(headRoot ?? normalizedParentRoot, effectiveHeadSlot, effectiveCanonicalGenesisRoot);
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

    private static Bytes32 NormalizeRootAtSlot(Bytes32 root, ulong slot, Bytes32 canonicalGenesisRoot)
    {
        return slot == 0 && root.Equals(Bytes32.Zero()) ? canonicalGenesisRoot : root;
    }

    private static Bytes32 BuildCanonicalGenesisRoot(int validatorCount)
    {
        var config = new ConsensusConfig { InitialValidatorCount = (ulong)Math.Max(1, validatorCount) };
        return new ForkChoiceStore(new ForkChoiceStateTransition(config), config).HeadRoot;
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

    private sealed record TestNode(
        string NodeId,
        ConsensusService Service,
        InMemoryBusNetworkService Network,
        ConsensusStateStore StateStore,
        StatusRpcRouter StatusRouter);

    private sealed class InMemoryBusNetworkService : INetworkService
    {
        private readonly InMemoryGossipBus _bus;
        private readonly string _nodeId;
        private int _blockByRootRequestCount;

        public InMemoryBusNetworkService(InMemoryGossipBus bus, string nodeId)
        {
            _bus = bus;
            _nodeId = nodeId;
        }

        public int BlockByRootRequestCount => Volatile.Read(ref _blockByRootRequestCount);

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
            _bus.Publish(_nodeId, topic, payload.ToArray());
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default)
        {
            _bus.Subscribe(_nodeId, topic, handler);
            return Task.CompletedTask;
        }

        public Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _blockByRootRequestCount);
            if (_bus.TryGetBlockByRoot(_nodeId, blockRoot.Span, out var payload))
            {
                return Task.FromResult<byte[]?>(payload);
            }

            return Task.FromResult<byte[]?>(null);
        }

        public Task<byte[]?> RequestBlockByRootAsync(
            ReadOnlyMemory<byte> blockRoot,
            string preferredPeerKey,
            CancellationToken cancellationToken = default)
        {
            return RequestBlockByRootAsync(blockRoot, cancellationToken);
        }

        public Task ProbePeerStatusesAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryGossipBus
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<Subscription>> _subscriptions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BlockRecord> _blocksByRoot = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _dropRules = new(StringComparer.Ordinal);
        private readonly HashSet<string> _partitionedLinks = new(StringComparer.Ordinal);
        private readonly SignedBlockWithAttestationGossipDecoder _blockDecoder = new();

        public void Subscribe(string nodeId, string topic, Action<byte[]> handler)
        {
            lock (_lock)
            {
                if (!_subscriptions.TryGetValue(topic, out var handlers))
                {
                    handlers = new List<Subscription>();
                    _subscriptions[topic] = handlers;
                }

                handlers.Add(new Subscription(nodeId, handler));
            }
        }

        public void DropNextDeliveries(string topic, string fromNodeId, string toNodeId, int deliveries = 1)
        {
            if (deliveries <= 0)
            {
                return;
            }

            lock (_lock)
            {
                _dropRules[BuildDropRuleKey(topic, fromNodeId, toNodeId)] = deliveries;
            }
        }

        public void PartitionBidirectional(IReadOnlyCollection<string> sideA, IReadOnlyCollection<string> sideB)
        {
            lock (_lock)
            {
                foreach (var from in sideA)
                {
                    foreach (var to in sideB)
                    {
                        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                        {
                            continue;
                        }

                        _partitionedLinks.Add(BuildPeerLinkKey(from, to));
                        _partitionedLinks.Add(BuildPeerLinkKey(to, from));
                    }
                }
            }
        }

        public void ClearPartitions()
        {
            lock (_lock)
            {
                _partitionedLinks.Clear();
            }
        }

        public void Publish(string publisherNodeId, string topic, byte[] payload)
        {
            (Subscription Subscriber, bool Drop)[] deliveries;
            lock (_lock)
            {
                if (topic.Contains("/block/", StringComparison.Ordinal))
                {
                    var decodeResult = _blockDecoder.DecodeAndValidate(payload);
                    if (decodeResult.IsSuccess && decodeResult.SignedBlock is not null)
                    {
                        var blockRoot = new Bytes32(decodeResult.SignedBlock.Message.Block.HashTreeRoot());
                        var key = Convert.ToHexString(blockRoot.AsSpan());
                        if (!_blocksByRoot.TryGetValue(key, out var record))
                        {
                            record = new BlockRecord(payload.ToArray());
                            _blocksByRoot[key] = record;
                        }
                        else
                        {
                            record.SetPayload(payload);
                        }

                        record.AddSource(publisherNodeId);
                    }
                }

                if (!_subscriptions.TryGetValue(topic, out var subscribers))
                {
                    return;
                }

                deliveries = subscribers
                    .Select(subscriber => (
                        subscriber,
                        Drop: ConsumeDropRuleLocked(topic, publisherNodeId, subscriber.NodeId) ||
                              IsPartitionedLocked(publisherNodeId, subscriber.NodeId)))
                    .ToArray();
            }

            foreach (var delivery in deliveries)
            {
                if (delivery.Drop)
                {
                    continue;
                }

                delivery.Subscriber.Handler(payload);
            }
        }

        public bool TryGetBlockByRoot(string requesterNodeId, ReadOnlySpan<byte> blockRoot, out byte[]? payload)
        {
            payload = null;
            if (blockRoot.Length != SszEncoding.Bytes32Length)
            {
                return false;
            }

            lock (_lock)
            {
                if (!_blocksByRoot.TryGetValue(Convert.ToHexString(blockRoot), out var record))
                {
                    return false;
                }

                if (!record.CanServe(requesterNodeId, IsPartitionedLocked))
                {
                    return false;
                }

                payload = record.GetPayloadCopy();
                return true;
            }
        }

        private bool ConsumeDropRuleLocked(string topic, string fromNodeId, string toNodeId)
        {
            var key = BuildDropRuleKey(topic, fromNodeId, toNodeId);
            if (!_dropRules.TryGetValue(key, out var remaining) || remaining <= 0)
            {
                return false;
            }

            if (remaining == 1)
            {
                _dropRules.Remove(key);
            }
            else
            {
                _dropRules[key] = remaining - 1;
            }

            return true;
        }

        private static string BuildDropRuleKey(string topic, string fromNodeId, string toNodeId)
        {
            return $"{topic}|{fromNodeId}|{toNodeId}";
        }

        private bool IsPartitionedLocked(string fromNodeId, string toNodeId)
        {
            if (string.Equals(fromNodeId, toNodeId, StringComparison.Ordinal))
            {
                return false;
            }

            return _partitionedLinks.Contains(BuildPeerLinkKey(fromNodeId, toNodeId));
        }

        private static string BuildPeerLinkKey(string fromNodeId, string toNodeId)
        {
            return $"{fromNodeId}|{toNodeId}";
        }

        private sealed class BlockRecord
        {
            private byte[] _payload;
            private readonly HashSet<string> _sourceNodeIds = new(StringComparer.Ordinal);

            public BlockRecord(byte[] payload)
            {
                _payload = payload.ToArray();
            }

            public void AddSource(string sourceNodeId)
            {
                if (!string.IsNullOrWhiteSpace(sourceNodeId))
                {
                    _sourceNodeIds.Add(sourceNodeId.Trim());
                }
            }

            public void SetPayload(byte[] payload)
            {
                _payload = payload.ToArray();
            }

            public byte[] GetPayloadCopy()
            {
                return _payload.ToArray();
            }

            public bool CanServe(string requesterNodeId, Func<string, string, bool> isPartitioned)
            {
                if (string.IsNullOrWhiteSpace(requesterNodeId))
                {
                    return true;
                }

                foreach (var sourceNodeId in _sourceNodeIds)
                {
                    if (!isPartitioned(sourceNodeId, requesterNodeId))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private sealed record Subscription(string NodeId, Action<byte[]> Handler);
    }
}
