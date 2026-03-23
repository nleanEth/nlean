using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Sync;

[TestFixture]
public sealed class CheckpointSyncTests
{
    [Test]
    public async Task SyncFromCheckpoint_ValidState_Succeeds()
    {
        var state = MakeValidStateWithCorrectRoot(validatorCount: 4);
        var config = MakeMatchingConfig(state);
        var provider = new FakeCheckpointProvider(state);
        var sync = new CheckpointSync(provider);

        var result = await sync.SyncFromCheckpointAsync("http://example.com", config, CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.State, Is.Not.Null);
        Assert.That(result.State!.Validators.Count, Is.EqualTo(4));
    }

    [Test]
    public async Task SyncFromCheckpoint_NullResponse_Fails()
    {
        var provider = new FakeCheckpointProvider(null);
        var sync = new CheckpointSync(provider);
        var config = new ConsensusConfig { GenesisTimeUnix = 1000 };

        var result = await sync.SyncFromCheckpointAsync("http://example.com", config, CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("Failed to fetch"));
    }

    [Test]
    public void ValidateState_NoValidators_Fails()
    {
        var state = MakeValidStateWithCorrectRoot(validatorCount: 0);
        var error = CheckpointSync.ValidateState(state);
        Assert.That(error, Does.Contain("no validators"));
    }

    [Test]
    public void ValidateState_TooManyValidators_Fails()
    {
        var state = MakeStateWithZeroedRoot(validatorCount: CheckpointSync.ValidatorRegistryLimit + 1);
        var error = CheckpointSync.ValidateState(state);
        Assert.That(error, Does.Contain("exceeding limit"));
    }

