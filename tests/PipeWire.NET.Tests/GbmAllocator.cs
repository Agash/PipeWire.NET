using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PipeWire.NET.Tests;

/// <summary>
/// Minimal libgbm allocator: opens the render node and hands out LINEAR-modifier BGRA buffers exported
/// as dmabuf fds. Just enough to back a PipeWire dmabuf producer in-process - not a general GBM wrapper.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class GbmAllocator : IDisposable
{
    public const ulong LinearModifier = 0; // DRM_FORMAT_MOD_LINEAR
    private const uint GbmFormatArgb8888 = 0x34325241; // fourcc('A','R','2','4') == BGRA byte order (LE)

    private readonly int _drmFd;
    private readonly IntPtr _device;

    public GbmAllocator(string renderNode)
    {
        _drmFd = open(renderNode, 2 /* O_RDWR */);
        if (_drmFd < 0) throw new InvalidOperationException($"open({renderNode}) failed errno={Marshal.GetLastPInvokeError()}");
        _device = gbm_create_device(_drmFd);
        if (_device == IntPtr.Zero) { close(_drmFd); throw new InvalidOperationException("gbm_create_device failed"); }
    }

    public Buffer CreateBgra(int width, int height)
    {
        ulong mod = LinearModifier;
        IntPtr bo = gbm_bo_create_with_modifiers(_device, (uint)width, (uint)height, GbmFormatArgb8888, ref mod, 1);
        if (bo == IntPtr.Zero) throw new InvalidOperationException("gbm_bo_create_with_modifiers failed (LINEAR BGRA)");
        int fd = gbm_bo_get_fd(bo);
        uint stride = gbm_bo_get_stride(bo);
        uint offset = gbm_bo_get_offset(bo, 0);
        return new Buffer(bo, fd, offset, (int)stride, stride * (uint)height);
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero) gbm_device_destroy(_device);
        if (_drmFd >= 0) close(_drmFd);
    }

    public sealed class Buffer(IntPtr bo, int fd, uint offset, int stride, uint size) : IDisposable
    {
        public long Fd { get; } = fd;
        public uint Offset { get; } = offset;
        public int Stride { get; } = stride;
        public uint Size { get; } = size;

        public void Dispose()
        {
            if (fd >= 0) close(fd);
            if (bo != IntPtr.Zero) gbm_bo_destroy(bo);
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
