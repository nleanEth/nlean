using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public interface IConsensusStateStore
{
    bool TryLoad([NotNullWhen(true)] out ConsensusHeadState? state);
    bool TryLoad([NotNullWhen(true)] out ConsensusHeadState? state, out State? headChainState);
    void Save(ConsensusHeadState state);
    void Save(ConsensusHeadState state, State headChainState);
    void Delete();
}
