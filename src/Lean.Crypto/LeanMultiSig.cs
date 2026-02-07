using System.Runtime.InteropServices;
using Lean.Crypto.Native;

namespace Lean.Crypto;

public interface ILeanMultiSig
{
    void SetupProver();
    void SetupVerifier();
    byte[] AggregateSignatures(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
        IReadOnlyList<ReadOnlyMemory<byte>> signatures,
        ReadOnlySpan<byte> message,
        uint epoch);
    bool VerifyAggregate(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> aggregateSignature,
        uint epoch);
}

public sealed class RustLeanMultiSig : ILeanMultiSig
{
    public RustLeanMultiSig()
    {
        NativeLibraryResolver.EnsureInitialized();
    }

    public void SetupProver()
    {
        var error = (LeanCryptoError)NativeMethods.LeanMultiSigSetupProver();
        ThrowIfError(error, "leanmultisig_setup_prover");
    }

    public void SetupVerifier()
    {
        var error = (LeanCryptoError)NativeMethods.LeanMultiSigSetupVerifier();
        ThrowIfError(error, "leanmultisig_setup_verifier");
    }

    public byte[] AggregateSignatures(
        IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
        IReadOnlyList<ReadOnlyMemory<byte>> signatures,
        ReadOnlySpan<byte> message,
        uint epoch)
    {
        if (publicKeys.Count != signatures.Count)
        {
            throw new ArgumentException("Public key and signature counts must match.");
        }

        if (message.Length != RustLeanSig.MessageLength)
        {
            throw new ArgumentException($"LeanMultiSig expects {RustLeanSig.MessageLength}-byte messages.");
        }

        using var pkBuffers = new PinnedBufferArray(publicKeys);
        using var sigBuffers = new PinnedBufferArray(signatures);

        unsafe
        {
            fixed (byte* msgPtr = message)
            {
                var error = (LeanCryptoError)NativeMethods.LeanMultiSigAggregate(
                    pkBuffers.Pointer,
                    pkBuffers.Length,
                    sigBuffers.Pointer,
                    sigBuffers.Length,
                    (IntPtr)msgPtr,
                    (nuint)message.Length,
                    epoch,
                    out var aggPtr,
                    out var aggLen);

                ThrowIfError(error, "leanmultisig_aggregate");
                return CopyAndFree(aggPtr, aggLen);
            }
        }
    }

    public bool VerifyAggregate(
        IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> aggregateSignature,
        uint epoch)
    {
        if (message.Length != RustLeanSig.MessageLength)
        {
            throw new ArgumentException($"LeanMultiSig expects {RustLeanSig.MessageLength}-byte messages.");
        }

        using var pkBuffers = new PinnedBufferArray(publicKeys);

        unsafe
        {
            fixed (byte* msgPtr = message)
            fixed (byte* aggPtr = aggregateSignature)
            {
                var error = (LeanCryptoError)NativeMethods.LeanMultiSigVerifyAggregate(
                    pkBuffers.Pointer,
                    pkBuffers.Length,
                    (IntPtr)aggPtr,
                    (nuint)aggregateSignature.Length,
                    (IntPtr)msgPtr,
                    (nuint)message.Length,
                    epoch,
                    out var isValid);

                ThrowIfError(error, "leanmultisig_verify_aggregate");
                return isValid != 0;
            }
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

        throw new LeanCryptoException(error, $"LeanMultiSig operation '{operation}' failed with {error}.");
    }
}
