using System.Reflection;
using System.Runtime.InteropServices;

namespace Lean.Crypto.Native;

internal static class NativeLibraryResolver
{
    public const string LibraryName = "lean_crypto_ffi";
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
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        var baseDir = AppContext.BaseDirectory;
        var rid = GetRuntimeIdentifier();
        var fileName = GetLibraryFileName();

        var candidate = Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
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

        return "linux-x64";
    }

    private static string GetLibraryFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "liblean_crypto_ffi.dylib";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "lean_crypto_ffi.dll";
        }

        return "liblean_crypto_ffi.so";
    }
}
