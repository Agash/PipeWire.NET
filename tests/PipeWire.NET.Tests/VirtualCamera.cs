using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

/// <summary>
/// A virtual camera, provisioned by the test rather than by whoever set the machine up.
/// </summary>
/// <remarks>
/// <para>
/// Upstream's <c>local-v4l2</c> needs a camera, and a headless test box has none. <c>v4l2loopback</c>
/// makes one: the module creates a <c>/dev/videoN</c> that anything can write to and anything can
/// read from, and <c>ffmpeg</c> feeds it a test pattern. PipeWire's own v4l2 SPA plugin then
/// enumerates it like any other camera.
/// </para>
/// <para>
/// Self-provisioning on purpose. A camera that has to be set up by hand is one that quietly stops
/// existing after a reboot, and then the test that depends on it starts passing by skipping - which
/// is worse than failing. Everything here is set up and torn down per run, and anything that does
/// not work reports <see cref="Assert.Inconclusive(string)"/> with the reason rather than failing
/// the library for the machine's shortcomings.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class VirtualCamera : IAsyncDisposable
{
    /// <summary>A number well clear of any real device, so a real camera is never disturbed.</summary>
    private const int VideoNr = 42;

    /// <summary>The label the v4l2 node carries, which is how the graph node is recognised.</summary>
    public const string CardLabel = "pwnet-virtual-cam";

    private readonly Process _feeder;

    private VirtualCamera(Process feeder, string devicePath)
    {
        _feeder = feeder;
        DevicePath = devicePath;
    }

    /// <summary>The device the camera appears at.</summary>
    public string DevicePath { get; }

    private static bool Run(string file, string args, out string output)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (p is null) { output = "could not start " + file; return false; }

            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(20_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }

    /// <summary>Whether the loaded module advertises capture only while a writer is attached.</summary>
    private static bool LoadedWithExclusiveCaps()
    {
        const string param = "/sys/module/v4l2loopback/parameters/exclusive_caps";
        return File.Exists(param) && File.ReadAllText(param).TrimStart().StartsWith('Y');
    }

    /// <summary>
    /// Creates the loopback device and starts feeding it, or reports why it could not.
    /// </summary>
    /// <remarks>
    /// Idempotent: a device left over from an earlier run is reused rather than fought over, which
    /// is what makes repeated runs on one machine behave the same as the first.
    /// </remarks>
    public static async Task<VirtualCamera> StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("v4l2 is a Linux interface.");

        string device = $"/dev/video{VideoNr.ToString(CultureInfo.InvariantCulture)}";

        // exclusive_caps=0, so the device reports VIDEO_CAPTURE from the moment it exists. With
        // exclusive_caps=1 it reports capture only once a writer is attached, but the session
        // manager's v4l2 monitor opens it as soon as it appears - before ffmpeg does - and SPA
        // creates a capture node only for a device that reports capture then (v4l2-device.c,
        // spa_v4l2_is_capture). Nothing re-checks when the writer arrives, so an already-running
        // session manager never produced a node at all. A device left loaded the old way is
        // reloaded rather than reused, for the same reason.
        if (File.Exists(device) && LoadedWithExclusiveCaps())
            _ = Run("sudo", "-n modprobe -r v4l2loopback", out _);

        if (!File.Exists(device))
        {
            if (!Run("sudo", $"-n modprobe v4l2loopback devices=1 video_nr={VideoNr} "
                             + $"card_label={CardLabel} exclusive_caps=0", out string modprobe))
            {
                Assert.Inconclusive($"could not load v4l2loopback: {modprobe.Trim()}");
            }

            for (var i = 0; i < 50 && !File.Exists(device); i++)
                await Task.Delay(100, ct);
        }

        if (!File.Exists(device))
            Assert.Inconclusive($"{device} did not appear after loading v4l2loopback.");

        // ffmpeg rather than gst: this box's gst has neither v4l2src nor v4l2sink.
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in new[]
                 {
                     "-nostdin", "-loglevel", "error",
                     "-re", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30",
                     "-pix_fmt", "yuyv422", "-f", "v4l2", device,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        Process? feeder = Process.Start(psi);
        if (feeder is null) Assert.Inconclusive("could not start ffmpeg to feed the virtual camera.");

        // Drained, not merely redirected. A child whose pipe fills up blocks writing to it and then
        // never exits, which hangs teardown - and with the test host holding the read end, the whole
        // run stops rather than the one test.
        var stderr = new System.Text.StringBuilder();
        feeder!.OutputDataReceived += (_, _) => { };
        feeder.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (stderr) { if (stderr.Length < 4000) stderr.AppendLine(e.Data); }
        };

        feeder.BeginOutputReadLine();
        feeder.BeginErrorReadLine();

        var camera = new VirtualCamera(feeder, device);

        // A moment for ffmpeg to open the device; if it failed it has already exited.
        await Task.Delay(700, ct);

        if (feeder.HasExited)
        {
            string err;
            lock (stderr) err = stderr.ToString();
            Assert.Inconclusive($"ffmpeg could not feed {device}: {err.Trim()}");
        }

        return camera;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_feeder.HasExited)
            {
                _feeder.Kill(entireProcessTree: true);
                await _feeder.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone. Nothing to do, and nothing worth reporting on a teardown path.
        }

        _feeder.Dispose();

        // The module stays loaded deliberately. Unloading it races any other client that has the
        // device open, and leaving it costs nothing: the next run reuses the same device.
    }
}
