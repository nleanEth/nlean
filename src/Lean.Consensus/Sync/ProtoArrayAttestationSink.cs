using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

/// <summary>
/// Thread-safe attestation sink over ProtoArrayForkChoiceStore.
/// </summary>
public sealed class ProtoArrayAttestationSink : IAttestationSink
{
    private readonly ProtoArrayForkChoiceStore _store;
    private readonly bool _storeSignatures;

    public ProtoArrayAttestationSink(ProtoArrayForkChoiceStore store, bool storeSignatures)
    {
        _store = store;
        _storeSignatures = storeSignatures;
    }

    public void AddAttestation(SignedAttestation attestation)
    {
        lock (_store.SyncRoot)
        {
            _ = _store.TryOnAttestation(attestation, _storeSignatures, out _);
        }
    }
}
