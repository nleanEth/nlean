using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public interface INetworkRequester
{
    Task<List<SignedBlockWithAttestation>> RequestBlocksByRootAsync(
        string peerId, List<Bytes32> roots, CancellationToken ct);
}
