using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// An output offering several pixel formats lets the consumer pick one, as a camera node enumerates
/// every format it produces, and writes its frames in the one picked.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class FormatChoiceTests : PipeWireTestBase
{
    private const int Width = 64;
    private const int Height = 32;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task Constructor_UnknownOrRepeatedFormat_Throws()
    {
        await using var ctx = new PipeWireContext(
            "pwnet-formats",
            ConsoleTestLoggerFactory.Instance
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PipeWireVideoOutput(ctx, "pwnet-formats-empty", Width, Height, [])
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PipeWireVideoOutput(
                ctx,
                "pwnet-formats-unknown",
                Width,
                Height,
                [PixelFormat.Unknown]
            )
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            new PipeWireVideoOutput(
                ctx,
                "pwnet-formats-twice",
                Width,
                Height,
                [PixelFormat.Bgra, PixelFormat.Nv12, PixelFormat.Bgra]
            )
        );

        await using var bgra = new PipeWireVideoOutput(ctx, "pwnet-formats-default", Width, Height);
        CollectionAssert.AreEqual(new[] { PixelFormat.Bgra }, bgra.Formats.ToArray());
    }

    [TestMethod]
    [DataRow(PixelFormat.Bgra)]
    [DataRow(PixelFormat.Nv12)]
    public async Task MemoryOutput_ConsumerPicksAnOfferedFormat_WritesInIt(PixelFormat wanted)
    {
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            "pwnet-formats",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        int filled = (int)PixelFormat.Unknown;
        await using var output = new PipeWireVideoOutput(
            ctx,
            $"pwnet-formats-src-{Environment.ProcessId}",
            Width,
            Height,
            [PixelFormat.Nv12, PixelFormat.Bgra]
        );
        output.FillFrame += (_, pixels, _, _, _, format) =>
        {
            Volatile.Write(ref filled, (int)format);
            pixels.Fill(0x5A);
            return true;
        };
        output.Connect(options: new() { AutoConnect = false });
        uint node = await output.WaitForNodeIdAsync(cts.Token);

        TaskCompletionSource<(PixelFormat Format, byte First)> seen = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var capture = new PipeWireVideoCapture(
            ctx,
            $"pwnet-formats-sink-{Environment.ProcessId}"
        );
        capture.FrameReady += (_, frame) =>
        {
            if (frame.Pixels.Length >= Width * Height)
                seen.TrySetResult((frame.Format, frame.Pixels[0]));
        };
        capture.Connect(
            PipeWireTarget.Id(node),
            new()
            {
                PreferredFormats = [wanted],
                PreferredWidth = Width,
                PreferredHeight = Height,
            }
        );

        (PixelFormat format, byte first) = await seen.Task.WaitAsync(cts.Token);
        Assert.AreEqual(wanted, format, "the consumer reads the format it asked for");
        Assert.AreEqual(wanted, output.NegotiatedPixelFormat);
        Assert.AreEqual(
            wanted,
            (PixelFormat)Volatile.Read(ref filled),
            "the fill handler writes it"
        );
        Assert.AreEqual((byte)0x5A, first);
    }

    [TestMethod]
    public async Task DmaBufOutput_MemoryConsumerPicksASecondFormat_GetsItThroughTheFallback()
    {
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            "pwnet-formats",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        int shared = 0;
        int filled = (int)PixelFormat.Unknown;
        await using var output = new PipeWireVideoOutput(
            ctx,
            $"pwnet-formats-gpu-{Environment.ProcessId}",
            Width,
            Height,
            [PixelFormat.Nv12, PixelFormat.Bgra]
        )
        {
            HostMemoryFallback = true,
        };
        output.AllocateDmaBuf += (_, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref shared);
            return 0;
        };
        output.FillFrame += (_, pixels, stride, w, h, format) =>
        {
            Volatile.Write(ref filled, (int)format);
            for (int y = 0; y < h; y++)
                pixels.Slice(y * stride, w * 4).Fill(0x33);
            return true;
        };
        output.ConnectDmaBufWithoutDevice([(long)DrmFormatModifier.Linear]);
        uint node = await output.WaitForNodeIdAsync(cts.Token);

        TaskCompletionSource<(PixelFormat Format, PipeWireBufferType Type, byte First)> seen = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var capture = new PipeWireVideoCapture(
            ctx,
            $"pwnet-formats-gpu-sink-{Environment.ProcessId}"
        );
        capture.FrameReady += (_, frame) =>
        {
            if (frame.Pixels.Length >= frame.Stride * Height)
                seen.TrySetResult((frame.Format, frame.BufferType, frame.Pixels[0]));
        };
        capture.Connect(
            PipeWireTarget.Id(node),
            new()
            {
                PreferredFormats = [PixelFormat.Bgra],
                PreferredWidth = Width,
                PreferredHeight = Height,
            }
        );

        (PixelFormat format, PipeWireBufferType type, byte first) = await seen.Task.WaitAsync(
            cts.Token
        );
        Assert.AreEqual(PixelFormat.Bgra, format);
        Assert.AreNotEqual(PipeWireBufferType.DmaBuf, type, "the consumer took memory");
        Assert.AreEqual(PixelFormat.Bgra, output.NegotiatedPixelFormat);
        Assert.AreEqual(PixelFormat.Bgra, (PixelFormat)Volatile.Read(ref filled));
        Assert.AreEqual((byte)0x33, first);
        Assert.AreEqual(0, Volatile.Read(ref shared), "no shared buffer was asked for");
    }

    [TestMethod]
    public async Task Output_WithoutADescription_IsListedByItsName()
    {
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            "pwnet-formats",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);
        string name = $"pwnet-described-{Environment.ProcessId}";
        await using var output = new PipeWireVideoOutput(ctx, name, Width, Height);
        output.FillFrame += (_, _, _, _, _, _) => false;
        output.Connect(options: new() { AutoConnect = false });
        uint node = await output.WaitForNodeIdAsync(cts.Token);

        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);
        PipeWireNode listed = registry.Nodes.Single(n => n.Id == node);
        Assert.AreEqual(name, listed.Description);
    }
}
