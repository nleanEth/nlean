namespace Lean.Crypto;

public sealed class LeanCryptoException : Exception
{
    public LeanCryptoError ErrorCode { get; }

    public LeanCryptoException(LeanCryptoError errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
