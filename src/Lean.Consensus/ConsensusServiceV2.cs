using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class ConsensusServiceV2 : IConsensusService, ITickTarget
{
    private readonly ProtoArrayForkChoiceStore _store;
    private readonly SlotClock _clock;
    private readonly ChainService _chainService;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public ConsensusServiceV2(ProtoArrayForkChoiceStore store, SlotClock clock, ConsensusConfig config)
    {
        _store = store;
        _clock = clock;
        _chainService = new ChainService(clock, this, ProtoArrayForkChoiceStore.IntervalsPerSlot);
    }

    public ulong CurrentSlot => _clock.CurrentSlot;
    public ulong HeadSlot => _store.HeadSlot;
    public ulong JustifiedSlot => _store.JustifiedSlot;
    public ulong FinalizedSlot => _store.FinalizedSlot;
    public bool HasUnknownBlockRootsInFlight => false;
    public byte[] HeadRoot => _store.HeadRoot.AsSpan().ToArray();

    public byte[] GetProposalHeadRoot() => HeadRoot;

    public AttestationData CreateAttestationData(ulong slot)
    {
        var headRoot = _store.HeadRoot;
        return new AttestationData(
            new Slot(slot),
            new Checkpoint(headRoot, new Slot(_store.HeadSlot)),
            new Checkpoint(headRoot, new Slot(_store.JustifiedSlot)),
            new Checkpoint(headRoot, new Slot(_store.FinalizedSlot)));
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = _chainService.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
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
