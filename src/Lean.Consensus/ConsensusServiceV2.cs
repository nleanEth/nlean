using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Sync;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class ConsensusServiceV2 : IConsensusService, ITickTarget
{
    private readonly ProtoArrayForkChoiceStore _store;
    private readonly SlotClock _clock;
    private readonly ChainService _chainService;
    private readonly ISyncService? _syncService;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public ConsensusServiceV2(ProtoArrayForkChoiceStore store, SlotClock clock, ConsensusConfig config,
        ISyncService? syncService = null)
    {
        _store = store;
        _clock = clock;
        _syncService = syncService;
        _chainService = new ChainService(clock, this, ProtoArrayForkChoiceStore.IntervalsPerSlot);
    }

    public ulong CurrentSlot => _clock.CurrentSlot;
    public ulong HeadSlot => _store.HeadSlot;
    public ulong JustifiedSlot => _store.JustifiedSlot;
    public ulong FinalizedSlot => _store.FinalizedSlot;

    public bool HasUnknownBlockRootsInFlight =>
        _syncService is not null && _syncService.State != SyncState.Synced;

    public byte[] HeadRoot => _store.HeadRoot.AsSpan().ToArray();

    public byte[] GetProposalHeadRoot() => HeadRoot;

    public AttestationData CreateAttestationData(ulong slot)
    {
        return new AttestationData(
            new Slot(slot),
            new Checkpoint(_store.HeadRoot, new Slot(_store.HeadSlot)),
            new Checkpoint(_store.JustifiedRoot, new Slot(_store.JustifiedSlot)),
            new Checkpoint(_store.FinalizedRoot, new Slot(_store.FinalizedSlot)));
    }

    public bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out string reason)
    {
        stateRoot = Bytes32.Zero();
        reason = "Not supported in V2 — use full chain state transition.";
        return false;
    }

    public bool TryApplyLocalBlock(SignedBlockWithAttestation signedBlock, out string reason)
    {
        var result = _store.OnBlock(signedBlock);
        reason = result.Accepted ? string.Empty : result.Reason;
        return result.Accepted;
    }

    public bool TryApplyLocalAttestation(SignedAttestation signedAttestation, out string reason)
    {
        _store.OnAttestation(signedAttestation);
        reason = string.Empty;
        return true;
    }

    public void OnTick(ulong slot, int intervalInSlot)
    {
        _store.TickInterval(slot, intervalInSlot);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = _chainService.RunAsync(_cts.Token);
        if (_syncService is not null)
            await _syncService.StartAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_syncService is not null)
            {
                try { await _syncService.StopAsync(cancellationToken); }
                catch (OperationCanceledException) { }
            }

            if (_runTask is not null)
            {
                try { await _runTask; }
                catch (OperationCanceledException) { }
            }

            _cts.Dispose();
            _cts = null;
        }
    }
}
