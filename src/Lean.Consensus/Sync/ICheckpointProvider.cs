using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public interface ICheckpointProvider
{
    Task<State?> FetchFinalizedStateAsync(string url, CancellationToken ct);

    // leanSpec PR #713: the finalized State alone isn't enough to build a
    // store — Store.create_store also needs the SignedBlock at
    // latest_finalized.root so BlocksByRoot listeners can serve the anchor.
    // Servers expose this at GET /lean/v0/blocks/finalized.
    Task<SignedBlock?> FetchFinalizedSignedBlockAsync(string url, CancellationToken ct);
}
