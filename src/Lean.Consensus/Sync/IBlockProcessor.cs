using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public interface IBlockProcessor
{
    bool IsBlockKnown(Bytes32 root);
    ForkChoiceApplyResult ProcessBlock(SignedBlockWithAttestation signedBlock);
}
