using System.Diagnostics.CodeAnalysis;
using Lean.Consensus.Types;
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

    public bool TryLoad([NotNullWhen(true)] out ConsensusHeadState? state, out State? headChainState)
    {
        headChainState = null;
        if (!TryLoad(out state))
        {
            return false;
        }

        var payload = _store.Get(HeadChainStateKey);
        if (payload is null)
        {
            return true;
        }

        if (!TryDeserializeHeadChainState(payload, out headChainState))
        {
            _store.Delete(HeadChainStateKey);
            headChainState = null;
        }

        return true;
    }

    public void Save(ConsensusHeadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var batch = _store.StartBatch();
        batch.Put(StateKey, state.Serialize());
        batch.Delete(HeadChainStateKey);
        batch.Commit();
    }

    public void Save(ConsensusHeadState state, State headChainState)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(headChainState);
        using var batch = _store.StartBatch();
        batch.Put(StateKey, state.Serialize());
        batch.Put(HeadChainStateKey, SszEncoding.Encode(headChainState));
        batch.Commit();
    }

    public void Delete()
    {
        using var batch = _store.StartBatch();
        batch.Delete(StateKey);
        batch.Delete(HeadChainStateKey);
        batch.Commit();
    }

    private static bool TryDeserializeHeadChainState(ReadOnlySpan<byte> payload, out State? headChainState)
    {
        headChainState = null;
        try
        {
            headChainState = SszDecoding.DecodeState(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ReadOnlySpan<byte> StateKey => "consensus:head_state"u8;
    private static ReadOnlySpan<byte> HeadChainStateKey => "consensus:head_chain_state"u8;
}
