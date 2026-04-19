using System.Reflection;
using System.Runtime.InteropServices;

namespace Lean.Crypto.Native;

internal static class NativeLibraryResolver
{
    public const string LibraryName = "lean_crypto_ffi";
    // Test-scheme (leanEnv=test) aggregate-verify FFI. rec_aggregation compiles its bytecode
    // against a single scheme, so the TEST build ships as a separate cdylib.
    public const string TestLibraryName = "lean_crypto_ffi_test";

    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal) &&
            !string.Equals(libraryName, TestLibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        var baseDir = AppContext.BaseDirectory;
        var rid = GetRuntimeIdentifier();
        var fileName = GetLibraryFileName(libraryName);

        // Prefer runtimes/{rid}/native/ layout (framework-dependent / portable publish)
        var candidate = Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
        {
            return handle;
        }

        // Fall back to base directory (single-file extraction puts native libs at root)
        candidate = Path.Combine(baseDir, fileName);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
        {
            return handle;
        }

        return IntPtr.Zero;
    }

    private static string GetRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "linux-arm64",
                Architecture.X64 => "linux-x64",
                _ => "linux-x64",
            };
        }

        return "linux-x64";
    }

    private static string GetLibraryFileName(string logicalName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"lib{logicalName}.dylib";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"{logicalName}.dll";
        }

        return $"lib{logicalName}.so";
    }
}
