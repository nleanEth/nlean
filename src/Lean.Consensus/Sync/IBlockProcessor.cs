using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public interface IBlockProcessor
{
    ulong HeadSlot { get; }
    bool IsBlockKnown(Bytes32 root);
    bool HasState(Bytes32 root);
    ForkChoiceApplyResult ProcessBlock(SignedBlock signedBlock);
}
