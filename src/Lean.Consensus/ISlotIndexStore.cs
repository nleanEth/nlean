using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public interface ISlotIndexStore
{
    void Save(ulong slot, Bytes32 blockRoot);
    bool TryLoad(ulong slot, [NotNullWhen(true)] out Bytes32 blockRoot);
    void DeleteBelow(ulong cutoffSlot);
    IReadOnlyList<(ulong Slot, Bytes32 Root)> GetEntriesBelow(ulong cutoffSlot);
}
