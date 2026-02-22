using Lean.Consensus.Types;

namespace Lean.Consensus.ForkChoice;

public sealed class ProtoNode
{
    public ProtoNode(Bytes32 root, Bytes32 parentRoot, ulong slot,
        int? parentIndex, ulong justifiedSlot, ulong finalizedSlot)
    {
        Root = root;
        ParentRoot = parentRoot;
        Slot = slot;
        ParentIndex = parentIndex;
        JustifiedSlot = justifiedSlot;
        FinalizedSlot = finalizedSlot;
    }

    public Bytes32 Root { get; }
    public Bytes32 ParentRoot { get; }
    public ulong Slot { get; }
    public int? ParentIndex { get; set; }
    public ulong JustifiedSlot { get; set; }
    public ulong FinalizedSlot { get; set; }
    public long Weight { get; set; }
    public int? BestChild { get; set; }
    public int? BestDescendant { get; set; }
}
