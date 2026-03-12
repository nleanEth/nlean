using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public interface IStateRootIndexStore
{
    void Save(Bytes32 stateRoot, Bytes32 blockRoot);
    bool TryLoad(Bytes32 stateRoot, [NotNullWhen(true)] out Bytes32 blockRoot);
}
