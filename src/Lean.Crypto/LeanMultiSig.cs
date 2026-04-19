using System.Runtime.InteropServices;
using Lean.Crypto.Native;

namespace Lean.Crypto;

public interface ILeanMultiSig
{
    byte[] AggregateSignatures(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
        IReadOnlyList<ReadOnlyMemory<byte>> signatures,
        ReadOnlySpan<byte> message,
        uint epoch);
    byte[] Aggregate(
        IReadOnlyList<bool> xmssParticipants,
        IReadOnlyList<byte[]> children,
        IReadOnlyList<(ReadOnlyMemory<byte> PublicKey, ReadOnlyMemory<byte> Signature)> rawXmss,
        ReadOnlySpan<byte> message,
        uint epoch,
        bool recursive = false);
    byte[] AggregateRecursive(
        IReadOnlyList<(IReadOnlyList<ReadOnlyMemory<byte>> PublicKeys, byte[] ProofData)> children,
        ReadOnlySpan<byte> message,
        uint epoch);
    bool VerifyAggregate(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> aggregateSignature,
        uint epoch);

    /// <summary>
    /// Verify an aggregate produced under leanSpec's TEST scheme (leanEnv=test,
    /// LOG_LIFETIME=8, DIMENSION=4). Backed by a separate cdylib.
    /// </summary>
    bool VerifyAggregateTest(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
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

        SetupProver();

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

    public byte[] Aggregate(
        IReadOnlyList<bool> xmssParticipants,
        IReadOnlyList<byte[]> children,
        IReadOnlyList<(ReadOnlyMemory<byte> PublicKey, ReadOnlyMemory<byte> Signature)> rawXmss,
        ReadOnlySpan<byte> message,
        uint epoch,
        bool recursive = false)
    {
        if (children.Count > 0)
        {
            throw new NotSupportedException(
                "Use AggregateRecursive() for merging children proofs.");
        }

        var publicKeys = rawXmss.Select(x => x.PublicKey).ToList();
        var signatures = rawXmss.Select(x => x.Signature).ToList();
        return AggregateSignatures(publicKeys, signatures, message, epoch);
    }

    public byte[] AggregateRecursive(
        IReadOnlyList<(IReadOnlyList<ReadOnlyMemory<byte>> PublicKeys, byte[] ProofData)> children,
        ReadOnlySpan<byte> message,
        uint epoch)
    {
        if (children.Count == 0)
        {
            throw new ArgumentException("At least one child proof is required for recursive aggregation.");
        }

        if (message.Length != RustLeanSig.MessageLength)
        {
            throw new ArgumentException($"LeanMultiSig expects {RustLeanSig.MessageLength}-byte messages.");
        }

        SetupProver();

        // Children proofs (serialized AggregatedXMSS)
        var childrenProofMemory = children.Select(c => (ReadOnlyMemory<byte>)c.ProofData.AsMemory()).ToList();
        using var childrenProofBuffers = new PinnedBufferArray(childrenProofMemory);

        // Flatten all children public keys into a single array
        var allChildPks = new List<ReadOnlyMemory<byte>>();
        var pkCounts = new nuint[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            pkCounts[i] = (nuint)children[i].PublicKeys.Count;
            foreach (var pk in children[i].PublicKeys)
            {
                allChildPks.Add(pk);
            }
        }
        using var childPkBuffers = new PinnedBufferArray(allChildPks);

        // No raw XMSS for pure recursive merge
        using var emptyPkBuffers = new PinnedBufferArray(Array.Empty<ReadOnlyMemory<byte>>());
        using var emptySigBuffers = new PinnedBufferArray(Array.Empty<ReadOnlyMemory<byte>>());

        unsafe
        {
            fixed (byte* msgPtr = message)
            fixed (nuint* countsPtr = pkCounts)
            {
                var error = (LeanCryptoError)NativeMethods.LeanMultiSigAggregateRecursive(
                    childrenProofBuffers.Pointer,
                    childrenProofBuffers.Length,
                    childPkBuffers.Pointer,
                    childPkBuffers.Length,
                    (IntPtr)countsPtr,
                    emptyPkBuffers.Pointer,
                    emptyPkBuffers.Length,
                    emptySigBuffers.Pointer,
                    emptySigBuffers.Length,
                    (IntPtr)msgPtr,
                    (nuint)message.Length,
                    epoch,
                    out var aggPtr,
                    out var aggLen);

                ThrowIfError(error, "leanmultisig_aggregate_recursive");
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

        SetupVerifier();

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

    public bool VerifyAggregateTest(
        IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> aggregateSignature,
        uint epoch)
    {
        if (message.Length != RustLeanSig.MessageLength)
        {
            throw new ArgumentException($"LeanMultiSig expects {RustLeanSig.MessageLength}-byte messages.");
        }

        SetupVerifierTest();

        using var pkBuffers = new PinnedBufferArray(publicKeys);

        unsafe
        {
            fixed (byte* msgPtr = message)
            fixed (byte* aggPtr = aggregateSignature)
            {
                var error = (LeanCryptoError)NativeMethods.LeanMultiSigVerifyAggregateTest(
                    pkBuffers.Pointer,
                    pkBuffers.Length,
                    (IntPtr)aggPtr,
                    (nuint)aggregateSignature.Length,
                    (IntPtr)msgPtr,
                    (nuint)message.Length,
                    epoch,
                    out var isValid);

                ThrowIfError(error, "leanmultisig_verify_aggregate_test");
                return isValid != 0;
            }
        }
    }

    private static void SetupProver()
    {
        var error = (LeanCryptoError)NativeMethods.LeanMultiSigSetupProver();
        ThrowIfError(error, "leanmultisig_setup_prover");
    }

    private static void SetupVerifier()
    {
        var error = (LeanCryptoError)NativeMethods.LeanMultiSigSetupVerifier();
        ThrowIfError(error, "leanmultisig_setup_verifier");
    }

    private static void SetupVerifierTest()
    {
        var error = (LeanCryptoError)NativeMethods.LeanMultiSigSetupVerifierTest();
        ThrowIfError(error, "leanmultisig_setup_verifier_test");
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
