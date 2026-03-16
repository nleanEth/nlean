using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public sealed record PendingBlock(
    Bytes32 Root, Bytes32 ParentRoot, ulong Slot, string? ReceivedFrom,
    SignedBlockWithAttestation? SignedBlock = null);

public sealed class NewBlockCache
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Dictionary<Bytes32, PendingBlock> _blocks = new();
    private readonly LinkedList<Bytes32> _insertionOrder = new();
    private readonly Dictionary<Bytes32, LinkedListNode<Bytes32>> _orderNodes = new();
    private readonly Dictionary<Bytes32, HashSet<Bytes32>> _childrenByParent = new();
    private readonly HashSet<Bytes32> _orphanParents = new();

    public NewBlockCache(int capacity = 1024)
    {
        _capacity = capacity;
    }

    public int Count { get { lock (_lock) return _blocks.Count; } }
    public int OrphanCount { get { lock (_lock) return _orphanParents.Count; } }

    public void Add(PendingBlock block)
    {
        lock (_lock)
        {
            if (_blocks.ContainsKey(block.Root))
                return;

            while (_blocks.Count >= _capacity)
                EvictOldest();

            _blocks[block.Root] = block;
            var node = _insertionOrder.AddLast(block.Root);
            _orderNodes[block.Root] = node;

            if (!_childrenByParent.TryGetValue(block.ParentRoot, out var siblings))
            {
                siblings = new HashSet<Bytes32>();
                _childrenByParent[block.ParentRoot] = siblings;
            }

            siblings.Add(block.Root);
        }
    }

    public bool TryGet(Bytes32 root, out PendingBlock? block)
    {
        lock (_lock) return _blocks.TryGetValue(root, out block);
    }

    public void Remove(Bytes32 root)
    {
        lock (_lock) RemoveCore(root);
    }

    public List<PendingBlock> GetChildren(Bytes32 parentRoot)
    {
        lock (_lock)
        {
            if (!_childrenByParent.TryGetValue(parentRoot, out var childRoots))
                return new List<PendingBlock>();

            var children = new List<PendingBlock>(childRoots.Count);
            foreach (var childRoot in childRoots)
            {
                if (_blocks.TryGetValue(childRoot, out var child))
                    children.Add(child);
            }

            children.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return children;
        }
    }

    public void MarkOrphan(Bytes32 parentRoot) { lock (_lock) _orphanParents.Add(parentRoot); }

    public void UnmarkOrphan(Bytes32 parentRoot) { lock (_lock) _orphanParents.Remove(parentRoot); }

    public List<Bytes32> GetOrphanParents() { lock (_lock) return new(_orphanParents); }

    public List<(Bytes32 ParentRoot, string? PreferredPeerId)> GetOrphanParentsWithHints()
    {
        lock (_lock)
        {
            var results = new List<(Bytes32 ParentRoot, string? PreferredPeerId)>(_orphanParents.Count);
            foreach (var parentRoot in _orphanParents)
            {
                string? preferredPeerId = null;
                if (_childrenByParent.TryGetValue(parentRoot, out var childRoots))
                {
                    foreach (var childRoot in childRoots)
                    {
                        if (_blocks.TryGetValue(childRoot, out var child) &&
                            !string.IsNullOrWhiteSpace(child.ReceivedFrom))
                        {
                            preferredPeerId = child.ReceivedFrom;
                            break;
                        }
                    }
                }

                results.Add((parentRoot, preferredPeerId));
            }

            return results;
        }
    }

    private void RemoveCore(Bytes32 root)
    {
        if (!_blocks.TryGetValue(root, out var block))
            return;

        _blocks.Remove(root);
        if (_orderNodes.TryGetValue(root, out var orderNode))
        {
            _insertionOrder.Remove(orderNode);
            _orderNodes.Remove(root);
        }

        if (_childrenByParent.TryGetValue(block.ParentRoot, out var siblings))
        {
            siblings.Remove(root);
            if (siblings.Count == 0)
            {
                _childrenByParent.Remove(block.ParentRoot);
                _orphanParents.Remove(block.ParentRoot);
            }
        }
    }

    private void EvictOldest()
    {
        var oldest = _insertionOrder.First;
        if (oldest is null) return;

        var root = oldest.Value;
        _insertionOrder.RemoveFirst();
        _orderNodes.Remove(root);

        if (_blocks.TryGetValue(root, out var block))
        {
            _blocks.Remove(root);
            if (_childrenByParent.TryGetValue(block.ParentRoot, out var siblings))
            {
                siblings.Remove(root);
                if (siblings.Count == 0)
                {
                    _childrenByParent.Remove(block.ParentRoot);
                    _orphanParents.Remove(block.ParentRoot);
                }
            }
        }
    }
}
