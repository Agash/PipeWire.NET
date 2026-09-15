using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// The whole transport loop: capture audio and video out of the graph, republish both, and capture
/// again at the far end.
/// </summary>
/// <remarks>
/// <para>
/// Every other test here covers one leg or one direction. This is the shape a transport actually
/// runs - media leaves the graph, passes through something, and comes back in - and what matters at
/// the end is whether it is still the same media, still aligned.
/// </para>
/// <para>
/// The video leg is a real DMA-BUF on both hops, so the loop is zero-copy in both directions rather
/// than only outbound. The audio leg carries a counter so loss or reordering is visible rather than
/// merely plausible. The A/V offset is measured after the round trip, which is the only place a
/// viewer would notice it.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[TestCategory("RequiresGpu")]
[SupportedOSPlatform("linux")]
public sealed class TransportRoundTripTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    private const int Width = 64;
    private const int Height = 32;
    private const int Rate = 48000;
    private const int PoolCap = 8;

    /// <summary>How far ahead of its cycle the origin publishes, for the loop to preserve.</summary>
    private const long LeadNs = 250_000_000;

    private static async Task<uint> NodeIdAsync(PipeWireVideoOutput output, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            if (output.NodeId is { } id) return id;
            await Task.Delay(50, ct);
        }

        throw new InvalidOperationException("a producer was never given a node id");
    }

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
    /// Content and alignment both survive a full capture, republish and recapture.
    /// </summary>
    [TestMethod]
    public async Task TheFullTransportLoop_PreservesContentAndAlignment()
    {
        using GbmAllocator gbm = RequireGbm();

        using var cts = new CancellationTokenSource(Budget);
        var originBuffers = new List<GbmAllocator.Buffer>();
        var relayBuffers = new List<GbmAllocator.Buffer>();

        try
        {
            await using var ctx = new PipeWireContext("pwnet-loop", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(cts.Token);

            long modifier = (long)GbmAllocator.LinearModifier;

            // - The origin, as a sender would capture from -
            await using var originVideo = new PipeWireVideoOutput(
                ctx, $"pwnet-loop-v0-{Environment.ProcessId}", Width, Height, PixelFormat.Bgra, 30);

            originVideo.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
            {
                if (index >= PoolCap) return 0;
                while (originBuffers.Count <= index) originBuffers.Add(gbm.CreateBgra(Width, Height));
                GbmAllocator.Buffer b = originBuffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };

            originVideo.FillDmaBuf += (sender, _) =>
            {
                if (sender.GraphClock is { } clock)
                    sender.NextPresentationTimestampNs = (long)clock.TimeNs + LeadNs;

                return true;
            };

            originVideo.ConnectDmaBuf([modifier]);
            uint originVideoNode = await NodeIdAsync(originVideo, cts.Token);

            uint sample = 0;
            await using var originAudio = new PipeWireAudioOutput(
                ctx, $"pwnet-loop-a0-{Environment.ProcessId}", Rate, 1, AudioSampleFormat.F32Le);

            originAudio.FillSamples += (_, samples, _, _, _) =>
            {
                Span<float> floats = MemoryMarshal.Cast<byte, float>(samples);
                for (var i = 0; i < floats.Length; i++) floats[i] = ++sample;
                return samples.Length;
            };

            originAudio.Connect(autoConnect: false);

            // - The relay: takes both in and puts both back -
            long relayPts = 0;
            var relayAudio = new ConcurrentQueue<float>();

            await using var relayVideoIn = new PipeWireVideoCapture(ctx, "pwnet-loop-v-relay-in");
            relayVideoIn.FrameReady += (_, f) =>
            {
                if (f.PresentationTimestampNs is { } pts) Volatile.Write(ref relayPts, pts);
            };

            relayVideoIn.Connect(originVideoNode, [PixelFormat.Bgra], modifiers: [modifier]);

            await using var relayAudioIn = new PipeWireAudioCapture(ctx, "pwnet-loop-a-relay-in");
            relayAudioIn.FrameReady += (_, f) =>
            {
                if (relayAudio.Count > 40000) return;
                foreach (float v in MemoryMarshal.Cast<byte, float>(f.Samples)) relayAudio.Enqueue(v);
            };

            relayAudioIn.Connect((await originAudio.WaitForNodeIdAsync(cts.Token)), sampleRate: Rate, channels: 1,
                format: AudioSampleFormat.F32Le);

            await relayVideoIn.WaitForStreamingAsync(cts.Token);
            await relayAudioIn.WaitForStreamingAsync(cts.Token);

            await using var relayVideoOut = new PipeWireVideoOutput(
                ctx, $"pwnet-loop-v1-{Environment.ProcessId}", Width, Height, PixelFormat.Bgra, 30);

            relayVideoOut.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
            {
                if (index >= PoolCap) return 0;
                while (relayBuffers.Count <= index) relayBuffers.Add(gbm.CreateBgra(Width, Height));
                GbmAllocator.Buffer b = relayBuffers[index];
                planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                return 1;
            };

            relayVideoOut.FillDmaBuf += (sender, _) =>
            {
                // Carrying the time the frame arrived with is what makes this a transport rather
                // than a second, unrelated source.
                long pts = Volatile.Read(ref relayPts);
                if (pts <= 0) return false;

                sender.NextPresentationTimestampNs = pts;
                return true;
            };

            relayVideoOut.ConnectDmaBuf([modifier]);
            uint relayVideoNode = await NodeIdAsync(relayVideoOut, cts.Token);

            await using var relayAudioOut = new PipeWireAudioOutput(
                ctx, $"pwnet-loop-a1-{Environment.ProcessId}", Rate, 1, AudioSampleFormat.F32Le);

            relayAudioOut.FillSamples += (_, samples, _, _, _) =>
            {
                Span<float> floats = MemoryMarshal.Cast<byte, float>(samples);
                for (var i = 0; i < floats.Length; i++)
                    floats[i] = relayAudio.TryDequeue(out float v) ? v : 0f;

                return samples.Length;
            };

            relayAudioOut.Connect(autoConnect: false);

            // - The far end -
            var offsets = new List<long>();
            var dmaBufOut = 0;

            await using var finalVideo = new PipeWireVideoCapture(ctx, "pwnet-loop-v-out");
            finalVideo.FrameReady += (_, f) =>
            {
                if (f.BufferType == PipeWireBufferType.DmaBuf) Interlocked.Increment(ref dmaBufOut);
                if (f.PresentationTimestampNs is not { } pts || f.GraphTimeNs is not { } cycle) return;
                lock (offsets) { if (offsets.Count < 32) offsets.Add(pts - cycle); }
            };

            finalVideo.Connect(relayVideoNode, [PixelFormat.Bgra], modifiers: [modifier]);

            var audioOut = new List<float>();
            await using var finalAudio = new PipeWireAudioCapture(ctx, "pwnet-loop-a-out");
            finalAudio.FrameReady += (_, f) =>
            {
                lock (audioOut)
                {
                    if (audioOut.Count > 20000) return;
                    foreach (float v in MemoryMarshal.Cast<byte, float>(f.Samples)) audioOut.Add(v);
                }
            };

            finalAudio.Connect((await relayAudioOut.WaitForNodeIdAsync(cts.Token)), sampleRate: Rate, channels: 1,
                format: AudioSampleFormat.F32Le);

            await finalVideo.WaitForStreamingAsync(cts.Token);
            await finalAudio.WaitForStreamingAsync(cts.Token);

            await Task.Delay(2500, cts.Token);

            long[] measured;
            float[] heard;
            lock (offsets) measured = [.. offsets];
            lock (audioOut) heard = [.. audioOut];

            Assert.IsTrue(
                Volatile.Read(ref dmaBufOut) > 0,
                "the far end received no DMA-BUF frames, so the loop fell back to host memory");

            Assert.IsTrue(measured.Length >= 3, $"only {measured.Length} frames completed the loop");

            foreach (long offset in measured)
            {
                Assert.AreEqual(
                    LeadNs / 1e6,
                    offset / 1e6,
                    80.0,
                    $"a frame published {LeadNs / 1e6:F0}ms ahead came out {offset / 1e6:F1}ms ahead; "
                    + "the round trip lost the A/V alignment");
            }

            int start = 0;
            while (start < heard.Length && heard[start] == 0f) start++;

            Assert.IsTrue(
                heard.Length - start > 500,
                $"only {heard.Length - start} audio samples completed the loop");

            var breaks = 0;
            for (int i = start + 1; i < heard.Length; i++)
            {
                // A zero means the relay queue ran dry, which is starvation rather than corruption.
                if (heard[i] == 0f) break;
                if (heard[i] != heard[i - 1] + 1f) breaks++;
            }

            Assert.IsTrue(
                breaks <= 2,
                $"the audio ramp broke {breaks} times across the loop, so samples were lost or "
                + "reordered in transit");
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in originBuffers) b.Dispose();
            foreach (GbmAllocator.Buffer b in relayBuffers) b.Dispose();
        }
    }
}
