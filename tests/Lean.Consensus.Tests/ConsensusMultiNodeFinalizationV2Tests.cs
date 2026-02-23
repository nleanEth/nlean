using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using Lean.Network;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

[TestFixture]
public sealed class ConsensusMultiNodeFinalizationV2Tests
{
    private const int SecondsPerSlot = 4;
    private const int IntervalsPerSlot = ProtoArrayForkChoiceStore.IntervalsPerSlot; // 5

    [TestCase(4)]
    [TestCase(8)]
    public async Task MultiNodeGossip_FromGenesis_FinalizesConsistently(int validatorCount)
    {
        var bus = new InMemoryGossipBus();
        var canonicalGenesisRoot = BuildCanonicalGenesisRoot(validatorCount);
        var nodes = Enumerable.Range(0, validatorCount)
            .Select(index => CreateNodeV2(bus, validatorCount, $"n{index}"))
            .ToArray();

        try
        {
            foreach (var node in nodes)
                await node.Service.StartAsync(CancellationToken.None);

            var atSlotOne = await WaitUntilAsync(
                () => nodes.All(n => n.Service.CurrentSlot >= 1),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotOne, Is.True);

            var blockOne = CreateSignedBlock(
                blockSlot: 1,
                parentRoot: Bytes32.Zero(), parentSlot: 0,
                sourceRoot: Bytes32.Zero(), sourceSlot: 0,
                proposerAttesterId: 0, proposerIndex: 0,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockOneRoot = new Bytes32(blockOne.Message.Block.HashTreeRoot());
            await PublishBlockToAll(bus, nodes, blockOne);

            var atSlotTwo = await WaitUntilAsync(
                () => nodes.All(n => n.Service.CurrentSlot >= 2),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotTwo, Is.True);

            var blockTwo = CreateSignedBlock(
                blockSlot: 2,
                parentRoot: blockOneRoot, parentSlot: 1,
                sourceRoot: Bytes32.Zero(), sourceSlot: 0,
                proposerAttesterId: 1, proposerIndex: 1,
                targetRoot: blockOneRoot, targetSlot: 1,
                headRoot: blockOneRoot, headSlot: 1,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            var blockTwoRoot = new Bytes32(blockTwo.Message.Block.HashTreeRoot());
            await PublishBlockToAll(bus, nodes, blockTwo);

            var atSlotThree = await WaitUntilAsync(
                () => nodes.All(n => n.Service.CurrentSlot >= 3),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotThree, Is.True);

            var blockThree = CreateSignedBlock(
                blockSlot: 3,
                parentRoot: blockTwoRoot, parentSlot: 2,
                sourceRoot: blockOneRoot, sourceSlot: 1,
                proposerAttesterId: 2, proposerIndex: 2,
                targetRoot: blockTwoRoot, targetSlot: 2,
                headRoot: blockTwoRoot, headSlot: 2,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
            await PublishBlockToAll(bus, nodes, blockThree);

            var converged = await WaitUntilAsync(
                () => nodes.All(n => n.Service.HeadSlot >= 3),
                TimeSpan.FromSeconds(8));
            Assert.That(converged, Is.True);

            // Wait for finalization ticks
            var finalized = await WaitUntilAsync(
                () => nodes.All(n => n.Service.FinalizedSlot >= 1),
                TimeSpan.FromSeconds(12));
            Assert.That(finalized, Is.True);
        }
        finally
        {
            foreach (var node in nodes)
                await node.Service.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task ThreeNode_BlockChasing_AfterOfflineNodeRestart_FinalizesAgain()
    {
        const int validatorCount = 8;
        var bus = new InMemoryGossipBus();
        var canonicalGenesisRoot = BuildCanonicalGenesisRoot(validatorCount);
        var nodes = new[]
        {
            CreateNodeV2(bus, validatorCount, "n0"),
            CreateNodeV2(bus, validatorCount, "n1"),
            CreateNodeV2(bus, validatorCount, "n2")
        };

        var rootsBySlot = new Dictionary<ulong, Bytes32>
        {
            [0] = canonicalGenesisRoot
        };

        SignedBlockWithAttestation BuildChainBlock(ulong slot, ulong proposerIndex)
        {
            if (slot == 0)
                throw new ArgumentOutOfRangeException(nameof(slot));

            var parentSlot = slot - 1;
            var sourceSlot = slot <= 2 ? 0 : slot - 2;
            var parentRoot = rootsBySlot[parentSlot];
            var sourceRoot = rootsBySlot[sourceSlot];
            return CreateSignedBlock(
                blockSlot: slot,
                parentRoot: parentRoot, parentSlot: parentSlot,
                sourceRoot: sourceRoot, sourceSlot: sourceSlot,
                proposerAttesterId: proposerIndex, proposerIndex: proposerIndex,
                targetRoot: parentRoot, targetSlot: parentSlot,
                headRoot: parentRoot, headSlot: parentSlot,
                aggregationBits: Enumerable.Repeat(true, validatorCount).ToArray(),
                canonicalGenesisRoot: canonicalGenesisRoot);
        }

        async Task<Bytes32> PublishBlockAsync(TestNodeV2[] targetNodes, ulong slot, ulong proposerIndex)
        {
            var block = BuildChainBlock(slot, proposerIndex);
            var root = new Bytes32(block.Message.Block.HashTreeRoot());
            rootsBySlot[slot] = root;
            // Publish to bus and deliver directly to target nodes
            bus.StoreBlock(block);
            foreach (var node in targetNodes)
                await node.SyncService.OnGossipBlockAsync(block, root, "external");
            return root;
        }

        try
        {
            foreach (var node in nodes)
            {
                await node.Service.StartAsync(CancellationToken.None);
                // Register peers so SyncService enters Syncing/Synced state
                node.SyncService.OnPeerConnected("external");
                await node.SyncService.OnPeerStatusAsync("external", 0, 0);
            }

            // Wait for slot 1
            var atSlotOne = await WaitUntilAsync(
                () => nodes.All(n => n.Service.CurrentSlot >= 1),
                TimeSpan.FromSeconds(8));
            Assert.That(atSlotOne, Is.True, "nodes should reach slot 1");

            // Phase 1: blocks 1-4, all nodes see them
            await PublishBlockAsync(nodes, 1, 0);
            await PublishBlockAsync(nodes, 2, 1);
            await PublishBlockAsync(nodes, 3, 2);
            await PublishBlockAsync(nodes, 4, 3);

            var firstFinalization = await WaitUntilAsync(
                () => nodes.All(n =>
                    n.Service.HeadSlot >= 4 &&
                    n.Service.FinalizedSlot >= 2),
                TimeSpan.FromSeconds(20));
            Assert.That(firstFinalization, Is.True,
                $"first finalization failed: heads=[{string.Join(",", nodes.Select(n => n.Service.HeadSlot))}], finalized=[{string.Join(",", nodes.Select(n => n.Service.FinalizedSlot))}]");

            // Phase 2: stop node1, publish blocks 5-8 to nodes 0 and 2 only
            await nodes[1].Service.StopAsync(CancellationToken.None);
            var activeNodes = new[] { nodes[0], nodes[2] };

            await PublishBlockAsync(activeNodes, 5, 4);
            await PublishBlockAsync(activeNodes, 6, 5);
            await PublishBlockAsync(activeNodes, 7, 6);
            await PublishBlockAsync(activeNodes, 8, 7);

            var twoNodeProgress = await WaitUntilAsync(
                () => nodes[0].Service.HeadSlot >= 8 &&
                      nodes[2].Service.HeadSlot >= 8 &&
                      nodes[0].Service.FinalizedSlot >= 4 &&
                      nodes[2].Service.FinalizedSlot >= 4,
                TimeSpan.FromSeconds(20));
            Assert.That(twoNodeProgress, Is.True,
                $"two-node progress failed: heads=[{nodes[0].Service.HeadSlot},{nodes[2].Service.HeadSlot}], finalized=[{nodes[0].Service.FinalizedSlot},{nodes[2].Service.FinalizedSlot}]");

            // Phase 3: restart node1, publish blocks 9-11 to all
            var finalizedBeforeRestart = Math.Min(nodes[0].Service.FinalizedSlot, nodes[2].Service.FinalizedSlot);

            // Recreate node1 with fresh state but same bus
            nodes[1] = CreateNodeV2(bus, validatorCount, "n1");
            await nodes[1].Service.StartAsync(CancellationToken.None);
            nodes[1].SyncService.OnPeerConnected("external");
            await nodes[1].SyncService.OnPeerStatusAsync("external", 8, 4);

            var restartedReady = await WaitUntilAsync(
                () => nodes[1].Service.CurrentSlot >= 1,
                TimeSpan.FromSeconds(8));
            Assert.That(restartedReady, Is.True, "restarted node should reach slot 1");

            // Publish blocks 9-11 to all nodes (including restarted node1)
            await PublishBlockAsync(nodes, 9, 0);
            await PublishBlockAsync(nodes, 10, 1);
            await PublishBlockAsync(nodes, 11, 2);

            var secondFinalization = await WaitUntilAsync(
                () => nodes.All(n =>
                    n.Service.HeadSlot >= 11 &&
                    n.Service.FinalizedSlot >= finalizedBeforeRestart + 2),
                TimeSpan.FromSeconds(25));
            Assert.That(
                secondFinalization,
                Is.True,
                $"post-restart finalization stalled: baseline={finalizedBeforeRestart}, " +
                $"heads=[{string.Join(",", nodes.Select(n => n.Service.HeadSlot))}], " +
                $"finalized=[{string.Join(",", nodes.Select(n => n.Service.FinalizedSlot))}]");
        }
        finally
        {
            foreach (var node in nodes)
                await node.Service.StopAsync(CancellationToken.None);
        }
    }

    #region Test Infrastructure

    private static TestNodeV2 CreateNodeV2(
        InMemoryGossipBus bus,
        int validatorCount,
        string nodeId,
        ulong? genesisTimeUnix = null)
    {
        var config = new ConsensusConfig
        {
            SecondsPerSlot = SecondsPerSlot,
            InitialValidatorCount = (ulong)Math.Max(1, validatorCount),
            GenesisTimeUnix = genesisTimeUnix ?? 0
        };
        var stateTransition = new ForkChoiceStateTransition(config);
        var store = new ProtoArrayForkChoiceStore(stateTransition, config);

        var resolvedGenesis = config.GenesisTimeUnix > 0
            ? config.GenesisTimeUnix
            : (ulong)Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2);

        var clock = new SlotClock(resolvedGenesis, SecondsPerSlot, IntervalsPerSlot, new WallClockTimeSource());
        var serviceV2 = new ConsensusServiceV2(store, clock, config);

        // Sync layer
        var peerManager = new SyncPeerManager();
        var cache = new NewBlockCache();
        var networkRequester = new BusNetworkRequester(bus, nodeId);
        var syncService = new SyncService(serviceV2, peerManager, cache, store, networkRequester);

        // Set sync service on ConsensusServiceV2
        var serviceWithSync = new ConsensusServiceV2(store, clock, config, syncService);

        return new TestNodeV2(nodeId, serviceWithSync, syncService, store);
    }

    private static Task PublishBlockToAll(InMemoryGossipBus bus, TestNodeV2[] nodes, SignedBlockWithAttestation block)
    {
        bus.StoreBlock(block);
        var root = new Bytes32(block.Message.Block.HashTreeRoot());
        foreach (var node in nodes)
            node.SyncService.OnGossipBlockAsync(block, root, "publisher");
        return Task.CompletedTask;
    }

    private static Bytes32 BuildCanonicalGenesisRoot(int validatorCount)
    {
        var config = new ConsensusConfig { InitialValidatorCount = (ulong)Math.Max(1, validatorCount) };
        var store = new ProtoArrayForkChoiceStore(new ForkChoiceStateTransition(config), config);
        return store.HeadRoot;
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - startedAt > timeout)
                return false;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return true;
    }

    #endregion

    #region Test Types

    private sealed record TestNodeV2(
        string NodeId,
        ConsensusServiceV2 Service,
        SyncService SyncService,
        ProtoArrayForkChoiceStore Store);

    private sealed class WallClockTimeSource : ITimeSource
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// INetworkRequester backed by the in-memory gossip bus for backfill sync.
    /// </summary>
    private sealed class BusNetworkRequester : INetworkRequester
    {
        private readonly InMemoryGossipBus _bus;
        private readonly string _nodeId;

        public BusNetworkRequester(InMemoryGossipBus bus, string nodeId)
        {
            _bus = bus;
            _nodeId = nodeId;
        }

        public Task<List<SignedBlockWithAttestation>> RequestBlocksByRootAsync(
            string peerId, List<Bytes32> roots, CancellationToken ct)
        {
            var results = new List<SignedBlockWithAttestation>();
            foreach (var root in roots)
            {
                var block = _bus.GetBlockByRoot(root);
                if (block is not null)
                    results.Add(block);
            }

            return Task.FromResult(results);
        }
    }

    /// <summary>
    /// Shared in-memory block store for gossip simulation.
    /// Stores decoded blocks by their root hash.
    /// </summary>
    private sealed class InMemoryGossipBus
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, SignedBlockWithAttestation> _blocksByRoot = new(StringComparer.Ordinal);

        public void StoreBlock(SignedBlockWithAttestation block)
        {
            var root = new Bytes32(block.Message.Block.HashTreeRoot());
            var key = Convert.ToHexString(root.AsSpan());
            lock (_lock)
            {
                _blocksByRoot[key] = block;
            }
        }

        public SignedBlockWithAttestation? GetBlockByRoot(Bytes32 root)
        {
            var key = Convert.ToHexString(root.AsSpan());
            lock (_lock)
            {
                return _blocksByRoot.TryGetValue(key, out var block) ? block : null;
            }
        }
    }

    #endregion
}
