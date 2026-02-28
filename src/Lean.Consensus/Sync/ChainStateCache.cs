using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

/// <summary>
/// Shared cache of chain state snapshots keyed by block root.
/// Both <see cref="ConsensusServiceV2"/> and <see cref="ProtoArrayBlockProcessor"/>
/// read/write through this cache so gossip-processed blocks also have
/// chain state snapshots available for block production.
/// </summary>
public sealed class ChainStateCache
{
    /// <summary>
    /// Maximum number of cached states. When exceeded, the oldest entries by slot
    /// are evicted to bound memory growth during finalization stalls.
    /// </summary>
    public const int MaxCachedStates = 128;

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public int Count => _states.Count;

    public bool TryGet(string key, out State state) => _states.TryGetValue(key, out state!);

    public void Set(string key, State state)
    {
        _states[key] = state;
        EvictIfOverCapacity();
    }

    public void SetIfAbsent(string key, State state)
    {
        if (_states.TryAdd(key, state))
            EvictIfOverCapacity();
    }

    /// <summary>
    /// Removes all cached states except those whose roots are still in the proto-array.
    /// Called when finalization advances to release memory from pruned blocks.
    /// </summary>
    public void PruneExcept(ProtoArray protoArray)
    {
        var staleKeys = _states.Keys
            .Where(key => !protoArray.ContainsKey(key))
            .ToList();
        foreach (var key in staleKeys)
            _states.Remove(key);
    }

    private void EvictIfOverCapacity()
    {
        if (_states.Count <= MaxCachedStates)
            return;

        // Evict entries with the lowest slot values (oldest states).
        var toEvict = _states
            .OrderBy(kv => kv.Value.Slot.Value)
            .Take(_states.Count - MaxCachedStates)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in toEvict)
            _states.Remove(key);
    }

    public static string RootKey(Bytes32 root) => ProtoArray.RootKey(root);
}
