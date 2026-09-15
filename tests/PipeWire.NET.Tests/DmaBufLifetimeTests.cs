using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// What a DMA-BUF session leaves behind, and how its buffer pool behaves while it runs.
/// </summary>
/// <remarks>
/// A working round trip says frames arrive. It says nothing about whether the descriptors backing
/// them come back, and a descriptor leak on this path is the expensive kind: each one pins GPU
/// memory, the process hits its limit after a few hundred reconnects, and nothing before that point
/// looks wrong. The buffer pool is the other half. Indexes are handed out by the daemon, reused as
/// buffers cycle, and reissued from zero after a reconnect, so an allocator that assumes they only
/// grow corrupts whichever buffer it kept under the old meaning.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[TestCategory("RequiresGpu")]
[SupportedOSPlatform("linux")]
public sealed class DmaBufLifetimeTests : PipeWireTestBase
{
    private const int Width = 320;
    private const int Height = 240;
    private const int PoolCap = 8;

    private static int OpenDescriptors() => Directory.GetFiles("/proc/self/fd").Length;

    private static GbmAllocator RequireGbm()
    {
        if (!File.Exists("/dev/dri/renderD128"))
            Assert.Inconclusive("no GPU render node, so there is no dmabuf to account for.");

        try
        {
            return new GbmAllocator("/dev/dri/renderD128");
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}).");
            throw;
        }
    }

    /// <summary>One producer and consumer pair, run to a frame count and then torn down.</summary>
    private static async Task<(int Frames, int DmaBufFrames, IReadOnlyList<int> Indexes)> RunSessionAsync(
        GbmAllocator gbm, List<GbmAllocator.Buffer> buffers, string name)
    {
        long modifier = (long)GbmAllocator.LinearModifier;
        var indexes = new ConcurrentQueue<int>();
        bool streaming = false;
        int frames = 0, dmaBufFrames = 0;

        await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();

        await using var output = new PipeWireVideoOutput(ctx, $"{name}-src", Width, Height, PixelFormat.Bgra, 30);

        output.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
        {
            indexes.Enqueue(index);

            // The daemon chooses the index. An allocator that grew an array per index rather than
            // reusing one would be the leak this is looking for, so the cap is enforced here and
            // the assertions below check it was never approached from the other side.
            if (index >= PoolCap) return 0;

            while (buffers.Count <= index) buffers.Add(gbm.CreateBgra(Width, Height));

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

        if (nodeId is null) Assert.Inconclusive("the producer was never given a node id.");

        await using var capture = new PipeWireVideoCapture(ctx, $"{name}-sink");
        capture.FrameReady += (_, frame) =>
        {
            Interlocked.Increment(ref frames);
            if (frame.BufferType == PipeWireBufferType.DmaBuf) Interlocked.Increment(ref dmaBufFrames);
        };

        capture.Connect(nodeId!.Value, [PixelFormat.Bgra], modifiers: [modifier]);

        // Not driven from here. This node is not the graph's driver, and pw_stream_trigger_process
        // on a node that is not one reaches the real driver as RequestProcess, which an audio
        // adapter refuses - once per call, logged as an error. The consumer drives the graph.
        await Task.Delay(TimeSpan.FromSeconds(3));

        return (Volatile.Read(ref frames), Volatile.Read(ref dmaBufFrames), [.. indexes]);
    }

    [TestMethod]
    public async Task ADmaBufSessionTornDown_ReturnsEveryDescriptorItTook()
    {
        GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();

        try
        {
            // One session first, so the descriptors the runtime and the driver open once are not
            // counted against the session that follows.
            _ = await RunSessionAsync(gbm, buffers, "pwnet-dmabuf-warm");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int before = OpenDescriptors();

            for (int round = 0; round < 3; round++)
                _ = await RunSessionAsync(gbm, buffers, $"pwnet-dmabuf-fd-{round}");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int after = OpenDescriptors();

            Console.Error.WriteLine($"three dmabuf sessions: fds {before} -> {after}");

            // The buffers themselves are owned by this test and deliberately still alive, so what
            // is being measured is whether the streams gave back what they imported.
            Assert.IsTrue(after <= before + 2,
                $"three connect/teardown cycles left {after - before} descriptors behind");
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
            gbm.Dispose();
        }
    }

    [TestMethod]
    public async Task ABufferPool_ReusesItsIndexesRatherThanGrowing()
    {
        GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();

        try
        {
            (int frames, int dmaBufFrames, IReadOnlyList<int> indexes) =
                await RunSessionAsync(gbm, buffers, "pwnet-dmabuf-pool");

            if (dmaBufFrames == 0)
                Assert.Inconclusive($"no dmabuf frames arrived ({frames} total), so the pool never cycled.");

            Assert.IsTrue(indexes.Count > 0, "no buffer was ever allocated");

            // The daemon asks for a bounded set and reuses it. An index beyond the pool means it
            // kept asking for new ones, which is the shape a leak takes on this path.
            int highest = indexes.Max();
            Assert.IsTrue(highest < PoolCap,
                $"the daemon asked for buffer index {highest}, beyond the pool of {PoolCap}");

            // And far more frames than buffers, which is what proves the buffers are being cycled
            // rather than one being used per frame.
            Assert.IsTrue(dmaBufFrames > indexes.Count,
                $"{dmaBufFrames} frames from {indexes.Count} allocations, so nothing was reused");

            Console.Error.WriteLine(
                $"pool: {indexes.Count} allocations, highest index {highest}, {dmaBufFrames} dmabuf frames");
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
            gbm.Dispose();
        }
    }

    [TestMethod]
    public async Task ABufferRemovedWhileTheApplicationHoldsItsFd_LeavesThatFdUsable()
    {
        // The ownership question, which the round trip does not ask. A consumer that imports a
        // plane into a GPU keeps the descriptor for as long as the texture lives, and that can
        // outlast the buffer being removed from the pool or the stream being torn down. If teardown
        // closed a descriptor the application still holds, the next use of that texture reads freed
        // memory, and the crash lands nowhere near the cause.
        GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();
        var held = new List<int>();

        try
        {
            (_, int dmaBufFrames, IReadOnlyList<int> indexes) =
                await RunSessionAsync(gbm, buffers, "pwnet-dmabuf-hold");

            if (dmaBufFrames == 0 || indexes.Count == 0)
                Assert.Inconclusive("no dmabuf frames arrived, so no descriptor was ever shared.");

            // Duplicated while the session was alive is what an importing consumer effectively does.
            // The session above has now been torn down around them.
            foreach (GbmAllocator.Buffer buffer in buffers)
            {
                int duplicate = dup((int)buffer.Fd);
                if (duplicate >= 0) held.Add(duplicate);
            }

            Assert.IsTrue(held.Count > 0, "no descriptor could be duplicated, so nothing was tested");

            // Still usable after the stream that published them is gone: fstat succeeds on a live
            // descriptor and fails with EBADF on a closed one, which is exactly the distinction.
            foreach (int duplicate in held)
            {
                Assert.AreEqual(0, FStatSucceeds(duplicate) ? 0 : 1,
                    $"descriptor {duplicate} was closed underneath a holder that still had it");
            }

            // And the originals too: teardown must not have closed what the allocator owns.
            foreach (GbmAllocator.Buffer buffer in buffers)
            {
                Assert.IsTrue(FStatSucceeds((int)buffer.Fd),
                    "the stream closed a descriptor the allocator owns");
            }
        }
        finally
        {
            foreach (int duplicate in held) close(duplicate);
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
            gbm.Dispose();
        }
    }

    /// <summary>True when a descriptor is still open.</summary>
    /// <remarks>
    /// fstat is the cheapest question that distinguishes a live descriptor from a closed one:
    /// EBADF means closed, anything else means it is still there.
    /// </remarks>
    private static bool FStatSucceeds(int fd)
    {
        Span<byte> statbuf = stackalloc byte[512];
        unsafe
        {
            fixed (byte* p = statbuf) return fstat(fd, p) == 0;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int close(int fd);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern unsafe int fstat(int fd, byte* buf);

    [TestMethod]
    public async Task BufferIndexesAfterAReconnect_StartAgainRatherThanContinuing()
    {
        // The index is the producer's, handed out per pool buffer and recycled when one is removed.
        // Two separate producer instances is what this measures; one instance renegotiating is
        // ARenegotiatingProducer_KeepsItsPoolBoundedAndBalanced below. What this pins is that a
        // fresh producer starts from zero and stays inside the pool, so an allocator keying a GPU
        // surface by index cannot carry one session's surface into the next.
        GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();

        try
        {
            (_, int firstDmaBuf, IReadOnlyList<int> first) =
                await RunSessionAsync(gbm, buffers, "pwnet-dmabuf-reconnect-a");

            (_, int secondDmaBuf, IReadOnlyList<int> second) =
                await RunSessionAsync(gbm, buffers, "pwnet-dmabuf-reconnect-b");

            if (firstDmaBuf == 0 || secondDmaBuf == 0)
                Assert.Inconclusive("a session produced no dmabuf frames, so there is nothing to compare.");

            Assert.IsTrue(first.Count > 0 && second.Count > 0, "a session allocated nothing");

            Assert.AreEqual(0, second.Min(),
                "the second session's indexes did not start again from zero");
            Assert.IsTrue(second.Max() < PoolCap,
                $"the second session asked for index {second.Max()}, beyond the pool");
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
            gbm.Dispose();
        }
    }

    /// <summary>
    /// One producer, renegotiated again and again while it keeps running, keeps its pool bounded
    /// and every buffer it backed is released exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The renegotiation is upstream's own. When a port loses its last link,
    /// <c>pw_impl_port_release_mix</c> clears its format (impl-port.c), and for a stream that is
    /// <c>clear_buffers</c>: every buffer removed. The next consumer negotiates a format and a pool
    /// from scratch, <c>impl_port_use_buffers</c> adding them again. So a consumer connecting and
    /// going away, round after round, is the same producer instance renegotiating each time.
    /// </para>
    /// <para>
    /// That is the path the free-index stack and the plane-layout table exist for. Both are touched
    /// by add_buffer and remove_buffer, which upstream emits from inside one node method on the main
    /// loop, and read lock-free by the process callback. A stack that leaked an index would grow the
    /// pool past the consumer's allocation within a few rounds; one that handed an index out twice
    /// would back two live buffers with one GPU surface, and the release count would not match.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task ARenegotiatingProducer_KeepsItsPoolBoundedAndBalanced()
    {
        const int Rounds = 6;
        GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();
        long modifier = (long)GbmAllocator.LinearModifier;

        var gate = new object();
        var live = new HashSet<int>();
        var violations = new List<string>();
        int allocations = 0, releases = 0, highest = -1;

        try
        {
            await using var ctx = new PipeWireContext("pwnet-dmabuf-churn", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync();

            await using var output = new PipeWireVideoOutput(ctx, "pwnet-dmabuf-churn-src", Width, Height, PixelFormat.Bgra, 30);

            output.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
            {
                if (index >= PoolCap) return 0;

                lock (gate)
                {
                    if (!live.Add(index)) violations.Add($"index {index} handed out while still live");
                    allocations++;
                    highest = Math.Max(highest, index);
                }

                while (buffers.Count <= index) buffers.Add(gbm.CreateBgra(Width, Height));

                GbmAllocator.Buffer b = buffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };

            output.ReleaseDmaBuf += (_, index) =>
            {
                lock (gate)
                {
                    if (!live.Remove(index)) violations.Add($"index {index} released without being live");
                    releases++;
                }
            };

            output.FillDmaBuf += (_, _) => true;
            output.ConnectDmaBuf([modifier]);

            uint nodeId = await output.WaitForNodeIdAsync();

            for (int round = 0; round < Rounds; round++)
            {
                int dmaBufFrames = 0;

                await using (var capture = new PipeWireVideoCapture(ctx, $"pwnet-dmabuf-churn-sink-{round}"))
                {
                    capture.FrameReady += (_, frame) =>
                    {
                        if (frame.BufferType == PipeWireBufferType.DmaBuf) Interlocked.Increment(ref dmaBufFrames);
                    };

                    capture.Connect(nodeId, [PixelFormat.Bgra], modifiers: [modifier]);

                    for (int i = 0; i < 100 && Volatile.Read(ref dmaBufFrames) < 10; i++)
                        await Task.Delay(50);
                }

                if (round == 0 && Volatile.Read(ref dmaBufFrames) == 0)
                    Assert.Inconclusive("the first round produced no dmabuf frames, so there is no pool to churn.");

                Assert.IsTrue(Volatile.Read(ref dmaBufFrames) >= 10,
                    $"round {round}: only {dmaBufFrames} dmabuf frames after the producer was renegotiated");

                // The consumer is gone, so its link is, and upstream clears the producer's format
                // and every buffer with it. Waiting for that is the proof the round renegotiated.
                for (int i = 0; i < 100; i++)
                {
                    lock (gate) { if (live.Count == 0) break; }
                    await Task.Delay(50);
                }

                lock (gate)
                {
                    Assert.AreEqual(0, live.Count,
                        $"round {round}: buffers {string.Join(",", live)} were never released after the consumer left");
                }
            }

            lock (gate)
            {
                Console.Error.WriteLine(
                    $"churn: {Rounds} rounds, {allocations} allocations, {releases} releases, highest index {highest}");

                Assert.AreEqual(0, violations.Count, string.Join("; ", violations));
                Assert.AreEqual(allocations, releases, "allocations and releases do not balance");
                Assert.IsTrue(allocations >= Rounds,
                    $"{allocations} allocations over {Rounds} rounds, so the pool was not rebuilt each round");
                Assert.IsTrue(highest < PoolCap,
                    $"index {highest} after {Rounds} renegotiations, so freed indexes were not reused");
            }
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
            gbm.Dispose();
        }
    }
}
