namespace Lean.Crypto;

public enum LeanCryptoError
{
    Ok = 0,
    NullPointer = 1,
    InvalidLength = 2,
    DeserializeError = 3,
    SigningFailed = 4,
    AggregateFailed = 5,
    ProofFailed = 6,
    InternalError = 7,
    Panic = 255,
}
