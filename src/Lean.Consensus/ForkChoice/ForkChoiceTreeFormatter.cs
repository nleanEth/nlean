using System;
using System.Collections.Generic;
using System.Text;
using Lean.Consensus.Types;

namespace Lean.Consensus.ForkChoice;

/// <summary>
/// Renders an ASCII art fork choice tree for terminal/log output.
/// Ported from ethlambda's fork_choice_tree.rs.
/// </summary>
public static class ForkChoiceTreeFormatter
{
    private const int MaxDisplayDepth = 20;

    public static string Format(
        IReadOnlyList<(Bytes32 Root, ulong Slot, Bytes32 ParentRoot, long Weight)> nodes,
        Bytes32 head,
        Bytes32 justifiedRoot, ulong justifiedSlot,
        Bytes32 finalizedRoot, ulong finalizedSlot,
        Bytes32 safeTarget)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("Fork Choice Tree:");
        sb.Append("  Finalized: slot ").Append(finalizedSlot)
            .Append(" | root ").AppendLine(ShortRoot(finalizedRoot));
        sb.Append("  Justified: slot ").Append(justifiedSlot)
            .Append(" | root ").AppendLine(ShortRoot(justifiedRoot));

        ulong headSlot = 0;
        foreach (var n in nodes)
        {
            if (n.Root.Equals(head))
            {
                headSlot = n.Slot;
                break;
            }
        }

        sb.Append("  Head:      slot ").Append(headSlot)
            .Append(" | root ").AppendLine(ShortRoot(head));

        if (nodes.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("  (empty)");
            return sb.ToString();
        }

        // Build lookup maps
        var blockMap = new Dictionary<Bytes32, (ulong Slot, Bytes32 ParentRoot, long Weight)>();
        foreach (var (root, slot, parentRoot, weight) in nodes)
        {
            blockMap[root] = (slot, parentRoot, weight);
        }

        // Build children map: parent -> children list
        var childrenMap = new Dictionary<Bytes32, List<Bytes32>>();
        foreach (var (root, slot, parentRoot, weight) in nodes)
        {
            if (!parentRoot.Equals(Bytes32.Zero()) && blockMap.ContainsKey(parentRoot))
            {
                if (!childrenMap.TryGetValue(parentRoot, out var children))
                {
                    children = new List<Bytes32>();
                    childrenMap[parentRoot] = children;
                }

                children.Add(root);
            }
        }

        // Sort children by weight descending, tiebreaker root hash descending
        foreach (var children in childrenMap.Values)
        {
            children.Sort((a, b) =>
            {
                long wa = blockMap[a].Weight;
                long wb = blockMap[b].Weight;
                int cmp = wb.CompareTo(wa);
                if (cmp != 0)
                {
                    return cmp;
                }

                return CompareBytes32Descending(a, b);
            });
        }

        // Find tree root (node whose parent is not in the map or is zero)
        var treeRoot = FindTreeRoot(nodes, blockMap);

        // Render linear trunk from root until a fork or leaf
        sb.AppendLine();
        sb.Append("  ");
        var (trunkTip, trunkDepth) = RenderTrunk(sb, treeRoot, blockMap, childrenMap, head);

