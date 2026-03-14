using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public interface IAttestationSink
{
    void AddAttestation(SignedAttestation attestation);
    bool TryAddAttestation(SignedAttestation attestation);
}
