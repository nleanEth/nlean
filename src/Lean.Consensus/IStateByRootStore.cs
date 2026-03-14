using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public interface IStateByRootStore
{
    void Save(Bytes32 blockRoot, State state);
    bool TryLoad(Bytes32 blockRoot, [NotNullWhen(true)] out State? state);
    void Delete(Bytes32 blockRoot);
}
