using System.Diagnostics.CodeAnalysis;
using Lean.Storage;

namespace Lean.Consensus;

public sealed class ConsensusStateStore : IConsensusStateStore
{
    private readonly IKeyValueStore _store;

    public ConsensusStateStore(IKeyValueStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public bool TryLoad([NotNullWhen(true)] out ConsensusHeadState? state)
    {
        var payload = _store.Get(StateKey);
        if (payload is null)
        {
            state = null;
            return false;
        }

        if (!ConsensusHeadState.TryDeserialize(payload, out state))
        {
            state = null;
            return false;
        }

        return true;
    }

    public void Save(ConsensusHeadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _store.Put(StateKey, state.Serialize());
    }

    public void Delete()
    {
        _store.Delete(StateKey);
    }

    private static ReadOnlySpan<byte> StateKey => "consensus:head_state"u8;
}
