using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
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
        batch.Put(HeadChainStateKey, JsonSerializer.SerializeToUtf8Bytes(StateSnapshotPayload.FromState(headChainState)));
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
            var snapshot = JsonSerializer.Deserialize<StateSnapshotPayload>(payload);
            if (snapshot is null)
            {
                return false;
            }

            return snapshot.TryToState(out headChainState);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryCreateBytes32(byte[]? payload, out Bytes32 bytes)
    {
        bytes = Bytes32.Zero();
        if (payload is null || payload.Length != SszEncoding.Bytes32Length)
        {
            return false;
        }

        bytes = new Bytes32(payload);
        return true;
    }

    private static bool TryCreateBytes52(byte[]? payload, out Bytes52 bytes)
    {
        bytes = Bytes52.Zero();
        if (payload is null || payload.Length != SszEncoding.Bytes52Length)
        {
            return false;
        }

        bytes = new Bytes52(payload);
        return true;
    }

    private static ReadOnlySpan<byte> StateKey => "consensus:head_state"u8;
    private static ReadOnlySpan<byte> HeadChainStateKey => "consensus:head_chain_state"u8;

    private sealed record ValidatorSnapshotPayload(byte[] Pubkey, ulong Index);

    private sealed record StateSnapshotPayload(
        ulong GenesisTime,
        ulong Slot,
        ulong LatestBlockHeaderSlot,
        ulong LatestBlockHeaderProposerIndex,
        byte[] LatestBlockHeaderParentRoot,
        byte[] LatestBlockHeaderStateRoot,
        byte[] LatestBlockHeaderBodyRoot,
        ulong LatestJustifiedSlot,
        byte[] LatestJustifiedRoot,
        ulong LatestFinalizedSlot,
        byte[] LatestFinalizedRoot,
        byte[][] HistoricalBlockHashes,
        bool[] JustifiedSlots,
        ValidatorSnapshotPayload[] Validators,
        byte[][] JustificationsRoots,
        bool[] JustificationsValidators)
    {
        public static StateSnapshotPayload FromState(State state)
        {
            return new StateSnapshotPayload(
                state.Config.GenesisTime,
                state.Slot.Value,
                state.LatestBlockHeader.Slot.Value,
                state.LatestBlockHeader.ProposerIndex,
                state.LatestBlockHeader.ParentRoot.AsSpan().ToArray(),
                state.LatestBlockHeader.StateRoot.AsSpan().ToArray(),
                state.LatestBlockHeader.BodyRoot.AsSpan().ToArray(),
                state.LatestJustified.Slot.Value,
                state.LatestJustified.Root.AsSpan().ToArray(),
                state.LatestFinalized.Slot.Value,
                state.LatestFinalized.Root.AsSpan().ToArray(),
                state.HistoricalBlockHashes.Select(hash => hash.AsSpan().ToArray()).ToArray(),
                state.JustifiedSlots.ToArray(),
                state.Validators.Select(validator => new ValidatorSnapshotPayload(validator.Pubkey.AsSpan().ToArray(), validator.Index)).ToArray(),
                state.JustificationsRoots.Select(root => root.AsSpan().ToArray()).ToArray(),
                state.JustificationsValidators.ToArray());
        }

        public bool TryToState([NotNullWhen(true)] out State? state)
        {
            state = null;

            if (HistoricalBlockHashes is null ||
                JustifiedSlots is null ||
                Validators is null ||
                JustificationsRoots is null ||
                JustificationsValidators is null)
            {
                return false;
            }

            if (!TryCreateBytes32(LatestBlockHeaderParentRoot, out var latestHeaderParentRoot) ||
                !TryCreateBytes32(LatestBlockHeaderStateRoot, out var latestHeaderStateRoot) ||
                !TryCreateBytes32(LatestBlockHeaderBodyRoot, out var latestHeaderBodyRoot) ||
                !TryCreateBytes32(LatestJustifiedRoot, out var latestJustifiedRoot) ||
                !TryCreateBytes32(LatestFinalizedRoot, out var latestFinalizedRoot))
            {
                return false;
            }

            var historicalBlockHashes = new List<Bytes32>(HistoricalBlockHashes.Length);
            foreach (var rootPayload in HistoricalBlockHashes)
            {
                if (!TryCreateBytes32(rootPayload, out var root))
                {
                    return false;
                }

                historicalBlockHashes.Add(root);
            }

            var validators = new List<Validator>(Validators.Length);
            foreach (var validator in Validators)
            {
                if (validator is null || !TryCreateBytes52(validator.Pubkey, out var pubkey))
                {
                    return false;
                }

                validators.Add(new Validator(pubkey, validator.Index));
            }

            var justificationsRoots = new List<Bytes32>(JustificationsRoots.Length);
            foreach (var rootPayload in JustificationsRoots)
            {
                if (!TryCreateBytes32(rootPayload, out var root))
                {
                    return false;
                }

                justificationsRoots.Add(root);
            }

            state = new State(
                new Config(GenesisTime),
                new Slot(Slot),
                new BlockHeader(
                    new Slot(LatestBlockHeaderSlot),
                    LatestBlockHeaderProposerIndex,
                    latestHeaderParentRoot,
                    latestHeaderStateRoot,
                    latestHeaderBodyRoot),
                new Checkpoint(latestJustifiedRoot, new Slot(LatestJustifiedSlot)),
                new Checkpoint(latestFinalizedRoot, new Slot(LatestFinalizedSlot)),
                historicalBlockHashes,
                JustifiedSlots,
                validators,
                justificationsRoots,
                JustificationsValidators);
            return true;
        }
    }
}
