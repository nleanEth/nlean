using Lean.Consensus.Types;

namespace Lean.Consensus.ForkChoice;

public sealed class ProtoArray
{
    private readonly List<ProtoNode> _nodes = new();
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);

    public ProtoArray(Bytes32 genesisRoot, ulong justifiedSlot, ulong finalizedSlot)
    {
        var genesis = new ProtoNode(genesisRoot, Bytes32.Zero(), 0, null, justifiedSlot, finalizedSlot);
        _nodes.Add(genesis);
        _indices[RootKey(genesisRoot)] = 0;
    }

    public int NodeCount => _nodes.Count;

    public bool ContainsBlock(Bytes32 root) => _indices.ContainsKey(RootKey(root));

    public void RegisterBlock(Bytes32 root, Bytes32 parentRoot, ulong slot,
        ulong justifiedSlot, ulong finalizedSlot)
    {
        var key = RootKey(root);
        if (_indices.ContainsKey(key)) return;
        int? parentIndex = _indices.TryGetValue(RootKey(parentRoot), out var pi) ? pi : null;
        var node = new ProtoNode(root, parentRoot, slot, parentIndex, justifiedSlot, finalizedSlot);
        _indices[key] = _nodes.Count;
        _nodes.Add(node);
    }

    internal static string RootKey(Bytes32 root) => Convert.ToHexString(root.AsSpan());
}
