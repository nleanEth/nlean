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

        // leanSpec PR #713: also fetch the SignedBlock at latest_finalized.root
        // so we can seed BlockStore for downstream BlocksByRoot listeners.
        // Best-effort against pre-#713 servers: a 404 / network error drops
        // the block but lets sync succeed — the syncing node simply won't be
        // able to serve the anchor root via BlocksByRoot until a peer pushes
        // a fresh block referencing it as parent.
        SignedBlock? anchorBlock = null;
        var blockUrl = DeriveAnchorBlockUrl(url);
        if (blockUrl is not null)
        {
            anchorBlock = await _provider.FetchFinalizedSignedBlockAsync(blockUrl, ct);
            if (anchorBlock is not null)
            {
                // Spec contract (leanSpec lstar/spec.py:906):
                //   assert anchor_block.state_root == hash_tree_root(anchor_state)
                // The proposer computed block.state_root over a state where
                // `latest_block_header.state_root` was still ZERO (process_slot
                // only fills it when the NEXT slot is processed). The state
                // store served at /lean/v0/states/finalized retains that zero
                // — we must NOT pre-normalize before hashing, or we'd hash a
                // state shape the proposer never saw. Use the raw fetched
                // state for the pairing assertion; reserve NormalizeState for
                // downstream consumers that need a non-zero header root.
                var rawStateRoot = new Bytes32(state.HashTreeRoot());
                if (!anchorBlock.Block.StateRoot.Equals(rawStateRoot))
                {
                    return CheckpointSyncResult.Failure(
                        $"Anchor block / state mismatch: signed_block.block.state_root=0x{Convert.ToHexString(anchorBlock.Block.StateRoot.AsSpan())} hash_tree_root(state)=0x{Convert.ToHexString(rawStateRoot.AsSpan())}. " +
                        "Server may have advanced finalization between requests; retry.");
                }
            }
        }

        return CheckpointSyncResult.Success(normalizedState, anchorBlock);
    }

    /// <summary>
    /// Hive and lean-quickstart hand us the full state-endpoint URL
    /// (`http://host:port/lean/v0/states/finalized`). Strip the suffix to
    /// rebuild the sibling block endpoint without forcing every caller to
    /// pass a base URL. Returns null when the URL doesn't look like the
    /// state endpoint we expected.
    /// </summary>
    internal static string? DeriveAnchorBlockUrl(string stateUrl)
    {
        const string StateSuffix = "/lean/v0/states/finalized";
        const string BlockSuffix = "/lean/v0/blocks/finalized";
        if (string.IsNullOrWhiteSpace(stateUrl))
            return null;
        var trimmed = stateUrl.TrimEnd('/');
        if (trimmed.EndsWith(StateSuffix, StringComparison.OrdinalIgnoreCase))
            return trimmed[..^StateSuffix.Length] + BlockSuffix;
        return null;
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

        if (config.GenesisValidatorKeys.Count > 0)
        {
            if (state.Validators.Count != config.GenesisValidatorKeys.Count)
                return $"Validator count mismatch: state has {state.Validators.Count}, expected {config.GenesisValidatorKeys.Count}.";

            for (var i = 0; i < state.Validators.Count; i++)
            {
                var (expectedAttestHex, expectedProposalHex) = config.GenesisValidatorKeys[i];
                var actualAttestHex = Convert.ToHexString(state.Validators[i].AttestationPubkey.AsSpan());
                if (!string.Equals(actualAttestHex, NormalizeHex(expectedAttestHex), StringComparison.OrdinalIgnoreCase))
                    return $"Validator attestation pubkey mismatch at index {i}.";

                var actualProposalHex = Convert.ToHexString(state.Validators[i].ProposalPubkey.AsSpan());
                if (!string.Equals(actualProposalHex, NormalizeHex(expectedProposalHex), StringComparison.OrdinalIgnoreCase))
                    return $"Validator proposal pubkey mismatch at index {i}.";
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
    private CheckpointSyncResult(bool succeeded, State? state, SignedBlock? anchorBlock, string? error)
    {
        Succeeded = succeeded;
        State = state;
        AnchorBlock = anchorBlock;
        Error = error;
    }

    public bool Succeeded { get; }
    public State? State { get; }

    // Populated when the source server speaks /lean/v0/blocks/finalized
    // (leanSpec PR #713). Null is acceptable — checkpoint sync still
    // succeeds, BlockStore just won't have the anchor entry until a peer
    // forwards a block that references the anchor root.
    public SignedBlock? AnchorBlock { get; }
    public string? Error { get; }

    public static CheckpointSyncResult Success(State state, SignedBlock? anchorBlock = null) =>
        new(true, state, anchorBlock, null);
    public static CheckpointSyncResult Failure(string error) => new(false, null, null, error);
}
