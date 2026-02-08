namespace Lean.Consensus.Types;

public enum AttestationGossipDecodeFailure
{
    None = 0,
    EmptyPayload = 1,
    InvalidSsz = 2
}

public sealed record AttestationGossipDecodeResult
{
    private AttestationGossipDecodeResult(
        bool isSuccess,
        SignedAttestation? attestation,
        AttestationGossipDecodeFailure failure,
        string reason)
    {
        IsSuccess = isSuccess;
        Attestation = attestation;
        Failure = failure;
        Reason = reason;
    }

    public bool IsSuccess { get; }

    public SignedAttestation? Attestation { get; }

    public AttestationGossipDecodeFailure Failure { get; }

    public string Reason { get; }

    public static AttestationGossipDecodeResult Success(SignedAttestation attestation)
    {
        return new AttestationGossipDecodeResult(
            isSuccess: true,
            attestation: attestation,
            failure: AttestationGossipDecodeFailure.None,
            reason: "Payload decoded and validated.");
    }

    public static AttestationGossipDecodeResult Fail(AttestationGossipDecodeFailure failure, string reason)
    {
        return new AttestationGossipDecodeResult(
            isSuccess: false,
            attestation: null,
            failure: failure,
            reason: reason);
    }
}
