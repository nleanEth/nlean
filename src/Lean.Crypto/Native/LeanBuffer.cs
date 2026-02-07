using System.Runtime.InteropServices;

namespace Lean.Crypto.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct LeanBuffer
{
    public IntPtr Ptr;
    public nuint Len;
}
