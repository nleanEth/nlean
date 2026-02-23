using Lean.Consensus.Types;

namespace Lean.Consensus.ForkChoice;

public sealed class ProtoArray
{
    private readonly List<ProtoNode> _nodes = new();
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    // Separate self-weight tracking: only direct votes, not propagated tree weight
    private readonly List<long> _selfWeights = new();

    public ProtoArray(Bytes32 genesisRoot, ulong justifiedSlot, ulong finalizedSlot)
    {
        var genesis = new ProtoNode(genesisRoot, Bytes32.Zero(), 0, null, justifiedSlot, finalizedSlot);
        _nodes.Add(genesis);
        _selfWeights.Add(0);
        _indices[RootKey(genesisRoot)] = 0;
    }

    public int NodeCount => _nodes.Count;

    public bool ContainsBlock(Bytes32 root) => _indices.ContainsKey(RootKey(root));
    public bool ContainsKey(string rootKey) => _indices.ContainsKey(rootKey);

    public void RegisterBlock(Bytes32 root, Bytes32 parentRoot, ulong slot,
        ulong justifiedSlot, ulong finalizedSlot)
    {
        var key = RootKey(root);
        if (_indices.ContainsKey(key)) return;
        int? parentIndex = _indices.TryGetValue(RootKey(parentRoot), out var pi) ? pi : null;
        var node = new ProtoNode(root, parentRoot, slot, parentIndex, justifiedSlot, finalizedSlot);
        _indices[key] = _nodes.Count;
        _nodes.Add(node);
        _selfWeights.Add(0);
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
    /// Applies vote deltas to self-weights and recomputes tree weights bottom-up.
    /// Updates BestChild/BestDescendant pointers for O(1) head lookups.
    /// </summary>
    public void ApplyScoreChanges(Dictionary<string, long> deltas,
        ulong justifiedSlot, ulong finalizedSlot)
    {
        // Apply deltas to self-weights
        foreach (var (key, delta) in deltas)
        {
            if (_indices.TryGetValue(key, out var idx))
                _selfWeights[idx] += delta;
        }

        // Reset tree weights to self-weights
        for (int i = 0; i < _nodes.Count; i++)
        {
            _nodes[i].Weight = _selfWeights[i];
            _nodes[i].BestChild = null;
            _nodes[i].BestDescendant = null;
        }

        // Bottom-up pass: propagate weights and update best child/descendant
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            var node = _nodes[i];
            if (node.ParentIndex is not { } parentIdx) continue;
            if (parentIdx < 0 || parentIdx >= _nodes.Count) continue;

            var parent = _nodes[parentIdx];
            parent.Weight += node.Weight;

            UpdateBestChildAndDescendant(parentIdx, i, justifiedSlot, finalizedSlot);
        }
    }

    private void UpdateBestChildAndDescendant(int parentIdx, int childIdx,
        ulong justifiedSlot, ulong finalizedSlot)
    {
        var parent = _nodes[parentIdx];
        var child = _nodes[childIdx];
        bool childViable = IsViable(child, justifiedSlot, finalizedSlot);

        if (parent.BestChild is { } currentBestIdx)
        {
            var currentBest = _nodes[currentBestIdx];
            bool currentViable = IsViable(currentBest, justifiedSlot, finalizedSlot);

            if (!childViable) return;

            if (!currentViable)
            {
                parent.BestChild = childIdx;
                parent.BestDescendant = child.BestDescendant ?? childIdx;
                return;
            }

            // Both viable — pick heavier, tie-break by higher hex root
            if (child.Weight > currentBest.Weight ||
                (child.Weight == currentBest.Weight &&
                 string.Compare(RootKey(child.Root), RootKey(currentBest.Root),
                     StringComparison.Ordinal) > 0))
            {
                parent.BestChild = childIdx;
                parent.BestDescendant = child.BestDescendant ?? childIdx;
            }
        }
        else
        {
            if (childViable)
            {
                parent.BestChild = childIdx;
                parent.BestDescendant = child.BestDescendant ?? childIdx;
            }
        }
    }

    /// <summary>
    /// Returns the head block root by following the BestDescendant chain from the justified root.
    /// Must be called after ApplyScoreChanges to get correct results.
    /// </summary>
    public Bytes32 FindHead(Bytes32 justifiedRoot, ulong justifiedSlot, ulong finalizedSlot)
    {
        if (!_indices.TryGetValue(RootKey(justifiedRoot), out var justifiedIdx))
            return default;

        var justified = _nodes[justifiedIdx];

        if (justified.BestDescendant is { } bestDescIdx)
        {
            var bestDesc = _nodes[bestDescIdx];
            if (IsViable(bestDesc, justifiedSlot, finalizedSlot))
                return bestDesc.Root;
        }

        return justified.Root;
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
    /// Rebuilds the index mapping after removal.
    /// </summary>
    public void Prune(Bytes32 finalizedRoot)
    {
        if (!_indices.TryGetValue(RootKey(finalizedRoot), out var finalizedIdx))
            return;

        if (finalizedIdx == 0) return; // Nothing to prune

        // Collect indices of nodes to keep: finalizedIdx and all descendants
        var keepSet = new HashSet<int>();
        for (int i = finalizedIdx; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            // Keep if it IS the finalized node, or its parent is in the keep set
            if (i == finalizedIdx || (node.ParentIndex is { } pi && keepSet.Contains(pi)))
                keepSet.Add(i);
        }

        // Build new lists preserving order
        var newNodes = new List<ProtoNode>(keepSet.Count);
        var newSelfWeights = new List<long>(keepSet.Count);
        var oldToNew = new Dictionary<int, int>();

        foreach (var oldIdx in keepSet.OrderBy(x => x))
        {
            oldToNew[oldIdx] = newNodes.Count;
            newNodes.Add(_nodes[oldIdx]);
            newSelfWeights.Add(_selfWeights[oldIdx]);
        }

        // Remap parent indices and clear stale BestChild/BestDescendant
        foreach (var node in newNodes)
        {
            if (node.ParentIndex is { } pi && oldToNew.TryGetValue(pi, out var newPi))
                node.ParentIndex = newPi;
            else
                node.ParentIndex = null; // finalized root has no parent

            // BestChild/BestDescendant are index-based and become stale after pruning.
            // Clear them so ApplyScoreChanges recomputes them correctly.
            node.BestChild = null;
            node.BestDescendant = null;
        }

        // Replace internal state
        _nodes.Clear();
        _nodes.AddRange(newNodes);
        _selfWeights.Clear();
        _selfWeights.AddRange(newSelfWeights);
        _indices.Clear();
        for (int i = 0; i < _nodes.Count; i++)
            _indices[RootKey(_nodes[i].Root)] = i;
    }

    private static bool IsViable(ProtoNode node, ulong justifiedSlot, ulong finalizedSlot)
    {
        // A node is viable if its checkpoints are at least as recent as the store's.
        // Both conditions must hold (AND), matching Lighthouse/Prysm behavior.
        return node.JustifiedSlot >= justifiedSlot && node.FinalizedSlot >= finalizedSlot;
    }

    public static string RootKey(Bytes32 root) => Convert.ToHexString(root.AsSpan());
}
