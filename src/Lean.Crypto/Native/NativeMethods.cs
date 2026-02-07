using System.Runtime.InteropServices;

namespace Lean.Crypto.Native;

internal static partial class NativeMethods
{
    [LibraryImport(NativeLibraryResolver.LibraryName, EntryPoint = "leansig_key_gen")]
    internal static partial int LeanSigKeyGen(
        uint activationEpoch,
        uint numActiveEpochs,
        out IntPtr publicKeyPtr,
        out nuint publicKeyLen,
        out IntPtr secretKeyPtr,
        out nuint secretKeyLen);

    [LibraryImport(NativeLibraryResolver.LibraryName, EntryPoint = "leansig_sign")]
    internal static partial int LeanSigSign(
        IntPtr secretKeyPtr,
        nuint secretKeyLen,
        uint epoch,
        IntPtr messagePtr,
        nuint messageLen,
        out IntPtr signaturePtr,
        out nuint signatureLen);

    [LibraryImport(NativeLibraryResolver.LibraryName, EntryPoint = "leansig_verify")]
    internal static partial int LeanSigVerify(
        IntPtr publicKeyPtr,
        nuint publicKeyLen,
        IntPtr signaturePtr,
        nuint signatureLen,
        uint epoch,
        IntPtr messagePtr,
        nuint messageLen,
        out byte isValid);

    [LibraryImport(NativeLibraryResolver.LibraryName, EntryPoint = "leanmultisig_setup_prover")]
    internal static partial int LeanMultiSigSetupProver();

    [LibraryImport(NativeLibraryResolver.LibraryName, EntryPoint = "leanmultisig_setup_verifier")]
    internal static partial int LeanMultiSigSetupVerifier();

    [LibraryImport(NativeLibraryResolver.LibraryName, EntryPoint = "leanmultisig_aggregate")]
    internal static partial int LeanMultiSigAggregate(
        IntPtr publicKeys,
        nuint publicKeyCount,
        IntPtr signatures,
        nuint signatureCount,
        IntPtr messagePtr,
        nuint messageLen,
        uint epoch,
        out IntPtr aggregatePtr,
        out nuint aggregateLen);

    [LibraryImport(NativeLibraryResolver.LibraryName, EntryPoint = "leanmultisig_verify_aggregate")]
    internal static partial int LeanMultiSigVerifyAggregate(
        IntPtr publicKeys,
        nuint publicKeyCount,
        IntPtr aggregatePtr,
        nuint aggregateLen,
        IntPtr messagePtr,
        nuint messageLen,
        uint epoch,
        out byte isValid);

    [LibraryImport(NativeLibraryResolver.LibraryName, EntryPoint = "lean_free")]
    internal static partial void LeanFree(IntPtr buffer, nuint bufferLen);
}
