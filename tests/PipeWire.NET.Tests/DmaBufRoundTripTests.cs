using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// End-to-end DMA-BUF zero-copy round-trip that depends on neither GStreamer nor any StreamWeaver/VAAPI
/// code: a <see cref="PipeWireVideoOutput"/> dmabuf producer is backed by a real dmabuf allocated through
/// libgbm on the render node, and a <see cref="PipeWireVideoCapture"/> consumer connects with the matching
/// DRM modifier and must receive frames whose <see cref="PipeWireBufferType"/> is <c>DmaBuf</c> with a valid
/// fd. This is the regression guard for the dmabuf path (modifier negotiation + fixation, plane layout, fd
/// passing) - gst's <c>pipewiresink</c> dmabuf export is unreliable across distro/driver builds (some
/// advertise no modifier at all), so the capability is exercised in-process instead.
/// Skips where there is no render node or libgbm (e.g. GPU-less CI runners).
/// </summary>
[TestClass]
[TestCategory("RequiresGStreamer")]
[SupportedOSPlatform("linux")]
public sealed class DmaBufRoundTripTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task DmaBufProducer_ToConsumer_DeliversDmaBufFrames()
    {
        if (!File.Exists("/dev/dri/renderD128"))
            Assert.Inconclusive("No GPU render node (/dev/dri/renderD128) - skipping dmabuf round-trip.");

        const int width = 320, height = 240, poolCap = 8;
        GbmAllocator gbm;
        try
        {
            gbm = new GbmAllocator("/dev/dri/renderD128");
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}) - skipping dmabuf round-trip.");
            return;
        }

        var buffers = new List<GbmAllocator.Buffer>();
        try
        {
            await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync();

            long modifier = (long)GbmAllocator.LinearModifier;
            bool streaming = false;
            int framesConsumed = 0, dmaBufConsumed = 0;
            long firstFd = -1;

            await using var output = new PipeWireVideoOutput(ctx, "stx-dmabuf-roundtrip", width, height, PixelFormat.Bgra, 30);
            output.AllocateDmaBuf += (_, index, w, h, _, planes) =>
            {
                if (index >= poolCap) return 0;
                while (buffers.Count <= index) buffers.Add(gbm.CreateBgra(width, height));
                GbmAllocator.Buffer b = buffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };
            output.FillDmaBuf += (_, _) => true;
            output.StateChanged += (_, _, s) => streaming = s == PipeWireStreamState.Streaming;
            output.ConnectDmaBuf([modifier]);

            uint? nodeId = null;
            for (int i = 0; i < 50 && nodeId is null; i++)
            {
                nodeId = output.NodeId;
                if (nodeId is null) await Task.Delay(50);
            }

            Assert.IsNotNull(nodeId, "producer node should be assigned an id");

            await using var capture = new PipeWireVideoCapture(ctx, "stx-dmabuf-roundtrip-sink");
            capture.FrameReady += (_, frame) =>
            {
                Interlocked.Increment(ref framesConsumed);
                if (frame.BufferType == PipeWireBufferType.DmaBuf)
                {
                    Interlocked.Increment(ref dmaBufConsumed);
                    Interlocked.CompareExchange(ref firstFd, frame.Fd, -1);
                }
            };
            capture.Connect(nodeId.Value, [PixelFormat.Bgra], modifiers: [modifier]);

            // DRIVER producer: pace it at ~30fps once streaming so the consumer sees a steady frame flow.
            using var driver = new Timer(_ => { if (streaming) output.TriggerFrame(); }, null, 100, 33);

            await Task.Delay(TimeSpan.FromSeconds(4));

            Assert.IsTrue(dmaBufConsumed >= 10,
                $"expected >=10 DMA-BUF frames through the dmabuf path, got {dmaBufConsumed} dmabuf of {framesConsumed} total");
            Assert.IsTrue(firstFd >= 0, "a delivered DMA-BUF frame must expose a valid fd for zero-copy import");
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
            gbm.Dispose();
        }
    }

    /// <summary>
    /// Minimal libgbm allocator: opens the render node and hands out LINEAR-modifier BGRA buffers exported
    /// as dmabuf fds. Just enough to back a PipeWire dmabuf producer in-process - not a general GBM wrapper.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private sealed class GbmAllocator : IDisposable
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
}
