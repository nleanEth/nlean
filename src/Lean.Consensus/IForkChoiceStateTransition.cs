using Lean.Consensus.Types;

namespace Lean.Consensus;

public interface IForkChoiceStateTransition
{
    bool TryTransition(
        ForkChoiceNodeState parentState,
        SignedBlockWithAttestation signedBlock,
        out ForkChoiceNodeState postState,
        out string reason);
}
