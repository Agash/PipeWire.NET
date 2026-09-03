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
}
