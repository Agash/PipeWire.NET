using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using PipeWire.NET.Interop;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The borrowed zero-copy path carrying a real GPU buffer, rather than host memory.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FrameRetention.Borrowed"/> was only ever exercised against <c>videotestsrc</c>, which
/// delivers <c>MemPtr</c>. Over host memory the descriptor fields are all absent by definition, so
/// every assertion about them passed by describing nothing: <c>IsFdBacked</c> was false, the fourcc
/// was whatever the format mapping returned, and no plane descriptor was ever looked at.
/// </para>
/// <para>
/// That is the one configuration this path exists for. A consumer handing frames to a GPU importer
/// needs the descriptor, the fourcc and the modifier together, and a borrowed frame that carries a
/// stale or closed descriptor fails at import time in the consumer's process, not here - which is
/// exactly the failure this is meant to catch first.
/// </para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class BorrowedFrameGpuTests : PipeWireTestBase
{
    private const int Width = 320;
    private const int Height = 240;
    private const int PoolCap = 8;

    private const string RenderNode = "/dev/dri/renderD128";

    private static GbmAllocator RequireGbm()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");

        if (!File.Exists(RenderNode))
            Assert.Inconclusive($"No GPU render node ({RenderNode}).");

        try
        {
            return new GbmAllocator(RenderNode);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}).");
            throw;
        }
    }

    /// <summary>
    /// A borrowed frame off a real dmabuf carries the descriptor, fourcc and modifier an importer
    /// binds with, and the descriptor is a live one.
    /// </summary>
    /// <remarks>
    /// Liveness is checked by duplicating it. A borrowed frame holds the pool's descriptor number
    /// rather than a handle, so a number left behind after the pool recycled or closed the buffer
    /// still reads as a plausible non-negative integer. Duplicating it is what separates a real
    /// descriptor from a remembered one - <c>fcntl</c> fails with <c>EBADF</c> on the latter.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task ABorrowedFrame_OverARealDmaBuf_CarriesALiveImportTriple()
    {
        using GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        try
        {
            await using var ctx = new PipeWireContext(
                "pwnet-borrow-gpu",
                ConsoleTestLoggerFactory.Instance
            );
            await ctx.StartAsync(cts.Token);

            long modifier = (long)GbmAllocator.LinearModifier;

            await using var output = new PipeWireVideoOutput(
                ctx,
                "pwnet-borrow-gpu-src",
                Width,
                Height,
                PixelFormat.Bgra,
                30
            );

            output.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
            {
                if (index >= PoolCap)
                    return 0;
                while (buffers.Count <= index)
                    buffers.Add(gbm.CreateBgra(Width, Height));
                GbmAllocator.Buffer b = buffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };

            output.FillDmaBuf += (_, _) => true;
            output.ConnectDmaBuf([modifier]);

            uint? nodeId = null;
            for (var i = 0; i < 60 && nodeId is null; i++)
            {
                nodeId = output.NodeId;
                if (nodeId is null)
                    await Task.Delay(50, cts.Token);
            }

            Assert.IsNotNull(nodeId, "the dmabuf producer was never assigned a node id");

            await using var capture = new PipeWireVideoCapture(ctx, "pwnet-borrow-gpu-sink")
            {
                Retention = FrameRetention.Borrowed,
            };

            var sawDmaBuf = 0;
            capture.FrameReady += (_, frame) =>
            {
                if (frame.BufferType == PipeWireBufferType.DmaBuf)
                    Interlocked.Increment(ref sawDmaBuf);
            };

            capture.Connect(nodeId!.Value, [PixelFormat.Bgra], modifiers: [modifier]);

            BorrowedVideoFrame borrowed = default;
            var got = false;
            for (var i = 0; i < 120 && !got; i++)
            {
                got = capture.TryGetBorrowedFrame(out borrowed) && borrowed.IsFdBacked;
                if (!got)
                    await Task.Delay(50, cts.Token);
            }

            Assert.IsTrue(
                Volatile.Read(ref sawDmaBuf) > 0,
                "no DMA-BUF frame ever arrived, so the borrowed path was never given a GPU buffer"
            );

            Assert.IsTrue(got, "borrowed retention never produced an fd-backed frame");

            Assert.AreEqual(Width, borrowed.Width);
            Assert.AreEqual(Height, borrowed.Height);
            Assert.IsTrue(
                borrowed.PlaneCount > 0,
                "an fd-backed frame with no planes cannot be imported"
            );

            Assert.AreEqual(
                DrmFormat.FromPixelFormat(PixelFormat.Bgra),
                borrowed.DrmFourcc,
                "the fourcc does not describe the format that was negotiated"
            );

            Assert.AreEqual(
                (ulong)modifier,
                borrowed.Modifier,
                "the modifier does not match the one the producer and consumer agreed on"
            );

            BorrowedVideoPlane plane = borrowed[0];
            Assert.IsTrue(plane.Fd >= 0, "the plane carries no descriptor");
            Assert.IsTrue(
                plane.Stride > 0,
                "the plane carries no stride, so an import cannot lay it out"
            );

            // The liveness check. A stale number survives every assertion above.
            using SafeFileHandle duplicate = FdInterop.DuplicateWithCloseOnExec((int)plane.Fd);
            Assert.IsFalse(
                duplicate.IsInvalid,
                "the borrowed descriptor could not be duplicated, so it is not a live dmabuf"
            );
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers)
                b.Dispose();
        }
    }

    /// <summary>
    /// Borrowed retention hands out each frame once, and does not allocate a frame object to do it.
    /// </summary>
    /// <remarks>
    /// The point of borrowing over <see cref="FrameRetention.Owned"/> is that it copies a handful of
    /// integers rather than duplicating a descriptor per plane per cycle. A borrowed frame is a
    /// value type, so the check that matters is that taking it clears the slot - a frame handed out
    /// twice means a consumer processes the same GPU buffer again while believing it is new.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task ABorrowedGpuFrame_IsHandedOutOnlyOnce()
    {
        using GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        try
        {
            await using var ctx = new PipeWireContext(
                "pwnet-borrow-once",
                ConsoleTestLoggerFactory.Instance
            );
            await ctx.StartAsync(cts.Token);

            long modifier = (long)GbmAllocator.LinearModifier;

            await using var output = new PipeWireVideoOutput(
                ctx,
                "pwnet-borrow-once-src",
                Width,
                Height,
                PixelFormat.Bgra,
                30
            );

            output.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
            {
                if (index >= PoolCap)
                    return 0;
                while (buffers.Count <= index)
                    buffers.Add(gbm.CreateBgra(Width, Height));
                GbmAllocator.Buffer b = buffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };

            output.FillDmaBuf += (_, _) => true;
            output.ConnectDmaBuf([modifier]);

            uint? nodeId = null;
            for (var i = 0; i < 60 && nodeId is null; i++)
            {
                nodeId = output.NodeId;
                if (nodeId is null)
                    await Task.Delay(50, cts.Token);
            }

            Assert.IsNotNull(nodeId);

            await using var capture = new PipeWireVideoCapture(ctx, "pwnet-borrow-once-sink")
            {
                Retention = FrameRetention.Borrowed,
            };

            capture.Connect(nodeId!.Value, [PixelFormat.Bgra], modifiers: [modifier]);

            var got = false;
            for (var i = 0; i < 120 && !got; i++)
            {
                got = capture.TryGetBorrowedFrame(out BorrowedVideoFrame first) && first.IsFdBacked;
                if (!got)
                    await Task.Delay(50, cts.Token);
            }

            Assert.IsTrue(got, "borrowed retention never produced an fd-backed frame");

            // Taken immediately after, before the producer's next cycle can refill the slot.
            Assert.IsFalse(
                capture.TryGetBorrowedFrame(out _),
                "the same borrowed GPU frame was handed out twice"
            );
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers)
                b.Dispose();
        }
    }
}
