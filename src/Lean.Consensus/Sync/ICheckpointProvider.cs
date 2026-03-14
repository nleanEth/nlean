using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public interface ICheckpointProvider
{
    Task<State?> FetchFinalizedStateAsync(string url, CancellationToken ct);
}
