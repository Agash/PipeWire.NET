using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// Planar video from this library's output to its capture, through the daemon: every plane arrives,
/// and a copy keeps every plane.
/// </summary>
/// <remarks>
/// The output lays a planar image out in one block and the capture used to ask for a block per plane,
/// the shape GStreamer's sink uses, so the daemon failed the buffer allocation between the two and
/// no planar frame ever crossed. The capture now takes either shape.
/// </remarks>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class PlanarRoundTripTests : PipeWireTestBase
{
    private const int Width = 64;
    private const int Height = 32;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [TestMethod]
    [DataRow(PixelFormat.Nv12)]
    [DataRow(PixelFormat.Yuv420)]
    public async Task PlanarVideo_ArrivesWithEveryPlane(PixelFormat format)
    {
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            "pwnet-planar",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        await using var output = new PipeWireVideoOutput(
            ctx,
            $"pwnet-planar-src-{format}-{Environment.ProcessId}",
            Width,
            Height,
            format,
            30,
            new VideoColorInfo(
                VideoColorRange.Limited_16_235,
                VideoColorMatrix.Bt709,
                VideoTransferFunction.Bt709,
                VideoColorPrimaries.Bt709,
                VideoChromaSite.HCosited
            )
        );
        output.FillFrame += (_, pixels, stride, _, h, f) =>
        {
            // Each plane a value of its own, so a plane read from the wrong place shows.
            int at = 0;
            for (int plane = 0; plane < Planes(f); plane++)
            {
                (int planeStride, int rows) = Layout(f, plane, stride, h);
                pixels.Slice(at, planeStride * rows).Fill((byte)(0x10 + (plane * 0x30)));
                at += planeStride * rows;
            }

            return true;
        };
        output.Connect(autoConnect: false);
        uint node = await output.WaitForNodeIdAsync(cts.Token);

        TaskCompletionSource<(
            int Planes,
            byte[] Samples,
            OwnedVideoFrame Copy,
            VideoColorInfo Color
        )> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = new PipeWireVideoCapture(
            ctx,
            $"pwnet-planar-sink-{format}-{Environment.ProcessId}"
        );
        capture.FrameReady += (_, frame) =>
        {
            if (seen.Task.IsCompleted)
                return;

            byte[] samples = new byte[frame.HostPlaneCount];
            for (int plane = 0; plane < frame.HostPlaneCount; plane++)
            {
                // The last row of each plane: the part a luma-only view would get wrong.
                ReadOnlySpan<byte> bytes = frame.GetHostPlane(plane);
                int stride = frame.GetHostStride(plane);
                samples[plane] = bytes[(bytes.Length / stride - 1) * stride];
            }

            seen.TrySetResult((frame.HostPlaneCount, samples, frame.Clone(), frame.Color));
        };
        capture.Connect(node, [format], preferredWidth: Width, preferredHeight: Height);

        (int planes, byte[] samples, OwnedVideoFrame copy, VideoColorInfo color) =
            await seen.Task.WaitAsync(cts.Token);

        Assert.AreEqual(Planes(format), planes, "every plane of the format is readable");
        for (int plane = 0; plane < planes; plane++)
            Assert.AreEqual((byte)(0x10 + (plane * 0x30)), samples[plane], $"plane {plane}");

        // A copy keeps the planes one after another at the strides that follow from the first.
        int expected = 0;
        for (int plane = 0; plane < planes; plane++)
        {
            (int stride, int rows) = Layout(format, plane, copy.Stride, Height);
            expected += stride * rows;
        }

        Assert.AreEqual(expected, copy.Pixels.Length);
        Assert.AreEqual((byte)(0x10 + ((planes - 1) * 0x30)), copy.Pixels[^1]);
        Assert.AreEqual(VideoChromaSite.HCosited, color.ChromaSite, "the declared colour arrives");
        Assert.AreEqual(VideoColorMatrix.Bt709, color.Matrix);
    }

    private static int Planes(PixelFormat format) => format == PixelFormat.Yuv420 ? 3 : 2;

    private static (int Stride, int Rows) Layout(
        PixelFormat format,
        int plane,
        int stride,
        int height
    ) =>
        plane == 0 ? (stride, height)
        : format == PixelFormat.Nv12 ? (stride, (height + 1) / 2)
        : ((stride + 1) / 2, (height + 1) / 2);
}
