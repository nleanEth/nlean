using System.Diagnostics.CodeAnalysis;

namespace Lean.Consensus;

public interface IConsensusStateStore
{
    bool TryLoad([NotNullWhen(true)] out ConsensusHeadState? state);
    void Save(ConsensusHeadState state);
    void Delete();
}
