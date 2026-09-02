using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Launches real PipeWire source nodes via <c>gst-launch-1.0 ... ! pipewiresink</c> so the
/// capture API can be tested against an external producer with real content (SMPTE bars,
/// test tones) - the actual StreamWeaver use case, not our own output looped back.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class GstTestSource : IAsyncDisposable
{
    private const string GstLaunch = "/usr/bin/gst-launch-1.0";

    private readonly Process _proc;
    private GstTestSource(Process proc) => _proc = proc;

    /// <summary>
    /// The registry id of the node this producer published, so a consumer can target it directly
    /// instead of looking it up by name again.
    /// </summary>
    public uint NodeId { get; private set; }

    /// <summary>True when gst-launch-1.0 and the pipewiresink element are usable.</summary>
    public static bool IsAvailable { get; } = ComputeAvailable();

    private static bool ComputeAvailable()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(GstLaunch)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/gst-inspect-1.0", "pipewiresink")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Skips the calling test (Inconclusive) when GStreamer/pipewiresink isn't present.</summary>
    public static void RequireGStreamer()
    {
        if (!IsAvailable)
            Assert.Inconclusive("GStreamer (gst-launch-1.0 + pipewiresink) not available - skipping real-source test.");
    }

    /// <summary>
    /// Starts a gst pipeline whose tail is a named <c>pipewiresink</c>, then waits until the
    /// node appears in the registry. <paramref name="pipelineHead"/> is everything before the sink,
    /// e.g. <c>videotestsrc is-live=true ! video/x-raw,format=BGRA,width=320,height=240,framerate=30/1</c>.
    /// </summary>
    public static async Task<GstTestSource> StartAsync(
        PipeWireContext ctx, string nodeName, string pipelineHead, string mediaClass,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(GstLaunch)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-q");
        foreach (var tok in pipelineHead.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(tok);
        psi.ArgumentList.Add("!");
        psi.ArgumentList.Add("pipewiresink");
        // mode=provide makes pipewiresink act as a real source node that consumers pull from. Without it
        // an Audio/Source branch advertises a node but never serves samples (a video source happens to
        // still produce), so an audio capture connects, reaches Streaming, yet no buffer ever flows.
        psi.ArgumentList.Add("mode=provide");
        psi.ArgumentList.Add($"stream-properties=props,node.name={nodeName},media.class={mediaClass}");

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start gst-launch-1.0.");
        var source = new GstTestSource(proc);
        try
        {
            PipeWireNode? node = await WaitForNodeAsync(ctx, nodeName, timeout ?? TimeSpan.FromSeconds(8));
            if (node is null)
            {
                string err = await proc.StandardError.ReadToEndAsync();
                await source.DisposeAsync();
                throw new InvalidOperationException(
                    $"gst node '{nodeName}' did not appear. gst stderr:\n{err}");
            }

            // Assigned here or not at all: the property is how a consumer targets this producer by
            // id, and leaving it zero silently points every such lookup at nothing.
            source.NodeId = node.NodeId;
            return source;
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Starts ONE gst pipeline with two named pipewiresink branches. Because they live in a
    /// single pipeline they share one clock, so the two PipeWire nodes carry coherent
    /// presentation timestamps - the basis for A/V sync.
    /// </summary>
    public static async Task<GstTestSource> StartTwoAsync(
        PipeWireContext ctx,
        (string Head, string Node) video,
        (string Head, string Node) audio,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(GstLaunch)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-q");
        AppendBranch(psi, video.Head, video.Node, "Video/Source");
        AppendBranch(psi, audio.Head, audio.Node, "Audio/Source");

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start gst-launch-1.0.");
        var source = new GstTestSource(proc);
        try
        {
            var t = timeout ?? TimeSpan.FromSeconds(10);
            if (await WaitForNodeAsync(ctx, video.Node, t) is null ||
                await WaitForNodeAsync(ctx, audio.Node, t) is null)
            {
                string err = await proc.StandardError.ReadToEndAsync();
                await source.DisposeAsync();
                throw new InvalidOperationException($"gst A/V nodes did not appear. stderr:\n{err}");
            }
            return source;
        }
        catch { await source.DisposeAsync(); throw; }
    }

    private static void AppendBranch(ProcessStartInfo psi, string head, string node, string mediaClass)
    {
        foreach (var tok in head.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(tok);
        psi.ArgumentList.Add("!");
        psi.ArgumentList.Add("pipewiresink");
        psi.ArgumentList.Add("mode=provide"); // see StartAsync: a source branch must provide to serve samples.
        psi.ArgumentList.Add($"stream-properties=props,node.name={node},media.class={mediaClass}");
    }

    private static async Task<PipeWireNode?> WaitForNodeAsync(PipeWireContext ctx, string nodeName, TimeSpan timeout)
    {
        await using var reg = new PipeWireRegistry(ctx);
        var found = new TaskCompletionSource<PipeWireNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        reg.NodeAdded += s => { if (s.NodeName == nodeName) found.TrySetResult(s); };

        foreach (var s in reg.Nodes)
            if (s.NodeName == nodeName) return s;

        try { return await found.Task.WaitAsync(timeout); }
        catch (TimeoutException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
            await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { /* best-effort teardown */ }
        finally { _proc.Dispose(); }
    }
}
