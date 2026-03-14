using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public interface IBlockByRootStore
{
    void Save(Bytes32 blockRoot, ReadOnlySpan<byte> payload);
    bool TryLoad(Bytes32 blockRoot, [NotNullWhen(true)] out byte[]? payload);
    void Delete(Bytes32 blockRoot);
}
