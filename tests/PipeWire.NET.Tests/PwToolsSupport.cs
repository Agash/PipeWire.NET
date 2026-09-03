using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

/// <summary>
/// Drives PipeWire's own command-line tools so tests can act on the graph as a third party.
/// </summary>
/// <remarks>
/// Everything here is a separate process holding its own connection to the daemon, which is the
/// point: a graph the library created must be visible to, and mutable by, tools that know nothing
/// about it. That is the difference between "our library agrees with itself" and "our library
/// agrees with PipeWire". These ship with pipewire itself, so CI already has them wherever the
/// daemon job runs.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class PwTools
{
    // Resolved rather than hardcoded: /usr/bin is one distribution's layout, and CI images and
    // Nix-style prefixes put these elsewhere. An env override lets a runner point at a specific
    // build without patching the tests.
    private static readonly string? PwLink = Resolve("pw-link");
    private static readonly string? PwCli = Resolve("pw-cli");
    private static readonly string? PwLoopback = Resolve("pw-loopback");
    private static readonly string? PwMetadata = Resolve("pw-metadata");

    /// <summary>The tool's path, or an assertion that skips the test when it is not installed.</summary>
    private static string Need(string? path, string tool)
    {
        if (path is null) Assert.Inconclusive($"{tool} not present.");
        return path;
    }

    private static string? Resolve(string tool)
    {
        string envKey = "PWNET_TEST_" + tool.Replace('-', '_').ToUpperInvariant();
        if (Environment.GetEnvironmentVariable(envKey) is { Length: > 0 } overridden)
            return File.Exists(overridden) ? overridden : null;

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, tool);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>True when the pw-* tools are present.</summary>
    public static bool IsAvailable { get; } =
        OperatingSystem.IsLinux() && PwLink is not null && PwCli is not null;

    public static void Require()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
        if (!IsAvailable)
            Assert.Inconclusive("pw-link / pw-cli not present - skipping third-party graph test.");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string exe, string[] args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exe}");

        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            await proc.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // Killed on any cancellation, not only the timeout. Leaving the child alive when the
            // caller's token fires is how ghost gst-launch and pw-loopback processes survive a run
            // and poison every test after it.
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }

            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{exe} did not exit");
        }

        return (proc.ExitCode, await stdout, await stderr);
    }

    /// <summary>Sets a node's volume through pw-cli, as a client that is not this library.</summary>
    /// <remarks>
    /// The POD is written in pw-cli's own textual syntax rather than built here, so what reaches the
    /// daemon has been through a different encoder than the library's.
    /// </remarks>
    public static async Task SetNodeVolumeAsync(uint nodeId, float volume, CancellationToken ct)
    {
        string pod = $"Props: {{ volume: {volume.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}";
        await RunAsync(Need(PwCli, "pw-cli"), ["set-param", nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Props", pod], ct);
    }

    /// <summary>Writes a metadata entry through pw-metadata.</summary>
    public static async Task SetMetadataAsync(string key, string value, CancellationToken ct)
    {
        if (PwMetadata is null)
            Assert.Inconclusive("pw-metadata not present.");

        await RunAsync(Need(PwMetadata, "pw-metadata"), ["-n", "default", "0", key, value, "Spa:String"], ct);
    }

    /// <summary>Clears every entry in a metadata store, from a process that is not us.</summary>
    /// <remarks>
    /// pw-metadata with a name and no key is the store-wide clear. Which subject the daemon puts on
    /// the resulting event is the thing worth observing, and only a third process can produce it.
    /// </remarks>
    public static async Task ClearMetadataAsync(string storeName, CancellationToken ct)
    {
        if (PwMetadata is null)
            Assert.Inconclusive("pw-metadata not present.");

        (int code, _, string err) = await RunAsync(Need(PwMetadata, "pw-metadata"), ["-n", storeName, "-d"], ct);
        if (code != 0)
            throw new InvalidOperationException($"pw-metadata -n {storeName} -d failed ({code}): {err}");
    }

    /// <summary>Links two ports by name, the way a user would from a terminal.</summary>
    /// <remarks><c>-w</c> waits for the attempt so the call is not merely fire-and-forget.</remarks>
    public static async Task LinkAsync(string outputPort, string inputPort, CancellationToken ct)
    {
        (int code, _, string err) = await RunAsync(Need(PwLink, "pw-link"), ["-w", outputPort, inputPort], ct);
        if (code != 0)
            throw new InvalidOperationException($"pw-link {outputPort} {inputPort} failed ({code}): {err}");
    }

    /// <summary>Disconnects a link by its id.</summary>
    public static async Task DisconnectAsync(uint linkId, CancellationToken ct)
    {
        (int code, _, string err) = await RunAsync(Need(PwLink, "pw-link"), ["-d", linkId.ToString()], ct);
        if (code != 0)
            throw new InvalidOperationException($"pw-link -d {linkId} failed ({code}): {err}");
    }

    /// <summary>Every link pw-link can see, as (linkId, outputPortId, inputPortId).</summary>
    /// <remarks>
    /// Parses the <c>-l -I</c> listing, whose shape is an output port line followed by indented
    /// <c>id |-&gt; id name</c> lines for each link leaving it.
    /// </remarks>
    public static async Task<List<(uint Link, uint Output, uint Input)>> ListLinksAsync(CancellationToken ct)
    {
        (_, string output, _) = await RunAsync(Need(PwLink, "pw-link"), ["-l", "-I"], ct);

        var links = new List<(uint, uint, uint)>();
        uint currentOutput = 0;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;

            string trimmed = line.TrimStart();

            // By the arrow, not by the indent. pw-link right-aligns ids to four columns, so matching
            // by indent misreads link lines as port lines once ids reach four digits.
            bool isLink = trimmed.Contains("|->", StringComparison.Ordinal)
                          || trimmed.Contains("|<-", StringComparison.Ordinal);

            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (!isLink)
            {
                // "  103 probe_a:monitor_FL" - the port whose links follow.
                if (uint.TryParse(parts[0], out uint portId)) currentOutput = portId;
                continue;
            }

            // "   85   |->   92 probe_b:playback_FL"
            if (!uint.TryParse(parts[0], out uint linkId)) continue;
            int arrow = Array.FindIndex(parts, p => p is "|->" or "|<-");
            if (arrow < 0 || arrow + 1 >= parts.Length) continue;
            if (!uint.TryParse(parts[arrow + 1], out uint peer)) continue;

            // Only record the outgoing view so each link appears once.
            if (parts[arrow] == "|->") links.Add((linkId, currentOutput, peer));
        }

        return links;
    }

    /// <summary>Port ids by full <c>node:port</c> name, from the <c>-o</c>/<c>-i</c> listings.</summary>
    public static async Task<Dictionary<string, uint>> ListPortsAsync(bool outputs, CancellationToken ct)
    {
        (_, string text, _) = await RunAsync(Need(PwLink, "pw-link"), [outputs ? "-o" : "-i", "-I"], ct);

        var ports = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string[] parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && uint.TryParse(parts[0], out uint id))
                ports[parts[1]] = id;
        }
        return ports;
    }

    /// <summary>Destroys any global by id, as an outside process would.</summary>
    public static async Task DestroyAsync(uint id, CancellationToken ct)
    {
        (int code, _, string err) = await RunAsync(Need(PwCli, "pw-cli"), ["destroy", id.ToString()], ct);
        if (code != 0)
            throw new InvalidOperationException($"pw-cli destroy {id} failed ({code}): {err}");
    }

    /// <summary>
    /// A <c>pw-loopback</c> process: a real third-party node pair the library did not create.
    /// </summary>
    public static async Task<Loopback> StartLoopbackAsync(string name, CancellationToken ct)
    {
        if (PwLoopback is null)
            Assert.Inconclusive("pw-loopback not present.");

        var psi = new ProcessStartInfo(Need(PwLoopback, "pw-loopback"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(name);

        Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start pw-loopback");

        var loopback = new Loopback(proc, name);

        // Only the fast failure is checked here: a pw-loopback that cannot start is gone in
        // milliseconds, and its stderr is the reason. Waiting for the nodes to appear is the
        // caller's job, and every caller already does it against the registry, which is both the
        // thing under test and the only source that reports when the node is actually usable.
        try
        {
            await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(250), ct);
        }
        catch (TimeoutException)
        {
            // Still running after a quarter second, which is what starting successfully looks like.
            return loopback;
        }
        catch
        {
            // Anything else, cancellation above all, and the caller never receives the loopback to
            // dispose. The process and its two redirected pipes would then outlive the test that
            // started them, which is exactly what the soak's descriptor count reports and blames on
            // the library.
            await loopback.DisposeAsync();
            throw;
        }

        try
        {
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"pw-loopback '{name}' exited immediately with {proc.ExitCode}: {stderr}");
        }
        finally
        {
            await loopback.DisposeAsync();
        }
    }

    internal sealed class Loopback(Process proc, string name) : IAsyncDisposable
    {
        public string Name { get; } = name;

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
            catch (InvalidOperationException) { /* already gone */ }
            finally { proc.Dispose(); }

            return ValueTask.CompletedTask;
        }
    }
}
