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

    public async Task<CheckpointSyncResult> SyncFromCheckpointAsync(string url, CancellationToken ct)
    {
        var state = await _provider.FetchFinalizedStateAsync(url, ct);

        if (state is null)
            return CheckpointSyncResult.Failure("Failed to fetch finalized state from checkpoint provider.");

        var validationError = ValidateState(state);
        if (validationError is not null)
            return CheckpointSyncResult.Failure(validationError);

        return CheckpointSyncResult.Success(state);
    }

    private static string? ValidateState(State state)
    {
        if (state.Validators.Count == 0)
            return "Checkpoint state has no validators.";

        if (state.Validators.Count > ValidatorRegistryLimit)
            return $"Checkpoint state has {state.Validators.Count} validators, exceeding limit of {ValidatorRegistryLimit}.";

        return null;
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
