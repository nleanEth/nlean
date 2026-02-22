using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public interface IBackfillTrigger
{
    void RequestBackfill(Bytes32 parentRoot);
}