        // Render branching subtree from the fork point
        if (childrenMap.TryGetValue(trunkTip, out var forkChildren) && forkChildren.Count > 1)
        {
            sb.Append(" \u2500 ").Append(forkChildren.Count).AppendLine(" branches");
            RenderBranches(sb, forkChildren, "  ", trunkDepth, blockMap, childrenMap, head);
        }
        else if (trunkTip.Equals(head))
        {
            sb.AppendLine(" *");
        }
        else
        {
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ShortRoot(Bytes32 root)
    {
        return Convert.ToHexString(root.AsSpan().Slice(0, 4));
    }

    private static string FormatNode(Bytes32 root, ulong slot)
    {
        return ShortRoot(root) + "..(" + slot + ")";
    }

    private static int CompareBytes32Descending(Bytes32 a, Bytes32 b)
    {
        var spanA = a.AsSpan();
        var spanB = b.AsSpan();
        for (int i = 0; i < 32; i++)
        {
            int cmp = spanB[i].CompareTo(spanA[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    private static Bytes32 FindTreeRoot(
        IReadOnlyList<(Bytes32 Root, ulong Slot, Bytes32 ParentRoot, long Weight)> nodes,
        Dictionary<Bytes32, (ulong Slot, Bytes32 ParentRoot, long Weight)> blockMap)
    {
        Bytes32 bestRoot = nodes[0].Root;
        ulong bestSlot = ulong.MaxValue;

        foreach (var (root, slot, parentRoot, _) in nodes)
        {
            if (parentRoot.Equals(Bytes32.Zero()) || !blockMap.ContainsKey(parentRoot))
            {
                if (slot < bestSlot)
                {
                    bestSlot = slot;
                    bestRoot = root;
                }
            }
        }

        return bestRoot;
    }

    /// <summary>
    /// Render the linear trunk (chain without forks) starting from root.
    /// Returns the last rendered node and current depth.
    /// </summary>
    private static (Bytes32 TrunkTip, int Depth) RenderTrunk(
        StringBuilder sb,
        Bytes32 root,
        Dictionary<Bytes32, (ulong Slot, Bytes32 ParentRoot, long Weight)> blockMap,
        Dictionary<Bytes32, List<Bytes32>> childrenMap,
        Bytes32 head)
    {
        var current = root;
        int depth = 0;
        ulong? prevSlot = null;

        while (true)
        {
            var (slot, _, _) = blockMap[current];

            // Insert missing slot indicators
            RenderGap(sb, prevSlot, slot, ref depth);

            // Render current node
            sb.Append(FormatNode(current, slot));
            depth++;

            if (depth >= MaxDisplayDepth)
            {
                sb.Append("\u2500\u2500 ...");
                return (current, depth);
            }

            if (childrenMap.TryGetValue(current, out var children) && children.Count == 1)
            {
                sb.Append("\u2500\u2500 ");
                prevSlot = slot;
                current = children[0];
            }
            else
            {
                // Fork point or leaf: stop trunk rendering
                return (current, depth);
            }
        }
    }

    /// <summary>
    /// Render branches from a fork point using tree connectors.
    /// </summary>
    private static void RenderBranches(
        StringBuilder sb,
        List<Bytes32> children,
        string prefix,
        int depth,
        Dictionary<Bytes32, (ulong Slot, Bytes32 ParentRoot, long Weight)> blockMap,
        Dictionary<Bytes32, List<Bytes32>> childrenMap,
        Bytes32 head)
    {
        for (int i = 0; i < children.Count; i++)
        {
            bool isLast = i == children.Count - 1;
            string connector = isLast ? "\u2514\u2500\u2500 " : "\u251c\u2500\u2500 ";
            string continuation = isLast ? "    " : "\u2502   ";

            sb.Append(prefix).Append(connector);
            RenderBranchLine(sb, children[i], prefix, continuation, depth, blockMap, childrenMap, head);
        }
    }

    /// <summary>
    /// Render a single branch line, following the chain until a fork or leaf.
    /// </summary>
    private static void RenderBranchLine(
        StringBuilder sb,
        Bytes32 start,
        string prefix,
        string continuation,
        int depth,
        Dictionary<Bytes32, (ulong Slot, Bytes32 ParentRoot, long Weight)> blockMap,
        Dictionary<Bytes32, List<Bytes32>> childrenMap,
        Bytes32 head)
    {
        var current = start;

        // Determine parent slot for gap calculation
        var (_, parentRoot, _) = blockMap[current];
        ulong? prevSlot = blockMap.ContainsKey(parentRoot) ? blockMap[parentRoot].Slot : null;

        while (true)
        {
            var (slot, _, _) = blockMap[current];

            // Insert missing slot indicators
            RenderGap(sb, prevSlot, slot, ref depth);

            bool isHead = current.Equals(head);
            sb.Append(FormatNode(current, slot));
            depth++;

            if (depth >= MaxDisplayDepth)
            {
                sb.AppendLine("\u2500\u2500 ...");
                return;
            }

            List<Bytes32>? nodeChildren = null;
            childrenMap.TryGetValue(current, out nodeChildren);
            int childCount = nodeChildren?.Count ?? 0;

            if (childCount == 0)
            {
                // Leaf node: show head marker and weight
                string headMarker = isHead ? " *" : "";
                long w = blockMap[current].Weight;
                sb.Append(headMarker).Append("  [w:").Append(w).AppendLine("]");
                return;
            }
            else if (childCount == 1)
            {
                // Continue linear chain
                if (isHead)
                {
                    sb.Append(" *");
                }

                sb.Append("\u2500\u2500 ");
                prevSlot = slot;
                current = nodeChildren![0];
            }
            else
            {
                // Sub-fork
                if (isHead)
                {
                    sb.Append(" *");
                }

                sb.Append(" \u2500 ").Append(childCount).AppendLine(" branches");
                string newPrefix = prefix + continuation;
                RenderBranches(sb, nodeChildren!, newPrefix, depth, blockMap, childrenMap, head);
                return;
            }
        }
    }

    /// <summary>
    /// Write missing-slot indicators between prevSlot and slot.
    /// </summary>
    private static void RenderGap(StringBuilder sb, ulong? prevSlot, ulong slot, ref int depth)
    {
        if (prevSlot.HasValue)
        {
            ulong gap = slot - prevSlot.Value;
            if (gap > 1)
            {
                ulong missing = gap - 1;
                if (missing == 1)
                {
                    sb.Append("[ ]\u2500\u2500 ");
                }
                else
                {
                    sb.Append('[').Append(missing).Append("]\u2500\u2500 ");
                }

                depth++;
            }
        }
    }
}
