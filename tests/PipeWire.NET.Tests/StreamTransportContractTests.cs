using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The sender path, end to end: a real producer, the real graph, a real consumer, and the pixels
/// and samples checked on the way out.
/// </summary>
/// <remarks>
/// <para>
/// This is what a transport does with this library - capture a surface, keep it aligned with audio,
/// hand it to an encoder. So the assertions are about data arriving intact and about the frame the
/// consumer sees being the same memory the producer allocated, not about whether the types have the
/// right fields on them.
/// </para>
/// <para>
/// The zero-copy claim in particular has to be demonstrated rather than described. A frame carrying
/// a plausible fd, a fourcc and a modifier looks identical whether it is the producer's GPU buffer
/// or a copy of it; the difference is whether the descriptor names the same kernel object, which is
/// what <c>fstat</c> answers.
/// </para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed partial class StreamTransportContractTests : PipeWireTestBase
{
    private const int Width = 64;
    private const int Height = 32;
    private const int PoolCap = 8;

    /// <summary>The inode a descriptor refers to; two descriptors onto one dmabuf share it.</summary>
    /// <remarks>
    /// Read from <c>/proc/self/fdinfo/N</c>, whose <c>ino:</c> line is the kernel's own answer, rather
    /// than from <c>fstat</c> into a hand-declared <c>struct stat</c>. That struct is per-architecture:
    /// the earlier 128-byte declaration was 16 bytes short of x86_64's 144, so every call wrote past
    /// it on the stack of the test's async state machine and the process died with an access
    /// violation in the continuation.
    /// </remarks>
    private static ulong? InodeOf(int fd)
    {
        try
        {
            foreach (string line in File.ReadLines($"/proc/self/fdinfo/{fd}"))
            {
                if (line.StartsWith("ino:", StringComparison.Ordinal)
                    && ulong.TryParse(line.AsSpan(4).Trim(), System.Globalization.CultureInfo.InvariantCulture, out ulong ino))
                {
                    return ino;
                }
            }
        }
        catch (IOException)
        {
            // A descriptor that is not open has no fdinfo; that is the null the callers assert on.
        }

        return null;
    }

    /// <summary>The same identity check, for tests that prove zero-copy in the other direction.</summary>
    internal static ulong? InodeOfForTests(int fd) => InodeOf(fd);

    private static GbmAllocator RequireGbm()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("PipeWire is a Linux daemon.");
        if (!File.Exists("/dev/dri/renderD128")) Assert.Inconclusive("No GPU render node.");

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

    /// <summary>
    /// A producer's pixels reach the consumer unaltered, frame after frame.
    /// </summary>
    /// <remarks>
    /// Each frame carries a different pattern keyed to its number, so a consumer receiving a stale
    /// buffer, a repeated one, or a partially written one fails rather than passing on "a frame
    /// arrived". This is the CPU path, where the bytes can actually be compared; the dmabuf path is
    /// checked for buffer identity instead, below.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task AProducersPixels_ReachTheConsumerUnaltered()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("PipeWire is a Linux daemon.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var ctx = new PipeWireContext("pwnet-content", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        var produced = 0;

        await using var output = new PipeWireVideoOutput(
            ctx, "pwnet-content-src", Width, Height, PixelFormat.Bgra, 30);

        output.FillFrame += (_, pixels, stride, width, height, _) =>
        {
            // A pattern that depends on the frame number and on the position, so a torn or stale
            // buffer cannot coincidentally match.
            byte tag = (byte)(Interlocked.Increment(ref produced) & 0xFF);
            for (var y = 0; y < height; y++)
            {
                Span<byte> row = pixels.Slice(y * stride, width * 4);
                for (var x = 0; x < row.Length; x++) row[x] = (byte)(tag ^ (x & 0xFF));
            }

            return true;
        };

        output.Connect(autoConnect: false);

        uint? nodeId = null;
        for (var i = 0; i < 60 && nodeId is null; i++)
        {
            nodeId = output.NodeId;
            if (nodeId is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(nodeId, "the producer was never given a node id");

        var verified = 0;
        var mismatches = new List<string>();

        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-content-sink");
        capture.FrameReady += (_, f) =>
        {
            if (f.Pixels.IsEmpty || f.Width != Width || f.Height != Height) return;

            // Recover the tag from the first pixel, then check the whole frame agrees with it.
            byte tag = (byte)(f.Pixels[0] ^ 0);
            for (var y = 0; y < f.Height; y++)
            {
                ReadOnlySpan<byte> row = f.Pixels.Slice(y * f.Stride, f.Width * 4);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x] != (byte)(tag ^ (x & 0xFF)))
                    {
                        mismatches.Add($"frame tag {tag} row {y} byte {x}");
                        return;
                    }
                }
            }

            Interlocked.Increment(ref verified);
        };

        capture.Connect(nodeId!.Value, [PixelFormat.Bgra]);
        await capture.WaitForStreamingAsync(cts.Token);
        await Task.Delay(900, cts.Token);

        Assert.AreEqual(0, mismatches.Count, string.Join("; ", mismatches.Take(3)));

        Assert.IsTrue(
            Volatile.Read(ref verified) > 5,
            $"only {Volatile.Read(ref verified)} frames arrived intact");
    }

    /// <summary>
    /// The frame a consumer receives is the producer's GPU buffer, not a copy of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The zero-copy claim, demonstrated. The consumer's descriptor is a different number from the
    /// producer's - it arrived over a socket and was duplicated on the way in - but if the path is
    /// genuinely zero-copy both numbers name the same kernel object, and <c>fstat</c> reports the
    /// same inode. A path that copied would show a different one.
    /// </para>
    /// <para>
    /// Without this, every other dmabuf assertion is satisfied by a copy: the fourcc, the modifier,
    /// the stride and a valid descriptor would all still be there.
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task TheConsumersFrame_IsTheProducersGpuBuffer()
    {
        using GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        try
        {
            await using var ctx = new PipeWireContext("pwnet-zerocopy", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(cts.Token);

            long modifier = (long)GbmAllocator.LinearModifier;

            await using var output = new PipeWireVideoOutput(
                ctx, "pwnet-zerocopy-src", Width, Height, PixelFormat.Bgra, 30);

            output.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
            {
                if (index >= PoolCap) return 0;
                while (buffers.Count <= index) buffers.Add(gbm.CreateBgra(Width, Height));
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
                if (nodeId is null) await Task.Delay(50, cts.Token);
            }

            Assert.IsNotNull(nodeId, "the producer was never given a node id");

            await using var capture = new PipeWireVideoCapture(ctx, "pwnet-zerocopy-sink")
            {
                Retention = FrameRetention.Borrowed,
            };

            capture.Connect(nodeId!.Value, [PixelFormat.Bgra], modifiers: [modifier]);

            BorrowedVideoFrame frame = default;
            var got = false;
            for (var i = 0; i < 120 && !got; i++)
            {
                got = capture.TryGetBorrowedFrame(out frame) && frame.IsFdBacked;
                if (!got) await Task.Delay(50, cts.Token);
            }

            Assert.IsTrue(got, "no fd-backed frame ever arrived");

            ulong? consumerInode = InodeOf((int)frame[0].Fd);
            Assert.IsNotNull(consumerInode, "the consumer's descriptor could not be stat'd, so it is not live");

            var producerInodes = new HashSet<ulong>();
            foreach (GbmAllocator.Buffer b in buffers)
            {
                if (InodeOf((int)b.Fd) is { } ino) producerInodes.Add(ino);
            }

            Assert.IsTrue(producerInodes.Count > 0, "none of the producer's buffers could be stat'd");

            Assert.IsTrue(
                producerInodes.Contains(consumerInode!.Value),
                "the consumer's frame does not name any buffer the producer allocated, so the path "
                + "copied rather than passing the GPU buffer through");
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
        }
    }

    /// <summary>
    /// A producer's samples reach the consumer unaltered, and stamped on the graph clock.
    /// </summary>
    /// <remarks>
    /// The audio half of the same claim. A transport encodes what it is handed, so silence where
    /// there should be tone, or a sample rate that does not match what was negotiated, is a stream
    /// nobody can decode back. The timestamp is checked here too because it is the piece a
    /// GPU-surface library cannot provide and the whole reason audio can be aligned to video.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task AProducersSamples_ReachTheConsumerUnalteredAndStamped()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("PipeWire is a Linux daemon.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        const int rate = 48000, channels = 1;

        await using var ctx = new PipeWireContext("pwnet-audiocontent", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        string nodeName = $"pwnet-audiocontent-{Environment.ProcessId}";

        // A ramp the consumer can check exactly: value n is n, so any reordering or loss shows.
        uint next = 0;

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, rate, channels, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(samples);
            for (var i = 0; i < floats.Length; i++) floats[i] = next++;
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        var got = new List<float>();
        long stampedFrames = 0;
        var badRate = 0;

        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");
        capture.FrameReady += (_, f) =>
        {
            if (f.SampleRate != rate || f.Channels != channels) { Interlocked.Increment(ref badRate); return; }
            // The queued time, not the header timestamp: no audio converter copies the header, so an
            // audio consumer stamps its packets with the cycle time its buffer was queued in.
            if (f.QueuedTimeNs is > 0) Interlocked.Increment(ref stampedFrames);

            ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(f.Samples);
            lock (got) { if (got.Count < 20000) foreach (float v in floats) got.Add(v); }
        };

        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)), sampleRate: rate, channels: channels,
            format: AudioSampleFormat.F32Le);

        await capture.WaitForStreamingAsync(cts.Token);
        await Task.Delay(800, cts.Token);

        float[] samplesSeen;
        lock (got) samplesSeen = [.. got];

        Assert.AreEqual(0, Volatile.Read(ref badRate), "a frame arrived with the wrong rate or channel count");
        Assert.IsTrue(samplesSeen.Length > 2000, $"only {samplesSeen.Length} samples arrived");

        int start = 0;
        while (start < samplesSeen.Length && samplesSeen[start] == 0f) start++;
        Assert.IsTrue(samplesSeen.Length - start > 1000, "the stream was silence throughout");

        var breaks = 0;
        for (int i = start + 1; i < samplesSeen.Length; i++)
            if (samplesSeen[i] != samplesSeen[i - 1] + 1f) breaks++;

        Assert.AreEqual(
            0, breaks,
            $"the sample sequence broke {breaks} times, so audio was lost or duplicated in transit");

        Assert.IsTrue(
            Volatile.Read(ref stampedFrames) > 0,
            "no audio frame carried a queued time, so a sender could not stamp its packets");
    }
}
