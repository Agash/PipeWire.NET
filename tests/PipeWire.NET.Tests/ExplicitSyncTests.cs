using System.Runtime.Versioning;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// Explicit synchronization end to end: a <see cref="PipeWireVideoOutput"/> publishing with
/// timeline metadata and descriptors, consumed by a <see cref="PipeWireVideoCapture"/> that waits
/// on acquire points and signals release points. This proves the transport (points and timeline
/// descriptors flow), the metadata contract (stamped points arrive intact), and the full
/// handshake (buffers recycle, so every release was signalled). The timelines are DRM syncobjs,
/// created by the library or by the app; GPU-time ordering beyond that needs a GPU engine, and a
/// thread signalling the app's timeline late stands in for one. Inconclusive where there is no
/// render node or libgbm, like the plain dmabuf round-trip.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[TestCategory("RequiresGpu")]
[SupportedOSPlatform("linux")]
public sealed class ExplicitSyncTests : PipeWireTestBase
{
    [TestMethod]
    public async Task SyncProducer_ToSyncConsumer_CarriesPointsAndCompletesTheHandshake()
    {
        if (!File.Exists("/dev/dri/renderD128"))
            Assert.Inconclusive(
                "No GPU render node (/dev/dri/renderD128) - skipping explicit-sync round-trip."
            );

        const int width = 320,
            height = 240,
            poolCap = 8;
        GbmAllocator gbm;
        try
        {
            gbm = new GbmAllocator("/dev/dri/renderD128");
        }
        catch (Exception ex)
        {
            Assert.Inconclusive(
                $"libgbm unavailable ({ex.Message}) - skipping explicit-sync round-trip."
            );
            return;
        }

        var buffers = new List<GbmAllocator.Buffer>();
        try
        {
            await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync();

            long modifier = (long)GbmAllocator.LinearModifier;
            bool streaming = false;
            int framesConsumed = 0,
                syncFrames = 0;
            ulong maxAcquireSeen = 0,
                maxReleaseSeen = 0;
            long stamped = 0;

            await using var output = new PipeWireVideoOutput(
                ctx,
                "stx-sync-roundtrip",
                width,
                height,
                PixelFormat.Bgra,
                30
            );
            output.AllocateDmaBufSync += (
                _,
                index,
                w,
                h,
                _,
                _,
                planes,
                out acquireFd,
                out releaseFd
            ) =>
            {
                // Library-created syncobj timelines; the app only stamps points below.
                acquireFd = -1;
                releaseFd = -1;
                if (index >= poolCap)
                    return 0;
                while (buffers.Count <= index)
                    buffers.Add(gbm.CreateBgra(width, height));
                GbmAllocator.Buffer b = buffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };
            output.FillDmaBuf += (_, index) =>
            {
                long point = Interlocked.Increment(ref stamped);
                output.StampSyncPoints(index, (ulong)point, (ulong)point);
                return true;
            };
            output.StateChanged += (_, _, s) => streaming = s == PipeWireStreamState.Streaming;
            output.ConnectDmaBufSync([modifier]);

            using var idCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            uint? nodeId = await output.WaitForNodeIdAsync(idCts.Token);

            await using var capture = new PipeWireVideoCapture(ctx, "stx-sync-roundtrip-sink");
            capture.FrameReady += (_, frame) =>
            {
                // Loop-thread serial: no synchronization needed inside the handler. The test
                // method below reads through Volatile after the observation window.
                framesConsumed++;
                if (frame.SyncTimeline is not { } timeline)
                    return;
                syncFrames++;
                if (timeline.AcquirePoint == 0 || timeline.ReleasePoint == 0)
                    return;
                if (timeline.AcquirePoint > maxAcquireSeen)
                    maxAcquireSeen = timeline.AcquirePoint;
                if (timeline.ReleasePoint > maxReleaseSeen)
                    maxReleaseSeen = timeline.ReleasePoint;
            };
            capture.Connect(
                nodeId.Value,
                [PixelFormat.Bgra],
                modifiers: [modifier],
                requestExplicitSync: true
            );

            // Not driven from here. This node is not the graph's driver, and pw_stream_trigger_process
            // on a node that is not one reaches the real driver as RequestProcess, which an audio
            // adapter refuses - once per call, logged as an error. The consumer drives the graph.

            await Task.Delay(TimeSpan.FromSeconds(6));

            int seen = Volatile.Read(ref framesConsumed);
            int synced = Volatile.Read(ref syncFrames);
            ulong maxAcquire = Volatile.Read(ref maxAcquireSeen);
            ulong maxRelease = Volatile.Read(ref maxReleaseSeen);

            Assert.IsTrue(
                synced >= 10,
                $"expected >=10 frames carrying timeline points, got {synced} of {seen}"
            );
            Assert.IsTrue(
                maxAcquire > 0 && maxRelease > 0,
                "timeline points must arrive nonzero: the stamped sequence never reached the consumer"
            );
            Assert.IsTrue(
                seen >= 10,
                "flow must sustain: buffers recycle only when every release is signalled"
            );
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers)
                b.Dispose();
            gbm.Dispose();
        }
    }

    /// <summary>
    /// A borrowed frame the consumer is still holding keeps its buffer out of the producer's
    /// rotation until it is released - the release point, not the end of the cycle, decides reuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What explicit sync is for, and what carrying the points across proves nothing about. The
    /// buffer goes back to the producer at the end of the cycle as always; what stops the producer
    /// writing into it is the release point, on a DRM syncobj timeline, which this consumer only
    /// signals when it lets the frame go. So while the frame is held, its buffer must not be
    /// delivered again, and once it is released the buffer must come back.
    /// </para>
    /// <para>
    /// Producer and consumer are on separate contexts, as they would be separate processes: the
    /// producer's release wait blocks its data loop, and on a shared one it would stall the
    /// consumer too and measure a deadlock instead of the gate. The hold is shorter than the
    /// producer's release timeout, so the producer is waiting on the syncobj when the release lands.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AHeldBorrowedFrame_KeepsItsBufferOutOfRotation_UntilItIsReleased()
    {
        if (!File.Exists("/dev/dri/renderD128"))
            Assert.Inconclusive(
                "No GPU render node (/dev/dri/renderD128) - explicit sync needs one."
            );

        // The whole pool the producer's Buffers param allows (2..16): a buffer the allocator declines
        // fails every buffer, not just itself (client-node.c do_port_use_buffers).
        const int width = 320,
            height = 240,
            poolCap = 16;
        GbmAllocator gbm;
        try
        {
            gbm = new GbmAllocator("/dev/dri/renderD128");
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}).");
            return;
        }

        var buffers = new List<GbmAllocator.Buffer>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

            await using var producerCtx = new PipeWireContext(
                "pwnet-gate-producer",
                ConsoleTestLoggerFactory.Instance
            );
            await producerCtx.StartAsync(cts.Token);
            await using var consumerCtx = new PipeWireContext(
                "pwnet-gate-consumer",
                ConsoleTestLoggerFactory.Instance
            );
            await consumerCtx.StartAsync(cts.Token);

            long modifier = (long)GbmAllocator.LinearModifier;
            long stamped = 0;

            await using var output = new PipeWireVideoOutput(
                producerCtx,
                "pwnet-gate",
                width,
                height,
                PixelFormat.Bgra,
                30
            );
            output.AllocateDmaBufSync += (
                _,
                index,
                _,
                _,
                _,
                _,
                planes,
                out acquireFd,
                out releaseFd
            ) =>
            {
                acquireFd = -1;
                releaseFd = -1;
                if (index >= poolCap)
                    return 0;
                while (buffers.Count <= index)
                    buffers.Add(gbm.CreateBgra(width, height));
                GbmAllocator.Buffer b = buffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };
            output.FillDmaBuf += (_, index) =>
            {
                long point = Interlocked.Increment(ref stamped);
                output.StampSyncPoints(index, (ulong)point, (ulong)point);
                return true;
            };
            output.ConnectDmaBufSync([modifier]);
            uint nodeId = await output.WaitForNodeIdAsync(cts.Token);

            // Which buffer each delivery used, by the consumer's descriptor number for its dmabuf -
            // stable for the life of the pool - and when it arrived.
            var deliveries = new List<(long Fd, long AtTicks)>();
            await using var capture = new PipeWireVideoCapture(consumerCtx, "pwnet-gate-sink")
            {
                Retention = FrameRetention.Borrowed,
            };
            capture.FrameReady += (_, frame) =>
            {
                if (frame.SyncTimeline is null || frame.Planes.IsEmpty)
                    return;
                lock (deliveries)
                    deliveries.Add((frame.Planes[0].Fd, DateTime.UtcNow.Ticks));
            };
            capture.Connect(
                nodeId,
                [PixelFormat.Bgra],
                modifiers: [modifier],
                requestExplicitSync: true
            );
            await capture.WaitForStreamingAsync(cts.Token);

            // Let the pool turn over so every buffer has been seen at least once.
            await Task.Delay(1000, cts.Token);

            Assert.IsTrue(
                capture.TryGetBorrowedFrame(out BorrowedVideoFrame held),
                "no borrowed frame to hold"
            );
            long heldFd = held[0].Fd;
            long heldAt = DateTime.UtcNow.Ticks;

            await Task.Delay(600, cts.Token);

            long releasedAt = DateTime.UtcNow.Ticks;
            capture.ReleaseBorrowedFrame();

            await Task.Delay(1500, cts.Token);

            (long Fd, long AtTicks)[] seen;
            lock (deliveries)
                seen = [.. deliveries];

            Assert.IsTrue(
                seen.Count(d => d.AtTicks < heldAt) >= poolCap,
                "the pool never turned over before the hold, so there is nothing to compare against"
            );

            int reusedWhileHeld = seen.Count(d =>
                d.Fd == heldFd && d.AtTicks > heldAt && d.AtTicks < releasedAt
            );
            Assert.AreEqual(
                0,
                reusedWhileHeld,
                "the producer wrote into a buffer the consumer was still holding - the release point did not gate reuse"
            );

            Assert.IsTrue(
                seen.Any(d => d.Fd == heldFd && d.AtTicks > releasedAt),
                "the held buffer never came back after it was released - the release was not signalled"
            );
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers)
                b.Dispose();
            gbm.Dispose();
        }
    }

    /// <summary>
    /// A producer's own acquire timeline is left for the producer to signal: a frame reaches the
    /// consumer only after the app signals its acquire point, never when the fill handler returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contract a GPU producer depends on. Its fill handler submits rendering and returns
    /// before the GPU has finished, and the GPU signals the acquire point when the frame is really
    /// written. If the library signalled that timeline itself when the handler returned, the
    /// consumer would read a frame still being drawn. Here a delayed signal from another thread
    /// stands in for the GPU, 150ms after each fill, so a frame that arrives before its signal is
    /// exactly that defect.
    /// </para>
    /// <para>
    /// The timelines are syncobjs the app creates and exports itself, as a Vulkan producer does,
    /// and the release side runs over them too: the consumer signals the app's release timeline and
    /// the producer waits on it, or the pool would stop turning over.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AnAppSuppliedAcquireTimeline_IsLeftForTheAppToSignal()
    {
        if (!File.Exists("/dev/dri/renderD128"))
            Assert.Inconclusive(
                "No GPU render node (/dev/dri/renderD128) - explicit sync needs one."
            );
        if (!DrmSyncobj.IsAvailable)
            Assert.Inconclusive(
                "No render node with timeline syncobjs, so the app has no timelines to supply."
            );

        // The whole pool the producer's Buffers param allows (2..16): a buffer the allocator declines
        // fails every buffer, not just itself (client-node.c do_port_use_buffers).
        const int width = 320,
            height = 240,
            poolCap = 16;
        GbmAllocator gbm;
        try
        {
            gbm = new GbmAllocator("/dev/dri/renderD128");
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}).");
            return;
        }

        var buffers = new List<GbmAllocator.Buffer>();
        var timelines = new (uint AcquireHandle, int AcquireFd, uint ReleaseHandle, int ReleaseFd)[
            poolCap
        ];
        var signalledAt = new System.Collections.Concurrent.ConcurrentDictionary<ulong, long>();
        var arrivals = new List<(ulong Point, long AtTicks)>();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

            await using var producerCtx = new PipeWireContext(
                "pwnet-appsync-producer",
                ConsoleTestLoggerFactory.Instance
            );
            await producerCtx.StartAsync(cts.Token);
            await using var consumerCtx = new PipeWireContext(
                "pwnet-appsync-consumer",
                ConsoleTestLoggerFactory.Instance
            );
            await consumerCtx.StartAsync(cts.Token);

            long modifier = (long)GbmAllocator.LinearModifier;
            long stamped = 0;

            var output = new PipeWireVideoOutput(
                producerCtx,
                "pwnet-appsync",
                width,
                height,
                PixelFormat.Bgra,
                30
            );
            try
            {
                output.AllocateDmaBufSync += (
                    _,
                    index,
                    _,
                    _,
                    _,
                    _,
                    planes,
                    out acquireFd,
                    out releaseFd
                ) =>
                {
                    acquireFd = -1;
                    releaseFd = -1;
                    if (index >= poolCap)
                        return 0;

                    if (timelines[index].AcquireFd == 0)
                    {
                        (uint ah, int af) = DrmSyncobj.Create();
                        (uint rh, int rf) = DrmSyncobj.Create();
                        timelines[index] = (ah, af, rh, rf);
                    }

                    acquireFd = timelines[index].AcquireFd;
                    releaseFd = timelines[index].ReleaseFd;

                    while (buffers.Count <= index)
                        buffers.Add(gbm.CreateBgra(width, height));
                    GbmAllocator.Buffer b = buffers[index];
                    planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                    return 1;
                };

                output.FillDmaBuf += (sender, index) =>
                {
                    ulong point = (ulong)Interlocked.Increment(ref stamped);
                    sender.StampSyncPoints(index, point, point);

                    // The "GPU": the frame is declared written only later, from elsewhere.
                    uint acquire = timelines[index].AcquireHandle;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        signalledAt[point] = DateTime.UtcNow.Ticks;
                        DrmSyncobj.Signal(acquire, point);
                    });
                    return true;
                };

                output.ConnectDmaBufSync([modifier]);
                uint nodeId = await output.WaitForNodeIdAsync(cts.Token);

                await using var capture = new PipeWireVideoCapture(
                    consumerCtx,
                    "pwnet-appsync-sink"
                );
                capture.FrameReady += (_, frame) =>
                {
                    if (frame.SyncTimeline is not { AcquirePoint: > 0 } timeline)
                        return;
                    lock (arrivals)
                        arrivals.Add((timeline.AcquirePoint, DateTime.UtcNow.Ticks));
                };
                capture.Connect(
                    nodeId,
                    [PixelFormat.Bgra],
                    modifiers: [modifier],
                    requestExplicitSync: true
                );
                await capture.WaitForStreamingAsync(cts.Token);

                await Task.Delay(3000, cts.Token);
            }
            finally
            {
                await output.DisposeAsync();
            }

            (ulong Point, long AtTicks)[] seen;
            lock (arrivals)
                seen = [.. arrivals];

            Assert.IsTrue(
                seen.Length >= 5,
                $"only {seen.Length} frames arrived, so the app's timelines did not carry the stream"
            );

            foreach ((ulong point, long at) in seen)
            {
                Assert.IsTrue(
                    signalledAt.TryGetValue(point, out long signalled),
                    $"frame {point} arrived although the app never signalled its acquire point"
                );
                Assert.IsTrue(
                    at >= signalled,
                    $"frame {point} arrived {(signalled - at) / TimeSpan.TicksPerMillisecond}ms before the app signalled it - the library declared it ready itself"
                );
            }
        }
        finally
        {
            foreach ((uint ah, int af, uint rh, int rf) in timelines)
            {
                if (af == 0)
                    continue;
                DrmSyncobj.Destroy(ah);
                DrmSyncobj.Destroy(rh);
                Descriptors.CloseDescriptor(af);
                Descriptors.CloseDescriptor(rf);
            }

            foreach (GbmAllocator.Buffer b in buffers)
                b.Dispose();
            gbm.Dispose();
        }
    }
}
