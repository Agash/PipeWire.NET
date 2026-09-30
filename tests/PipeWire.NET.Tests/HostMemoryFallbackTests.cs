using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// A DMA-BUF output that also offers host memory serves a consumer that cannot import a shared
/// buffer: the stream backs its pool with memfd memory and fills it through FillFrame.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class HostMemoryFallbackTests : PipeWireTestBase
{
    private const int Width = 64;
    private const int Height = 32;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task ADmaBufOutput_ServesAMemoryConsumerThroughItsFallback()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");

        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            "pwnet-fallback",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        int shared = 0;
        await using var output = new PipeWireVideoOutput(
            ctx,
            $"pwnet-fallback-src-{Environment.ProcessId}",
            Width,
            Height,
            PixelFormat.Bgra
        )
        {
            HostMemoryFallback = true,
        };
        output.AllocateDmaBuf += (_, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref shared);
            return 0;
        };
        output.FillFrame += (_, pixels, stride, w, h, _) =>
        {
            for (int y = 0; y < h; y++)
                pixels.Slice(y * stride, w * 4).Fill((byte)(0x20 + (y % 7)));
            return true;
        };
        output.ConnectDmaBuf([(long)DrmFormatModifier.Linear]);
        uint node = await output.WaitForNodeIdAsync(cts.Token);

        TaskCompletionSource<(byte First, byte Last, PipeWireBufferType Type)> seen = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var capture = new PipeWireVideoCapture(
            ctx,
            $"pwnet-fallback-sink-{Environment.ProcessId}"
        );
        capture.FrameReady += (_, frame) =>
        {
            if (frame.Pixels.Length >= frame.Stride * Height)
                seen.TrySetResult(
                    (frame.Pixels[0], frame.Pixels[(Height - 1) * frame.Stride], frame.BufferType)
                );
        };
        capture.Connect(node, [PixelFormat.Bgra], preferredWidth: Width, preferredHeight: Height);

        (byte first, byte last, PipeWireBufferType type) = await seen.Task.WaitAsync(cts.Token);
        Assert.AreEqual((byte)0x20, first);
        Assert.AreEqual((byte)(0x20 + ((Height - 1) % 7)), last);
        Assert.AreNotEqual(PipeWireBufferType.DmaBuf, type, "the consumer took memory");
        Assert.AreEqual(0, Volatile.Read(ref shared), "no shared buffer was asked for");
    }
}
