using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PipeWire.NET.Tests;

/// <summary>
/// Minimal libgbm allocator: opens the render node and hands out LINEAR-modifier BGRA buffers exported
/// as dmabuf fds. Just enough to back a PipeWire dmabuf producer in-process - not a general GBM wrapper.
/// </summary>
/// <remarks>
/// The allocator owns what it hands out. A buffer object belongs to its device, and destroying one
/// after the device is gone is a use-after-free inside libgbm - which is how a test that disposed the
/// allocator before its buffers took the whole test host down with SIGSEGV. So disposing the allocator
/// first destroys every buffer still alive, a buffer disposed later does nothing, and the order a test
/// disposes them in cannot matter.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class GbmAllocator : IDisposable
{
    public const ulong LinearModifier = 0; // DRM_FORMAT_MOD_LINEAR
    private const uint GbmFormatArgb8888 = 0x34325241; // fourcc('A','R','2','4') == BGRA byte order (LE)

    private readonly int _drmFd;
    private readonly IntPtr _device;
    private readonly List<Buffer> _live = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    public GbmAllocator(string renderNode)
    {
        _drmFd = open(renderNode, 2 /* O_RDWR */);
        if (_drmFd < 0) throw new InvalidOperationException($"open({renderNode}) failed errno={Marshal.GetLastPInvokeError()}");
        _device = gbm_create_device(_drmFd);
        if (_device == IntPtr.Zero) { close(_drmFd); throw new InvalidOperationException("gbm_create_device failed"); }
    }

    public Buffer CreateBgra(int width, int height)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            ulong mod = LinearModifier;
            IntPtr bo = gbm_bo_create_with_modifiers(_device, (uint)width, (uint)height, GbmFormatArgb8888, ref mod, 1);
            if (bo == IntPtr.Zero) throw new InvalidOperationException("gbm_bo_create_with_modifiers failed (LINEAR BGRA)");
            int fd = gbm_bo_get_fd(bo);
            uint stride = gbm_bo_get_stride(bo);
            uint offset = gbm_bo_get_offset(bo, 0);
            var buffer = new Buffer(this, bo, fd, offset, (int)stride, stride * (uint)height);
            _live.Add(buffer);
            return buffer;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            // Buffers before the device they were allocated from.
            foreach (Buffer b in _live.ToArray()) b.Release();
            _live.Clear();

            if (_device != IntPtr.Zero) gbm_device_destroy(_device);
            if (_drmFd >= 0) close(_drmFd);
            _disposed = true;
        }
    }

    public sealed class Buffer : IDisposable
    {
        private readonly GbmAllocator _owner;
        private IntPtr _bo;
        private int _fd;

        internal Buffer(GbmAllocator owner, IntPtr bo, int fd, uint offset, int stride, uint size)
        {
            _owner = owner;
            _bo = bo;
            _fd = fd;
            Fd = fd;
            Offset = offset;
            Stride = stride;
            Size = size;
        }

        public long Fd { get; }
        public uint Offset { get; }
        public int Stride { get; }
        public uint Size { get; }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                // Already released with its allocator, or disposed twice: nothing left to free.
                if (_bo == IntPtr.Zero) return;
                Release();
                _owner._live.Remove(this);
            }
        }

        /// <summary>Frees the descriptor and the buffer object. Called with the allocator's gate held.</summary>
        internal void Release()
        {
            if (_fd >= 0) close(_fd);
            if (_bo != IntPtr.Zero) gbm_bo_destroy(_bo);
            _fd = -1;
            _bo = IntPtr.Zero;
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libgbm.so.1")] private static extern IntPtr gbm_create_device(int fd);
    [DllImport("libgbm.so.1")] private static extern void gbm_device_destroy(IntPtr dev);
    [DllImport("libgbm.so.1")] private static extern IntPtr gbm_bo_create_with_modifiers(IntPtr dev, uint w, uint h, uint format, ref ulong modifiers, uint count);
    [DllImport("libgbm.so.1")] private static extern int gbm_bo_get_fd(IntPtr bo);
    [DllImport("libgbm.so.1")] private static extern uint gbm_bo_get_stride(IntPtr bo);
    [DllImport("libgbm.so.1")] private static extern uint gbm_bo_get_offset(IntPtr bo, int plane);
    [DllImport("libgbm.so.1")] private static extern void gbm_bo_destroy(IntPtr bo);
}
