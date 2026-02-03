using System.Runtime.InteropServices;
using Lean.Crypto.Native;

namespace Lean.Crypto;

public interface ILeanSig
{
    LeanSigKeyPair GenerateKeyPair(uint activationEpoch, uint numActiveEpochs);
    byte[] Sign(ReadOnlySpan<byte> secretKey, uint epoch, ReadOnlySpan<byte> message);
    bool Verify(ReadOnlySpan<byte> publicKey, uint epoch, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);
}

public sealed record LeanSigKeyPair(byte[] PublicKey, byte[] SecretKey);

public sealed class RustLeanSig : ILeanSig
{
    public const int MessageLength = 32;

    public RustLeanSig()
    {
        NativeLibraryResolver.EnsureInitialized();
    }

    public LeanSigKeyPair GenerateKeyPair(uint activationEpoch, uint numActiveEpochs)
    {
        var error = (LeanCryptoError)NativeMethods.LeanSigKeyGen(
            activationEpoch,
            numActiveEpochs,
            out var publicKeyPtr,
            out var publicKeyLen,
            out var secretKeyPtr,
            out var secretKeyLen);

        ThrowIfError(error, "leansig_key_gen");

        var publicKey = CopyAndFree(publicKeyPtr, publicKeyLen);
        var secretKey = CopyAndFree(secretKeyPtr, secretKeyLen);

        return new LeanSigKeyPair(publicKey, secretKey);
    }

    public byte[] Sign(ReadOnlySpan<byte> secretKey, uint epoch, ReadOnlySpan<byte> message)
    {
        EnsureMessageLength(message);

        unsafe
        {
            fixed (byte* skPtr = secretKey)
            fixed (byte* msgPtr = message)
            {
                var error = (LeanCryptoError)NativeMethods.LeanSigSign(
                    (IntPtr)skPtr,
                    (nuint)secretKey.Length,
                    epoch,
                    (IntPtr)msgPtr,
                    (nuint)message.Length,
                    out var signaturePtr,
                    out var signatureLen);

                ThrowIfError(error, "leansig_sign");
                return CopyAndFree(signaturePtr, signatureLen);
            }
        }
    }

    public bool Verify(ReadOnlySpan<byte> publicKey, uint epoch, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        EnsureMessageLength(message);

        unsafe
        {
            fixed (byte* pkPtr = publicKey)
            fixed (byte* sigPtr = signature)
            fixed (byte* msgPtr = message)
            {
                var error = (LeanCryptoError)NativeMethods.LeanSigVerify(
                    (IntPtr)pkPtr,
                    (nuint)publicKey.Length,
                    (IntPtr)sigPtr,
                    (nuint)signature.Length,
                    epoch,
                    (IntPtr)msgPtr,
                    (nuint)message.Length,
                    out var isValid);

                ThrowIfError(error, "leansig_verify");
                return isValid != 0;
            }
        }
    }

    private static void EnsureMessageLength(ReadOnlySpan<byte> message)
    {
        if (message.Length != MessageLength)
        {
            throw new ArgumentException($"LeanSig expects {MessageLength}-byte messages.");
        }
    }

    private static byte[] CopyAndFree(IntPtr ptr, nuint len)
    {
        if (ptr == IntPtr.Zero || len == 0)
        {
            return Array.Empty<byte>();
        }

        var bytes = new byte[(int)len];
        Marshal.Copy(ptr, bytes, 0, bytes.Length);
        NativeMethods.LeanFree(ptr, len);
        return bytes;
    }

    private static void ThrowIfError(LeanCryptoError error, string operation)
    {
        if (error == LeanCryptoError.Ok)
        {
            return;
        }

        throw new LeanCryptoException(error, $"LeanSig operation '{operation}' failed with {error}.");
    }
}
