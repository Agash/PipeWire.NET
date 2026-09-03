using System.Runtime.Versioning;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// Explicit synchronization end to end: a <see cref="PipeWireVideoOutput"/> publishing with
/// timeline metadata and descriptors, consumed by a <see cref="PipeWireVideoCapture"/> that waits
/// on acquire points and signals release points. This proves the transport (points and timeline
/// descriptors flow), the metadata contract (stamped points arrive intact), and the full
/// handshake (buffers recycle, so every release was signalled). GPU-time ordering beyond the
/// handshake needs a GPU engine; the eventfd timelines here order exactly the same protocol.
/// Skips where there is no render node or libgbm, like the plain dmabuf round-trip.
/// </summary>
[TestClass]
[TestCategory("RequiresGStreamer")]
[SupportedOSPlatform("linux")]
public sealed class ExplicitSyncTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task SyncProducer_ToSyncConsumer_CarriesPointsAndCompletesTheHandshake()
    {
        if (!File.Exists("/dev/dri/renderD128"))
            Assert.Inconclusive("No GPU render node (/dev/dri/renderD128) - skipping explicit-sync round-trip.");

        const int width = 320, height = 240, poolCap = 8;
        GbmAllocator gbm;
        try
        {
            gbm = new GbmAllocator("/dev/dri/renderD128");
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}) - skipping explicit-sync round-trip.");
            return;
        }

        var buffers = new List<GbmAllocator.Buffer>();
        try
        {
            await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync();

            long modifier = (long)GbmAllocator.LinearModifier;
            bool streaming = false;
            int framesConsumed = 0, syncFrames = 0;
            ulong maxAcquireSeen = 0, maxReleaseSeen = 0;
            long stamped = 0;

            await using var output = new PipeWireVideoOutput(ctx, "stx-sync-roundtrip", width, height, PixelFormat.Bgra, 30);
            output.AllocateDmaBufSync += (_, index, w, h, _, planes, out acquireFd, out releaseFd) =>
            {
                // Library eventfds for the timelines; the app only stamps points below.
                acquireFd = -1;
                releaseFd = -1;
                if (index >= poolCap) return 0;
                while (buffers.Count <= index) buffers.Add(gbm.CreateBgra(width, height));
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

            uint? nodeId = null;
            for (int i = 0; i < 50 && nodeId is null; i++)
            {
                nodeId = output.NodeId;
                if (nodeId is null) await Task.Delay(50);
            }

            Assert.IsNotNull(nodeId, "producer node should be assigned an id");

            await using var capture = new PipeWireVideoCapture(ctx, "stx-sync-roundtrip-sink");
            capture.FrameReady += (_, frame) =>
            {
                // Loop-thread serial: no synchronization needed inside the handler. The test
                // method below reads through Volatile after the observation window.
                framesConsumed++;
                if (frame.SyncTimeline is not { } timeline) return;
                syncFrames++;
                if (timeline.AcquirePoint == 0 || timeline.ReleasePoint == 0) return;
                if (timeline.AcquirePoint > maxAcquireSeen) maxAcquireSeen = timeline.AcquirePoint;
                if (timeline.ReleasePoint > maxReleaseSeen) maxReleaseSeen = timeline.ReleasePoint;
            };
            capture.Connect(nodeId.Value, [PixelFormat.Bgra], modifiers: [modifier],
                requestExplicitSync: true);

            using var driver = new Timer(_ => { if (streaming) output.TriggerFrame(); }, null, 100, 33);

            await Task.Delay(TimeSpan.FromSeconds(6));

            int seen = Volatile.Read(ref framesConsumed);
            int synced = Volatile.Read(ref syncFrames);
            ulong maxAcquire = Volatile.Read(ref maxAcquireSeen);
            ulong maxRelease = Volatile.Read(ref maxReleaseSeen);

            Assert.IsTrue(synced >= 10,
                $"expected >=10 frames carrying timeline points, got {synced} of {seen}");
            Assert.IsTrue(maxAcquire > 0 && maxRelease > 0,
                "timeline points must arrive nonzero: the stamped sequence never reached the consumer");
            Assert.IsTrue(seen >= 10,
                "flow must sustain: buffers recycle only when every release is signalled");
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
            gbm.Dispose();
        }
    }
}
