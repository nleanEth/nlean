using System.Runtime.InteropServices;

namespace Lean.Crypto.Native;

internal sealed class PinnedBufferArray : IDisposable
{
    private readonly GCHandle[] _bufferHandles;
    private readonly GCHandle _arrayHandle;
    private readonly byte[][] _ownedBuffers;
    private bool _disposed;

    public PinnedBufferArray(IReadOnlyList<ReadOnlyMemory<byte>> buffers)
    {
        if (buffers.Count == 0)
        {
            _bufferHandles = Array.Empty<GCHandle>();
            _ownedBuffers = Array.Empty<byte[]>();
            _arrayHandle = default;
            Pointer = IntPtr.Zero;
            Length = 0;
            return;
        }

        _ownedBuffers = new byte[buffers.Count][];
        _bufferHandles = new GCHandle[buffers.Count];
        var nativeBuffers = new LeanBuffer[buffers.Count];

        for (var i = 0; i < buffers.Count; i++)
        {
            var memory = buffers[i];
            if (!MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) || segment.Array is null)
            {
                segment = new ArraySegment<byte>(memory.ToArray());
            }

            var array = segment.Array ?? throw new InvalidOperationException("Expected a non-null buffer.");
            _ownedBuffers[i] = array;
            _bufferHandles[i] = GCHandle.Alloc(array, GCHandleType.Pinned);
            nativeBuffers[i] = new LeanBuffer
            {
                Ptr = IntPtr.Add(_bufferHandles[i].AddrOfPinnedObject(), segment.Offset),
                Len = (nuint)segment.Count,
            };
        }

        _arrayHandle = GCHandle.Alloc(nativeBuffers, GCHandleType.Pinned);
        Pointer = _arrayHandle.AddrOfPinnedObject();
        Length = (nuint)nativeBuffers.Length;
    }

    public IntPtr Pointer { get; }

    public nuint Length { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_arrayHandle.IsAllocated)
        {
            _arrayHandle.Free();
        }

        foreach (var handle in _bufferHandles)
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }

        _disposed = true;
    }
}
