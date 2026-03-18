using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public sealed class CheckpointSync
{
    public const int ValidatorRegistryLimit = SszEncoding.ValidatorRegistryLimit;

    private readonly ICheckpointProvider _provider;

    public CheckpointSync(ICheckpointProvider provider)
    {
        _provider = provider;
    }

    public async Task<CheckpointSyncResult> SyncFromCheckpointAsync(
        string url, ConsensusConfig config, CancellationToken ct)
    {
        var state = await _provider.FetchFinalizedStateAsync(url, ct);

        if (state is null)
            return CheckpointSyncResult.Failure("Failed to fetch finalized state from checkpoint provider.");

        var normalizedState = NormalizeState(state);
        var validationError = ValidateState(normalizedState, config);
        if (validationError is not null)
            return CheckpointSyncResult.Failure(validationError);

        return CheckpointSyncResult.Success(normalizedState);
    }

    public static string? ValidateState(State state, ConsensusConfig config)
    {
        var structuralError = ValidateState(state);
        if (structuralError is not null)
            return structuralError;

        if (state.Config.GenesisTime != config.GenesisTimeUnix)
            return $"Genesis time mismatch: state has {state.Config.GenesisTime}, expected {config.GenesisTimeUnix}.";

        if (state.Slot.Value == 0)
            return "Checkpoint state slot must be > 0.";

        if (state.LatestFinalized.Slot.Value > state.Slot.Value)
            return $"Finalized slot {state.LatestFinalized.Slot.Value} exceeds state slot {state.Slot.Value}.";

        if (state.LatestJustified.Slot.Value < state.LatestFinalized.Slot.Value)
            return $"Justified slot {state.LatestJustified.Slot.Value} precedes finalized slot {state.LatestFinalized.Slot.Value}.";

        if (state.LatestBlockHeader.Slot.Value > state.Slot.Value)
            return $"Block header slot {state.LatestBlockHeader.Slot.Value} exceeds state slot {state.Slot.Value}.";

        if (config.GenesisValidatorPublicKeys.Count > 0)
        {
            if (state.Validators.Count != config.GenesisValidatorPublicKeys.Count)
                return $"Validator count mismatch: state has {state.Validators.Count}, expected {config.GenesisValidatorPublicKeys.Count}.";

            for (var i = 0; i < state.Validators.Count; i++)
            {
                var expectedHex = config.GenesisValidatorPublicKeys[i];
                var actualHex = Convert.ToHexString(state.Validators[i].Pubkey.AsSpan());
                if (!string.Equals(actualHex, NormalizeHex(expectedHex), StringComparison.OrdinalIgnoreCase))
                    return $"Validator pubkey mismatch at index {i}.";
            }
        }

        return null;
    }

    public static string? ValidateState(State state)
    {
        if (state.Validators.Count == 0)
            return "Checkpoint state has no validators.";

        if (state.Validators.Count > ValidatorRegistryLimit)
            return $"Checkpoint state has {state.Validators.Count} validators, exceeding limit of {ValidatorRegistryLimit}.";

        var normalizedState = NormalizeState(state);
        var headerStateRoot = normalizedState.LatestBlockHeader.StateRoot;
        var withZeroedRoot = normalizedState with
        {
            LatestBlockHeader = normalizedState.LatestBlockHeader with { StateRoot = Bytes32.Zero() }
        };
        var computedRoot = new Bytes32(withZeroedRoot.HashTreeRoot());
        if (!computedRoot.Equals(headerStateRoot))
            return $"State root mismatch: LatestBlockHeader.StateRoot={headerStateRoot} but computed={computedRoot}.";

        return null;
    }

    public static State NormalizeState(State state)
    {
        if (!state.LatestBlockHeader.StateRoot.Equals(Bytes32.Zero()))
            return state;

        var withZeroedRoot = state with
        {
            LatestBlockHeader = state.LatestBlockHeader with { StateRoot = Bytes32.Zero() }
        };
        var computedRoot = new Bytes32(withZeroedRoot.HashTreeRoot());
        return withZeroedRoot with
        {
            LatestBlockHeader = withZeroedRoot.LatestBlockHeader with { StateRoot = computedRoot }
        };
    }

    private static string NormalizeHex(string hex)
    {
        var trimmed = hex.AsSpan().Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        return trimmed.ToString().ToUpperInvariant();
    }
}

public sealed class CheckpointSyncResult
{
    private CheckpointSyncResult(bool succeeded, State? state, string? error)
    {
        Succeeded = succeeded;
        State = state;
        Error = error;
    }

    public bool Succeeded { get; }
    public State? State { get; }
    public string? Error { get; }

    public static CheckpointSyncResult Success(State state) => new(true, state, null);
    public static CheckpointSyncResult Failure(string error) => new(false, null, error);
}
