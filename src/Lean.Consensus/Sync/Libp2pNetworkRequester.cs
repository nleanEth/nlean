using Lean.Consensus.Types;
using Lean.Network;

namespace Lean.Consensus.Sync;

/// <summary>
/// Adapts INetworkService to the INetworkRequester interface used by BackfillSync.
/// Fetches blocks one-by-one via the existing blocks-by-root RPC and decodes SSZ payloads.
/// </summary>
public sealed class Libp2pNetworkRequester : INetworkRequester
{
    private readonly INetworkService _network;
    private readonly SignedBlockWithAttestationGossipDecoder _decoder = new();

    public Libp2pNetworkRequester(INetworkService network)
    {
        _network = network;
    }

    public async Task<List<SignedBlockWithAttestation>> RequestBlocksByRootAsync(
        string peerId, List<Bytes32> roots, CancellationToken ct)
    {
        var rawRoots = roots.Select(r => r.AsSpan().ToArray()).ToList();
        var batchResults = await _network.RequestBlocksByRootBatchAsync(rawRoots, peerId, ct);

        var results = new List<SignedBlockWithAttestation>();
        foreach (var (_, payload) in batchResults)
        {
            var decodeResult = _decoder.DecodeAndValidate(payload);
            if (decodeResult.IsSuccess && decodeResult.SignedBlock is not null)
                results.Add(decodeResult.SignedBlock);
        }

        return results;
    }
}
