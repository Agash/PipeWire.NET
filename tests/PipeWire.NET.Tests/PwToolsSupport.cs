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
    private const string PwLink = "/usr/bin/pw-link";
    private const string PwCli = "/usr/bin/pw-cli";
    private const string PwLoopback = "/usr/bin/pw-loopback";

    /// <summary>True when the pw-* tools are present.</summary>
    public static bool IsAvailable { get; } =
        OperatingSystem.IsLinux() && File.Exists(PwLink) && File.Exists(PwCli);

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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new TimeoutException($"{exe} did not exit");
        }

        return (proc.ExitCode, await stdout, await stderr);
    }

    /// <summary>Links two ports by name, the way a user would from a terminal.</summary>
    /// <remarks><c>-w</c> waits for the attempt so the call is not merely fire-and-forget.</remarks>
    public static async Task LinkAsync(string outputPort, string inputPort, CancellationToken ct)
    {
        (int code, _, string err) = await RunAsync(PwLink, ["-w", outputPort, inputPort], ct);
        if (code != 0)
            throw new InvalidOperationException($"pw-link {outputPort} {inputPort} failed ({code}): {err}");
    }

    /// <summary>Disconnects a link by its id.</summary>
    public static async Task DisconnectAsync(uint linkId, CancellationToken ct)
    {
        (int code, _, string err) = await RunAsync(PwLink, ["-d", linkId.ToString()], ct);
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
        (_, string output, _) = await RunAsync(PwLink, ["-l", "-I"], ct);

        var links = new List<(uint, uint, uint)>();
        uint currentOutput = 0;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;

            string trimmed = line.TrimStart();
            bool indented = line.Length != trimmed.Length && (trimmed.Contains("|->") || trimmed.Contains("|<-"));

            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (!indented)
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
        (_, string text, _) = await RunAsync(PwLink, [outputs ? "-o" : "-i", "-I"], ct);

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
        (int code, _, string err) = await RunAsync(PwCli, ["destroy", id.ToString()], ct);
        if (code != 0)
            throw new InvalidOperationException($"pw-cli destroy {id} failed ({code}): {err}");
    }

    /// <summary>
    /// A <c>pw-loopback</c> process: a real third-party node pair the library did not create.
    /// </summary>
    public static async Task<Loopback> StartLoopbackAsync(string name, CancellationToken ct)
    {
        if (!File.Exists(PwLoopback))
            Assert.Inconclusive("pw-loopback not present.");

        var psi = new ProcessStartInfo(PwLoopback)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(name);

        Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start pw-loopback");

        await Task.Delay(600, ct);   // give it time to publish its nodes
        return new Loopback(proc, name);
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
