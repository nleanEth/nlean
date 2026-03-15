using Lean.Consensus.Types;

namespace Lean.Consensus.ForkChoice;

public sealed class ProtoArray
{
    private readonly List<ProtoNode> _nodes = new();
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);

    public ProtoArray(Bytes32 genesisRoot, ulong justifiedSlot, ulong finalizedSlot)
        : this(genesisRoot, slot: 0, justifiedSlot, finalizedSlot)
    {
    }

    public ProtoArray(Bytes32 genesisRoot, ulong slot, ulong justifiedSlot, ulong finalizedSlot)
    {
        var genesis = new ProtoNode(genesisRoot, Bytes32.Zero(), slot, null, justifiedSlot, finalizedSlot);
        _nodes.Add(genesis);
        _indices[RootKey(genesisRoot)] = 0;
    }

    public int NodeCount => _nodes.Count;

    public bool ContainsBlock(Bytes32 root) => _indices.ContainsKey(RootKey(root));
    public bool ContainsKey(string rootKey) => _indices.ContainsKey(rootKey);
    public HashSet<string> GetAllKeys() => new(_indices.Keys, StringComparer.Ordinal);

    public int? GetIndex(Bytes32 root)
    {
        if (_indices.TryGetValue(RootKey(root), out var idx))
            return idx;
        return null;
    }

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

    public long GetWeight(Bytes32 root)
    {
        if (_indices.TryGetValue(RootKey(root), out var idx))
            return _nodes[idx].Weight;
        return 0;
    }

    public ProtoNode? GetNode(Bytes32 root)
    {
        if (_indices.TryGetValue(RootKey(root), out var idx))
            return _nodes[idx];
        return null;
    }

    public ProtoNode? GetNodeByIndex(int index)
    {
        if (index >= 0 && index < _nodes.Count)
            return _nodes[index];
        return null;
    }

    /// <summary>
    /// Zeam-style two-pass delta application.
    /// Pass 1: apply deltas to node weights and propagate to parents.
    /// Pass 2: recompute BestChild/BestDescendant with cutoffWeight filter.
    /// Tie-break: weight → slot → root (lexicographic).
    /// </summary>
    public void ApplyDeltas(long[] deltas, long cutoffWeight)
    {
        // Pass 1: backward — apply deltas and propagate to parents
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            var nodeDelta = deltas[i];
            _nodes[i].Weight += nodeDelta;

            if (_nodes[i].ParentIndex is { } parentIdx)
                deltas[parentIdx] += nodeDelta;
        }

        // Pass 2: backward — recompute bestChild/bestDescendant
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            if (_nodes[i].ParentIndex is not { } parentIdx)
                continue;
            if (parentIdx < 0 || parentIdx >= _nodes.Count)
                continue;

            var node = _nodes[i];
            var parent = _nodes[parentIdx];

            // This node's best descendant candidate: its own bestDescendant,
            // or itself if it meets the cutoff weight threshold.
            int? nodeBestDescendant = node.BestDescendant
                ?? (node.Weight >= cutoffWeight ? i : null);

            if (parent.BestChild == i)
            {
                // Same best child — update bestDescendant if changed
                if (parent.BestDescendant != nodeBestDescendant)
                    parent.BestDescendant = nodeBestDescendant;
            }
            else if (parent.BestChild is { } currentBestIdx)
            {
                var currentBest = _nodes[currentBestIdx];

                // Compare: weight → slot → root
                if (node.Weight > currentBest.Weight ||
                    (node.Weight == currentBest.Weight && node.Slot > currentBest.Slot) ||
                    (node.Weight == currentBest.Weight && node.Slot == currentBest.Slot &&
                     string.Compare(RootKey(node.Root), RootKey(currentBest.Root),
                         StringComparison.Ordinal) > 0))
                {
                    parent.BestChild = i;
                    parent.BestDescendant = nodeBestDescendant;
                }
            }
            else
            {
                // No existing best child
                parent.BestChild = i;
                parent.BestDescendant = nodeBestDescendant;
            }
        }
    }

    public ulong? GetSlot(Bytes32 root)
    {
        if (_indices.TryGetValue(RootKey(root), out var idx))
            return _nodes[idx].Slot;
        return null;
    }

    public Bytes32? GetParentRoot(Bytes32 root)
    {
        if (_indices.TryGetValue(RootKey(root), out var idx))
            return _nodes[idx].ParentRoot;
        return null;
    }

    /// <summary>
    /// Removes all nodes that are not descendants of the finalized root.
    /// Returns old→new index mapping for callers to remap AttestationTracker indices.
    /// </summary>
    public Dictionary<int, int> Prune(Bytes32 finalizedRoot)
    {
        var emptyMapping = new Dictionary<int, int>();

        if (!_indices.TryGetValue(RootKey(finalizedRoot), out var finalizedIdx))
            return emptyMapping;

        if (finalizedIdx == 0) return emptyMapping;

        // Collect indices of nodes to keep: finalizedIdx and all descendants
        var keepSet = new HashSet<int>();
        for (int i = finalizedIdx; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (i == finalizedIdx || (node.ParentIndex is { } pi && keepSet.Contains(pi)))
                keepSet.Add(i);
        }

        // Build new lists preserving order
        var newNodes = new List<ProtoNode>(keepSet.Count);
        var oldToNew = new Dictionary<int, int>();

        foreach (var oldIdx in keepSet.OrderBy(x => x))
        {
            oldToNew[oldIdx] = newNodes.Count;
            newNodes.Add(_nodes[oldIdx]);
        }

        // Remap parent indices and clear stale BestChild/BestDescendant
        foreach (var node in newNodes)
        {
            if (node.ParentIndex is { } pi && oldToNew.TryGetValue(pi, out var newPi))
                node.ParentIndex = newPi;
            else
                node.ParentIndex = null;

            node.BestChild = null;
            node.BestDescendant = null;
        }

        // Replace internal state
        _nodes.Clear();
        _nodes.AddRange(newNodes);
        _indices.Clear();
        for (int i = 0; i < _nodes.Count; i++)
            _indices[RootKey(_nodes[i].Root)] = i;

        return oldToNew;
    }

    /// <summary>
    /// Returns all blocks as (root, slot, parentRoot) tuples.
    /// </summary>
    public IEnumerable<(Bytes32 Root, ulong Slot, Bytes32 ParentRoot)> GetAllBlocks()
    {
        foreach (var node in _nodes)
            yield return (node.Root, node.Slot, node.ParentRoot);
    }

    // TODO: Replace hex-string dictionary keys with Bytes32 directly.
    // Bytes32 implements IEquatable<Bytes32> and GetHashCode (4× int32 from positions 0,8,16,24),
    // so it can serve as a dictionary key without the per-call hex allocation.
    // This touches 50+ call sites across ProtoArray, ProtoArrayForkChoiceStore, ChainStateCache,
    // ConsensusServiceV2, and tests — best done as a dedicated refactoring PR.
    public static string RootKey(Bytes32 root) => Convert.ToHexString(root.AsSpan());
}