    [Test]
    public async Task SyncFromCheckpoint_ZeroedStateRoot_IsNormalizedAndSucceeds()
    {
        var state = MakeStateWithZeroedRoot(validatorCount: 4);
        var config = MakeMatchingConfig(state);
        var provider = new FakeCheckpointProvider(state);
        var sync = new CheckpointSync(provider);

        var result = await sync.SyncFromCheckpointAsync("http://example.com", config, CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.State, Is.Not.Null);
        Assert.That(result.State!.LatestBlockHeader.StateRoot, Is.Not.EqualTo(Bytes32.Zero()));

        var withZeroedRoot = result.State with
        {
            LatestBlockHeader = result.State.LatestBlockHeader with { StateRoot = Bytes32.Zero() }
        };
        var expectedRoot = new Bytes32(withZeroedRoot.HashTreeRoot());
        Assert.That(result.State.LatestBlockHeader.StateRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public void ValidateState_CorruptedStateRoot_Fails()
    {
        var state = MakeValidStateWithCorrectRoot(validatorCount: 4);
        var corrupted = state with
        {
            LatestBlockHeader = state.LatestBlockHeader with
            {
                StateRoot = new Bytes32(Enumerable.Repeat((byte)0xFF, 32).ToArray())
            }
        };
        var error = CheckpointSync.ValidateState(corrupted);
        Assert.That(error, Does.Contain("State root mismatch"));
    }

    [Test]
    public void ValidateState_CorrectStateRoot_Passes()
    {
        var state = MakeValidStateWithCorrectRoot(validatorCount: 4);
        var error = CheckpointSync.ValidateState(state);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void ValidateStateWithConfig_GenesisTimeMismatch_Fails()
    {
        var state = MakeValidStateWithCorrectRoot(validatorCount: 4);
        var config = MakeMatchingConfig(state);
        config.GenesisTimeUnix = 9999;
        var error = CheckpointSync.ValidateState(state, config);
        Assert.That(error, Does.Contain("Genesis time mismatch"));
    }

    [Test]
    public void ValidateStateWithConfig_SlotZero_Fails()
    {
        var state = MakeStateWithZeroedRoot(validatorCount: 4) with { Slot = new Slot(0) };
        state = FillStateRoot(state);
        var config = MakeMatchingConfig(state);
        var error = CheckpointSync.ValidateState(state, config);
        Assert.That(error, Does.Contain("slot must be > 0"));
    }

    [Test]
    public void ValidateStateWithConfig_FinalizedExceedsState_Fails()
    {
        var state = MakeStateWithZeroedRoot(validatorCount: 4) with
        {
            LatestFinalized = new Checkpoint(Bytes32.Zero(), new Slot(999))
        };
        state = FillStateRoot(state);
        var config = MakeMatchingConfig(state);
        var error = CheckpointSync.ValidateState(state, config);
        Assert.That(error, Does.Contain("exceeds state slot"));
    }

    [Test]
    public void ValidateStateWithConfig_JustifiedPrecedesFinalized_Fails()
    {
        var state = MakeStateWithZeroedRoot(validatorCount: 4) with
        {
            LatestJustified = new Checkpoint(Bytes32.Zero(), new Slot(1)),
            LatestFinalized = new Checkpoint(Bytes32.Zero(), new Slot(50))
        };
        state = FillStateRoot(state);
        var config = MakeMatchingConfig(state);
        var error = CheckpointSync.ValidateState(state, config);
        Assert.That(error, Does.Contain("precedes finalized"));
    }

    [Test]
    public void ValidateStateWithConfig_ValidatorCountMismatch_Fails()
    {
        var state = MakeValidStateWithCorrectRoot(validatorCount: 4);
        var config = MakeMatchingConfig(state);
        config.GenesisValidatorKeys = new[] { ("00", "00") };
        var error = CheckpointSync.ValidateState(state, config);
        Assert.That(error, Does.Contain("Validator count mismatch"));
    }

    [Test]
    public void ValidateStateWithConfig_ValidatorPubkeyMismatch_Fails()
    {
        var state = MakeValidStateWithCorrectRoot(validatorCount: 2);
        var config = MakeMatchingConfig(state);
        var keys = config.GenesisValidatorKeys.ToList();
        keys[1] = ("0x" + new string('F', 104), keys[1].ProposalPubkey);
        config.GenesisValidatorKeys = keys;
        var error = CheckpointSync.ValidateState(state, config);
        Assert.That(error, Does.Contain("pubkey mismatch"));
    }

    [Test]
    public void ValidateStateWithConfig_AllChecksPass()
    {
        var state = MakeValidStateWithCorrectRoot(validatorCount: 4);
        var config = MakeMatchingConfig(state);
        var error = CheckpointSync.ValidateState(state, config);
        Assert.That(error, Is.Null);
    }

    // --- Helpers ---

    private static State MakeStateWithZeroedRoot(int validatorCount)
    {
        var validators = new List<Validator>();
        for (int i = 0; i < validatorCount; i++)
        {
            var pubkey = new Bytes52(Enumerable.Repeat((byte)(i + 1), 52).ToArray());
            validators.Add(new Validator(pubkey, pubkey, (ulong)i));
        }

        return new State(
            Config: new Config(1000),
            Slot: new Slot(100),
            LatestBlockHeader: new BlockHeader(new Slot(100), 0, Bytes32.Zero(), Bytes32.Zero(), Bytes32.Zero()),
            LatestJustified: Checkpoint.Default(),
            LatestFinalized: Checkpoint.Default(),
            HistoricalBlockHashes: Array.Empty<Bytes32>(),
            JustifiedSlots: Array.Empty<bool>(),
            Validators: validators,
            JustificationsRoots: Array.Empty<Bytes32>(),
            JustificationsValidators: Array.Empty<bool>());
    }

    private static State MakeValidStateWithCorrectRoot(int validatorCount)
    {
        return FillStateRoot(MakeStateWithZeroedRoot(validatorCount));
    }

    private static State FillStateRoot(State state)
    {
        var withZeroed = state with
        {
            LatestBlockHeader = state.LatestBlockHeader with { StateRoot = Bytes32.Zero() }
        };
        var stateRoot = new Bytes32(withZeroed.HashTreeRoot());
        return withZeroed with
        {
            LatestBlockHeader = withZeroed.LatestBlockHeader with { StateRoot = stateRoot }
        };
    }

    private static ConsensusConfig MakeMatchingConfig(State state)
    {
        var keys = state.Validators
            .Select(v => (
                Convert.ToHexString(v.AttestationPubkey.AsSpan()),
                Convert.ToHexString(v.ProposalPubkey.AsSpan())))
            .ToList();
        return new ConsensusConfig
        {
            GenesisTimeUnix = state.Config.GenesisTime,
            GenesisValidatorKeys = keys
        };
    }

    private sealed class FakeCheckpointProvider : ICheckpointProvider
    {
        private readonly State? _state;
        public FakeCheckpointProvider(State? state) => _state = state;

        public Task<State?> FetchFinalizedStateAsync(string url, CancellationToken ct) =>
            Task.FromResult(_state);
    }
}
