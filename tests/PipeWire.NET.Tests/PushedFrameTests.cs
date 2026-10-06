using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// A producer that renders into shared buffers it takes on its own thread and queues them itself, as
/// GStreamer's pipewiresink does.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed partial class PushedFrameTests : PipeWireTestBase
{
    private const int Width = 64;
    private const int Height = 32;
    private const int PoolSize = 8;
    private const int ProtRead = 1;
    private const int ProtWrite = 2;
    private const int MapShared = 1;

    // Each pushed frame is numbered in its first pixel; the consumer sees the numbers in order, every
    // one it gets newly rendered, and the pull handler is never asked for a frame. Pushed faster than
    // the consumer takes them, frames are skipped rather than queued up behind each other.
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    [Timeout(60_000)]
    public async Task PushedFrames_ReachTheConsumerAsRendered()
    {
        using GbmAllocator gbm = new("/dev/dri/renderD128");
        GbmAllocator.Buffer[] buffers = new GbmAllocator.Buffer[PoolSize];
        for (int i = 0; i < PoolSize; i++)
        {
            buffers[i] = gbm.CreateBgra(Width, Height);
        }

        try
        {
            await using PipeWireContext context = new("test", ConsoleTestLoggerFactory.Instance);
            await context.StartAsync(TestContext.CancellationToken);
            long modifier = (long)GbmAllocator.LinearModifier;
            int pulled = 0;
            await using PipeWireVideoOutput output = new(
                context,
                "stx-pushed-frames",
                Width,
                Height,
                PixelFormat.Bgra,
                30
            )
            {
                PushFrames = true,
            };
            output.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
            {
                if (index >= PoolSize)
                {
                    return 0;
                }

                GbmAllocator.Buffer b = buffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };
            output.FillDmaBuf += (_, _) =>
            {
                Interlocked.Increment(ref pulled);
                return true;
            };
            output.ConnectDmaBuf([modifier], TestContext.CancellationToken);
            uint node = await output.WaitForNodeIdAsync(TestContext.CancellationToken);

            ConcurrentQueue<uint> seen = new();
            await using PipeWireVideoCapture capture = new(context, "stx-pushed-frames-sink");
            capture.FrameReady += (_, frame) =>
            {
                if (frame.BufferType == PipeWireBufferType.DmaBuf)
                {
                    seen.Enqueue(FirstPixel(frame.Fd, buffers[0].Size));
                }
            };
            capture.Connect(node, [PixelFormat.Bgra], modifiers: [modifier]);

            uint pushed = 0;
            int skipped = 0;
            using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(5));
            using CancellationTokenSource expiry = new(TimeSpan.FromSeconds(20));
            while (seen.Count < 20 && await frames.WaitForNextTickAsync(expiry.Token))
            {
                if (!output.TryBeginFrame(out PipeWireOutputFrame frame))
                {
                    skipped += output.SharesBuffers ? 1 : 0;
                    continue;
                }

                using (frame)
                {
                    GbmAllocator.Buffer target = buffers[frame.BufferIndex];
                    WriteFirstPixel(target.Fd, target.Size, ++pushed);
                    frame.Publish();
                }
            }

            uint[] numbers = [.. seen];
            Assert.IsGreaterThanOrEqualTo(20, numbers.Length);
            Assert.AreEqual(0, Volatile.Read(ref pulled), "the pull handler was asked for a frame");
            Assert.IsGreaterThan(0, skipped, "frames pushed at 200 Hz to a 30 Hz consumer queued up");
            for (int i = 1; i < numbers.Length; i++)
            {
                Assert.IsGreaterThan(
                    numbers[i - 1],
                    numbers[i],
                    $"frame {i} repeats or reorders: {string.Join(',', numbers)}"
                );
            }
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers)
            {
                b?.Dispose();
            }
        }
    }

    // Until a consumer settles on shared buffers there is nothing to render into.
    [TestMethod]
    public async Task TryBeginFrame_BeforeAConsumerSettles_TakesNothing()
    {
        await using PipeWireContext context = new("test", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(TestContext.CancellationToken);
        await using PipeWireVideoOutput output = new(
            context,
            "stx-pushed-unsettled",
            Width,
            Height,
            PixelFormat.Bgra,
            30
        )
        {
            PushFrames = true,
        };

        Assert.IsFalse(output.TryBeginFrame(out _));
    }

    private static unsafe uint FirstPixel(long fd, uint size)
    {
        void* map = mmap(null, size, ProtRead, MapShared, (int)fd, 0);
        Assert.AreNotEqual((nint)(-1), (nint)map, $"mmap failed: {Marshal.GetLastPInvokeError()}");
        try
        {
            return *(uint*)map;
        }
        finally
        {
            _ = munmap(map, size);
        }
    }

    private static unsafe void WriteFirstPixel(long fd, uint size, uint value)
    {
        void* map = mmap(null, size, ProtRead | ProtWrite, MapShared, (int)fd, 0);
        Assert.AreNotEqual((nint)(-1), (nint)map, $"mmap failed: {Marshal.GetLastPInvokeError()}");
        try
        {
            *(uint*)map = value;
        }
        finally
        {
            _ = munmap(map, size);
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial void* mmap(
        void* addr,
        nuint length,
        int prot,
        int flags,
        int fd,
        long offset
    );

    [LibraryImport("libc")]
    private static unsafe partial int munmap(void* addr, nuint length);
}
