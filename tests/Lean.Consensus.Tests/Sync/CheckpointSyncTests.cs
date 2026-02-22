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
        var state = MakeValidState(validatorCount: 4);
        var provider = new FakeCheckpointProvider(state);
        var sync = new CheckpointSync(provider);

        var result = await sync.SyncFromCheckpointAsync("http://example.com", CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.State, Is.Not.Null);
        Assert.That(result.State!.Validators.Count, Is.EqualTo(4));
    }

    [Test]
    public async Task SyncFromCheckpoint_NullResponse_Fails()
    {
        var provider = new FakeCheckpointProvider(null);
        var sync = new CheckpointSync(provider);

        var result = await sync.SyncFromCheckpointAsync("http://example.com", CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("Failed to fetch"));
    }

    [Test]
    public async Task SyncFromCheckpoint_NoValidators_Fails()
    {
        var state = MakeValidState(validatorCount: 0);
        var provider = new FakeCheckpointProvider(state);
        var sync = new CheckpointSync(provider);

        var result = await sync.SyncFromCheckpointAsync("http://example.com", CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("no validators"));
    }

    [Test]
    public async Task SyncFromCheckpoint_TooManyValidators_Fails()
    {
        var state = MakeValidState(validatorCount: CheckpointSync.ValidatorRegistryLimit + 1);
        var provider = new FakeCheckpointProvider(state);
        var sync = new CheckpointSync(provider);

        var result = await sync.SyncFromCheckpointAsync("http://example.com", CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("exceeding limit"));
    }

    // --- Helpers ---

    private static State MakeValidState(int validatorCount)
    {
        var validators = new List<Validator>();
        for (int i = 0; i < validatorCount; i++)
        {
            var pubkey = new Bytes52(Enumerable.Repeat((byte)(i + 1), 52).ToArray());
            validators.Add(new Validator(pubkey, (ulong)i));
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

    private sealed class FakeCheckpointProvider : ICheckpointProvider
    {
        private readonly State? _state;
        public FakeCheckpointProvider(State? state) => _state = state;

        public Task<State?> FetchFinalizedStateAsync(string url, CancellationToken ct) =>
            Task.FromResult(_state);
    }
}
