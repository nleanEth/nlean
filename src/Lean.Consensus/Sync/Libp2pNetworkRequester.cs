using Lean.Consensus.Types;
using Lean.Network;

namespace Lean.Consensus.Sync;

/// <summary>
/// Adapts INetworkService to the INetworkRequester interface used by BackfillSync.
/// Fetches blocks via the blocks-by-root RPC and decodes SSZ payloads.
/// Includes a hard deadline to guard against QUIC transport hangs where the
/// cancellation token is not respected by the underlying stream.
/// </summary>
public sealed class Libp2pNetworkRequester : INetworkRequester
{
    internal const int DefaultHardDeadlineMs = 35_000;

    private readonly INetworkService _network;
    private readonly int _hardDeadlineMs;
    private readonly SignedBlockWithAttestationGossipDecoder _decoder = new();

    public Libp2pNetworkRequester(INetworkService network, int hardDeadlineMs = DefaultHardDeadlineMs)
    {
        _network = network;
        _hardDeadlineMs = hardDeadlineMs;
    }

    public async Task<List<SignedBlockWithAttestation>> RequestBlocksByRootAsync(
        string peerId, List<Bytes32> roots, CancellationToken ct)
    {
        var rawRoots = roots.Select(r => r.AsSpan().ToArray()).ToList();

        // The QUIC transport may hang indefinitely when the underlying
        // QuicStream is disposed asynchronously (ObjectDisposedException in
        // a fire-and-forget task). CancellationToken propagation through
        // the libp2p stack is unreliable in this failure mode, so we add a
        // hard Task.WhenAny deadline to guarantee the call returns.
        var networkTask = _network.RequestBlocksByRootBatchAsync(rawRoots, peerId, ct);
        var completed = await Task.WhenAny(networkTask, Task.Delay(_hardDeadlineMs, ct));

        if (completed != networkTask)
            throw new OperationCanceledException("blocks-by-root hard deadline exceeded", ct);

        var batchResults = await networkTask; // propagate exceptions

        var results = new List<SignedBlockWithAttestation>();
        foreach (var payload in batchResults)
        {
            var decodeResult = _decoder.DecodeAndValidate(payload);
            if (decodeResult.IsSuccess && decodeResult.SignedBlock is not null)
                results.Add(decodeResult.SignedBlock);
        }

        return results;
    }
}
