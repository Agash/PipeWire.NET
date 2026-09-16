using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// DMA-BUF device-ID negotiation between a real producer and a real consumer, through the daemon:
/// which device is chosen, that both ends and the allocator agree on it, and that an end that does not
/// negotiate still streams.
/// </summary>
/// <remarks>
/// <para>
/// The shapes are upstream's video-src-fixate and video-play-fixate. Buffers are real DMA-BUFs from
/// libgbm on this machine's first render node; the other devices offered are phantoms - valid
/// <c>dev_t</c>s no allocation happens on - because a negotiation only compares device numbers, and a
/// phantom is what makes "the ends settled on the one device they share" observable on a single-GPU
/// machine.
/// </para>
/// <para>
/// Each test waits for DMA-BUF frames, not for a state: a stream that reports Streaming with an
/// allocation that failed delivers nothing, and only frames prove the allocator was handed a device
/// it could use.
/// </para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class DeviceIdNegotiationEndToEndTests : PipeWireTestBase
{
    private const int Width = 320, Height = 240, PoolCap = 16;
    private static readonly long Linear = (long)GbmAllocator.LinearModifier;

    /// <summary>What one run saw.</summary>
    private sealed record Outcome(
        int Frames, int DmaBufFrames, int SyncFrames,
        DrmDevice? OutputDevice, DrmDevice? CaptureDevice,
        ImmutableArray<DrmDevice?> HandedToAllocator);

    private static DrmDevice Phantom(uint minor) => DrmDevice.FromNumbers(226, minor);

    /// <summary>The render node the buffers are allocated on, and an allocator for it.</summary>
    private static (DrmDevice Device, GbmAllocator Gbm) RealDevice()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("PipeWire is a Linux daemon.");

        ImmutableArray<DrmDevice> nodes = DrmDevice.EnumerateRenderNodes();
        if (nodes.IsEmpty) Assert.Inconclusive("No GPU render node - skipping device-ID negotiation.");

        try
        {
            return (nodes[0], new GbmAllocator(nodes[0].RenderNodePath!));
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}) - skipping device-ID negotiation.");
            throw;
        }
    }

    /// <summary>
    /// A producer and a consumer on one context, connected however the test says, streamed until
    /// enough DMA-BUF frames arrived or the budget ran out.
    /// </summary>
    private static async Task<Outcome> StreamAsync(
        string name,
        GbmAllocator gbm,
        Action<PipeWireVideoOutput> connectOutput,
        Action<PipeWireVideoCapture, uint> connectCapture,
        bool explicitSync = false,
        int wantFrames = 10,
        TimeSpan? budget = null)
    {
        var buffers = new List<GbmAllocator.Buffer>();
        var handed = new List<DrmDevice?>();
        int frames = 0, dmaBufFrames = 0, syncFrames = 0;
        long stamped = 0;

        int Back(int index, DrmDevice? device, Span<VideoPlane> planes)
        {
            lock (handed) handed.Add(device);
            if (index >= PoolCap) return 0;
            while (buffers.Count <= index) buffers.Add(gbm.CreateBgra(Width, Height));
            GbmAllocator.Buffer b = buffers[index];
            planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
            return 1;
        }

        try
        {
            await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync();

            await using var output = new PipeWireVideoOutput(ctx, $"{name}-src", Width, Height, PixelFormat.Bgra, 30);
            if (explicitSync)
            {
                output.AllocateDmaBufSync += (_, index, _, _, _, device, planes, out acquireFd, out releaseFd) =>
                {
                    acquireFd = -1;
                    releaseFd = -1;
                    return Back(index, device, planes);
                };
                output.FillDmaBuf += (sender, index) =>
                {
                    long point = Interlocked.Increment(ref stamped);
                    sender.StampSyncPoints(index, (ulong)point, (ulong)point);
                    return true;
                };
            }
            else
            {
                output.AllocateDmaBuf += (_, index, _, _, _, device, planes) => Back(index, device, planes);
                output.FillDmaBuf += (_, _) => true;
            }

            connectOutput(output);

            using var idCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            uint nodeId = await output.WaitForNodeIdAsync(idCts.Token);

            await using var capture = new PipeWireVideoCapture(ctx, $"{name}-sink");
            capture.FrameReady += (_, frame) =>
            {
                Interlocked.Increment(ref frames);
                if (frame.BufferType == PipeWireBufferType.DmaBuf) Interlocked.Increment(ref dmaBufFrames);
                if (frame.SyncTimeline is not null) Interlocked.Increment(ref syncFrames);
            };
            connectCapture(capture, nodeId);

            DateTime deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline && Volatile.Read(ref dmaBufFrames) < wantFrames)
                await Task.Delay(100);

            ImmutableArray<DrmDevice?> allocated;
            lock (handed) allocated = [.. handed];

            return new Outcome(
                Volatile.Read(ref frames), Volatile.Read(ref dmaBufFrames), Volatile.Read(ref syncFrames),
                output.NegotiatedDevice, capture.NegotiatedDevice, allocated);
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task BothEndsNegotiating_SettleOnTheDeviceTheyShare_AndTheAllocatorIsHandedIt()
    {
        (DrmDevice real, GbmAllocator gbm) = RealDevice();
        using (gbm)
        {
            // Each end prefers a device the other does not have. The producer lists its phantom first,
            // so a negotiation that ignored the devices would settle on it; the consumer's phantom is
            // not in the producer's available-devices list and is filtered out before it is offered.
            Outcome o = await StreamAsync("pwnet-devid-both", gbm,
                output => output.ConnectDmaBuf([new DmaBufDeviceOffer(Phantom(250), [Linear]), new DmaBufDeviceOffer(real, [Linear])]),
                (capture, node) => capture.Connect(node, [PixelFormat.Bgra],
                    deviceOffers: [new DmaBufDeviceOffer(Phantom(251), [Linear]), new DmaBufDeviceOffer(real, [Linear])]));

            Assert.IsTrue(o.DmaBufFrames >= 10, $"expected DMA-BUF frames, got {o.DmaBufFrames} of {o.Frames}");
            Assert.AreEqual(real, o.OutputDevice, "the producer's negotiated device");
            Assert.AreEqual(real, o.CaptureDevice, "the consumer's negotiated device");
            Assert.AreEqual(real.RenderNodePath, o.OutputDevice!.Value.RenderNodePath,
                "the negotiated device is described by the offer it matched, path included");
            Assert.IsFalse(o.HandedToAllocator.IsEmpty, "the allocator was never called");
            Assert.IsTrue(o.HandedToAllocator.All(d => d == real),
                $"every buffer must be allocated on the negotiated device; handed [{string.Join(", ", o.HandedToAllocator)}]");
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task AConsumerThatDoesNotNegotiate_StillStreams_WithTheDeviceUndefined()
    {
        (DrmDevice real, GbmAllocator gbm) = RealDevice();
        using (gbm)
        {
            // Every consumer that predates the protocol: modifiers, no Capability. The producer falls
            // back to its first device's modifiers without a device, upstream's implicit device.
            Outcome o = await StreamAsync("pwnet-devid-oldsink", gbm,
                output => output.ConnectDmaBuf([new DmaBufDeviceOffer(real, [Linear])]),
                (capture, node) => capture.Connect(node, [PixelFormat.Bgra], modifiers: [Linear]));

            Assert.IsTrue(o.DmaBufFrames >= 10, $"expected DMA-BUF frames, got {o.DmaBufFrames} of {o.Frames}");
            Assert.IsNull(o.OutputDevice, "no device can have been negotiated with a peer that does not negotiate");
            Assert.IsNull(o.CaptureDevice);
            Assert.IsFalse(o.HandedToAllocator.IsEmpty, "the allocator was never called");
            Assert.IsTrue(o.HandedToAllocator.All(d => d is null), "the allocator is told the device is undefined");
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task AProducerThatDoesNotNegotiate_StillStreams_WithTheDeviceUndefined()
    {
        (DrmDevice real, GbmAllocator gbm) = RealDevice();
        using (gbm)
        {
            // The consumer connects inactive and waits for the producer's capabilities; this producer
            // sends none, so what arrives is pw_stream's synthesised PeerCapability, and the consumer
            // must still activate and offer the first device's modifiers without a device.
            Outcome o = await StreamAsync("pwnet-devid-oldsrc", gbm,
                output => output.ConnectDmaBuf([Linear]),
                (capture, node) => capture.Connect(node, [PixelFormat.Bgra],
                    deviceOffers: [new DmaBufDeviceOffer(real, [Linear])]));

            Assert.IsTrue(o.DmaBufFrames >= 10, $"expected DMA-BUF frames, got {o.DmaBufFrames} of {o.Frames}");
            Assert.IsNull(o.OutputDevice);
            Assert.IsNull(o.CaptureDevice);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task ExplicitSync_OverANegotiatedDevice_CarriesTimelinesAndTheDevice()
    {
        (DrmDevice real, GbmAllocator gbm) = RealDevice();
        if (!DrmSyncobj.IsAvailable) Assert.Inconclusive("No render node supports syncobj timelines.");

        using (gbm)
        {
            Outcome o = await StreamAsync("pwnet-devid-sync", gbm,
                output => output.ConnectDmaBufSync([new DmaBufDeviceOffer(real, [Linear])]),
                (capture, node) => capture.Connect(node, [PixelFormat.Bgra],
                    deviceOffers: [new DmaBufDeviceOffer(real, [Linear])], requestExplicitSync: true),
                explicitSync: true);

            Assert.IsTrue(o.DmaBufFrames >= 10, $"expected DMA-BUF frames, got {o.DmaBufFrames} of {o.Frames}");
            Assert.IsTrue(o.SyncFrames >= 10, $"expected frames carrying timelines, got {o.SyncFrames} of {o.Frames}");
            Assert.AreEqual(real, o.OutputDevice);
            Assert.AreEqual(real, o.CaptureDevice);
            Assert.IsTrue(o.HandedToAllocator.All(d => d == real),
                $"handed [{string.Join(", ", o.HandedToAllocator)}]");
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    // Both streams report the failed negotiation as a stream error, which is the right outcome here.
    [ExpectsLibraryError("no more output formats")]
    public async Task EndsWithNoDeviceInCommon_DoNotSettleOnOne()
    {
        (DrmDevice real, GbmAllocator gbm) = RealDevice();
        using (gbm)
        {
            // Both negotiate and share nothing. The consumer's only remaining shape is host memory,
            // which a DMA-BUF-only producer cannot agree to, so nothing may stream - and above all no
            // device may be reported that neither end offered.
            Outcome o = await StreamAsync("pwnet-devid-disjoint", gbm,
                output => output.ConnectDmaBuf([new DmaBufDeviceOffer(real, [Linear])]),
                (capture, node) => capture.Connect(node, [PixelFormat.Bgra],
                    deviceOffers: [new DmaBufDeviceOffer(Phantom(252), [Linear])]),
                wantFrames: 1,
                budget: TimeSpan.FromSeconds(4));

            Assert.AreEqual(0, o.DmaBufFrames, "a DMA-BUF frame arrived with no device in common");
            Assert.IsNull(o.OutputDevice);
            Assert.IsNull(o.CaptureDevice);
            Assert.IsTrue(o.HandedToAllocator.IsEmpty, "nothing was negotiated, so nothing should be allocated");
        }
    }
}
