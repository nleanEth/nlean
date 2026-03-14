using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using Lean.Network;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Sync;

[TestFixture]
public sealed class Libp2pNetworkRequesterTests
{
    [Test]
    public async Task RequestBlocksByRoot_ReturnsDecodedBlocks()
    {
        var block = CreateSignedBlock(slot: 1);
        var encoded = SszEncoding.Encode(block);
        var root = new Bytes32(block.Message.HashTreeRoot());

        var network = new FakeNetworkService();
        network.SetResponse(root, encoded);

        var requester = new Libp2pNetworkRequester(network);
        var results = await requester.RequestBlocksByRootAsync("peer1", [root], CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Message.Block.Slot, Is.EqualTo(new Slot(1)));
    }

    [Test]
    public async Task RequestBlocksByRoot_SkipsMissingBlocks()
    {
        var network = new FakeNetworkService();
        var requester = new Libp2pNetworkRequester(network);

        var root = new Bytes32(Enumerable.Repeat((byte)0xAA, 32).ToArray());
        var results = await requester.RequestBlocksByRootAsync("peer1", [root], CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task RequestBlocksByRoot_SkipsInvalidPayloads()
    {
        var root = new Bytes32(Enumerable.Repeat((byte)0xBB, 32).ToArray());
        var network = new FakeNetworkService();
        network.SetResponse(root, new byte[] { 0xFF, 0xFF });

        var requester = new Libp2pNetworkRequester(network);
        var results = await requester.RequestBlocksByRootAsync("peer1", [root], CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(0));
    }

    [Test]
    public void RequestBlocksByRoot_RespectsCancel()
    {
        var network = new FakeNetworkService();
        var requester = new Libp2pNetworkRequester(network);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var root = new Bytes32(Enumerable.Repeat((byte)0xCC, 32).ToArray());
        Assert.ThrowsAsync<OperationCanceledException>(
            () => requester.RequestBlocksByRootAsync("peer1", [root], cts.Token));
    }

    [Test]
    public void RequestBlocksByRoot_ThrowsOnHardDeadline()
    {
        var network = new HangingNetworkService();
        var requester = new Libp2pNetworkRequester(network, hardDeadlineMs: 100);

        var root = new Bytes32(Enumerable.Repeat((byte)0xDD, 32).ToArray());
        var ex = Assert.ThrowsAsync<OperationCanceledException>(
            () => requester.RequestBlocksByRootAsync("peer1", [root], CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("hard deadline"));
    }

    private static SignedBlockWithAttestation CreateSignedBlock(ulong slot)
    {
        var attData = new AttestationData(
            new Slot(slot),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)));

        var block = new Block(
            new Slot(slot), 0,
            Bytes32.Zero(), Bytes32.Zero(),
            new BlockBody(Array.Empty<AggregatedAttestation>()));

        var message = new BlockWithAttestation(block, new Attestation(0, attData));
        var sigs = new BlockSignatures(Array.Empty<AggregatedSignatureProof>(), XmssSignature.Empty());
        return new SignedBlockWithAttestation(message, sigs);
    }

    private sealed class FakeNetworkService : INetworkService
    {
        private readonly Dictionary<string, byte[]> _responses = new();

        public void SetResponse(Bytes32 root, byte[] payload) =>
            _responses[Convert.ToHexString(root.AsSpan())] = payload;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ProbePeerStatusesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ConnectToPeersAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<List<byte[]>> RequestBlocksByRootBatchAsync(
            List<byte[]> roots, string? preferredPeerKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = new List<byte[]>();
            foreach (var root in roots)
            {
                var key = Convert.ToHexString(root);
                if (_responses.TryGetValue(key, out var data))
                    results.Add(data);
            }
            return Task.FromResult(results);
        }
    }

    private sealed class HangingNetworkService : INetworkService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ProbePeerStatusesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ConnectToPeersAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<List<byte[]>> RequestBlocksByRootBatchAsync(
            List<byte[]> roots, string? preferredPeerKey, CancellationToken cancellationToken = default)
        {
            // Simulate a hung QUIC connection that never completes
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new List<byte[]>();
        }
    }
}
