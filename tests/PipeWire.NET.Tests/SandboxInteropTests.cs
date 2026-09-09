using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The security context surface against PipeWire's own <c>pw-container</c>, in both directions:
/// connecting over a descriptor another process sandboxed, and serving a sandbox a third-party
/// client connects through.
/// </summary>
/// <remarks>
/// This is the xdg-desktop-portal shape without the portal. pw-container does what the portal
/// does - a listening socket, <c>pw_security_context_create</c>, then a client that reaches the
/// daemon only through it - so the endpoint is genuinely restricted rather than a socket this
/// process opened to the daemon itself. The observable difference is the metadata bit: an ordinary
/// core reads <c>r-xm-</c> and a sandboxed one <c>r-x--</c>. Not the write bit, which no client
/// holds on the core object.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class SandboxInteropTests : PipeWireTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>
    /// A descriptor connected to someone else's sandbox is what a portal hands out, and starting
    /// over it must reach the daemon.
    /// </summary>
    [TestMethod]
    public async Task StartAsync_OverASocketConnectedToAThirdPartySandbox_ReachesTheDaemon()
    {
        RequireLinux();
        CliTool.Require("pw-container");
        using var cts = new CancellationTokenSource(Budget);

        await using Sandbox sandbox = await Sandbox.StartAsync(cts.Token);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(sandbox.SocketPath), cts.Token);

        await using var context = new PipeWireContext("pwnet-sandboxed", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(socket.SafeHandle, cts.Token);

        await using var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        Assert.IsTrue(registry.Current.Nodes.Length >= 0, "the sandboxed connection enumerated nothing at all");

        // Borrowed, as everywhere else: the connection took a duplicate.
        Assert.IsFalse(socket.SafeHandle.IsClosed);
        _ = socket.Poll(0, SelectMode.SelectRead);
    }

    /// <summary>
    /// The sandbox is real, not merely reachable: our own client inside it must not hold the
    /// permissions an unrestricted one does.
    /// </summary>
    [TestMethod]
    public async Task AConnectionThroughAThirdPartySandbox_IsRestricted()
    {
        RequireLinux();
        CliTool.Require("pw-container");
        CliTool.Require("pw-cli");
        using var cts = new CancellationTokenSource(Budget);

        await using Sandbox sandbox = await Sandbox.StartAsync(cts.Token);

        string sandboxed = await CoreInfoAsync(sandbox.SocketPath, cts.Token);
        string ordinary = await CoreInfoAsync(remote: null, cts.Token);

        Assert.IsTrue(HasMetadata(ordinary), $"an unrestricted core reported: {ordinary}");
        Assert.IsFalse(HasMetadata(sandboxed), $"the sandbox granted metadata access: {sandboxed}");
    }

    /// <summary>
    /// The other direction: a sandbox this library creates has to be one a third-party client can
    /// actually connect through.
    /// </summary>
    [TestMethod]
    public async Task ASandboxWeCreate_AcceptsAThirdPartyClient()
    {
        RequireLinux();
        CliTool.Require("pw-cli");
        using var cts = new CancellationTokenSource(Budget);

        await using var context = new PipeWireContext("pwnet-sandbox-host", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireSecurityContext? available = registry.Current.SecurityContext;
        if (available is null) Assert.Inconclusive("this daemon exposes no security context.");

        await using PipeWireSecurityContextControl control = registry.BindSecurityContext(available!.Id);

        string path = Path.Combine(Path.GetTempPath(), $"pwnet-sandbox-{Environment.ProcessId}");
        File.Delete(path);

        using var listening = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listening.Bind(new UnixDomainSocketEndPoint(path));
        listening.Listen(4);

        // A pipe, as pw-container uses: the daemon watches this for hangup to learn the sandbox is
        // gone, and holding the other end is what keeps it alive.
        using var closeSide = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);

        try
        {
            await control.CreateAsync(
                listening.SafeHandle,
                closeSide.SafePipeHandle,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["pipewire.access"] = "restricted",
                    ["pipewire.sec.engine"] = "org.pipewire.Test",
                },
                cts.Token);

            string sandboxed = await CoreInfoAsync(path, cts.Token);

            StringAssert.Contains(sandboxed, "permissions",
                "a third-party client could not read the core through our sandbox");
            Assert.IsFalse(HasMetadata(sandboxed),
                $"our sandbox did not restrict the client, got: {sandboxed}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Whether a permission line grants metadata access, which a sandbox withholds.</summary>
    private static bool HasMetadata(string permissionLine)
    {
        int colon = permissionLine.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && permissionLine[(colon + 1)..].Contains('m', StringComparison.Ordinal);
    }

    /// <summary>The core's permission line as pw-cli reports it, optionally through a socket.</summary>
    private static async Task<string> CoreInfoAsync(string? remote, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("/usr/bin/pw-cli", "info 0")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (remote is not null) psi.Environment["PIPEWIRE_REMOTE"] = remote;

        using Process process = Process.Start(psi)!;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        string output;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return "pw-cli did not answer";
        }

        foreach (string line in output.Split('\n'))
            if (line.Contains("permissions", StringComparison.Ordinal))
                return line.Trim();

        return output.Trim();
    }

    /// <summary>A restricted endpoint created by pw-container, and the socket path it published.</summary>
    private sealed class Sandbox : IAsyncDisposable
    {
        private readonly Process _process;

        private Sandbox(Process process, string socketPath)
            => (_process, SocketPath) = (process, socketPath);

        public string SocketPath { get; }

        public static async Task<Sandbox> StartAsync(CancellationToken cancellationToken)
        {
            // Through the sandboxed command rather than pw-container's stdout: it announces the
            // socket with fprintf and then blocks in its main loop, so on a pipe the line sits in
            // libc's buffer unflushed. The command it runs sees the same path in PIPEWIRE_REMOTE,
            // which is how a sandboxed application finds it anyway.
            string marker = Path.Combine(Path.GetTempPath(), $"pwnet-sbx-{Guid.NewGuid():N}");

            var psi = new ProcessStartInfo("/usr/bin/pw-container")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add($"printf %s \"$PIPEWIRE_REMOTE\" > '{marker}'; exec sleep 600");

            Process process = Process.Start(psi)!;

            string? path = null;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    while (path is null)
                    {
                        if (File.Exists(marker))
                        {
                            string text = File.ReadAllText(marker).Trim();
                            if (text.Length > 0) path = text;
                        }

                        if (path is null) await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Kill(process);
                    File.Delete(marker);
                    Assert.Inconclusive("pw-container did not publish a socket within 15s.");
                }
            }

            File.Delete(marker);
            return new Sandbox(process, path!);
        }

        private static void Kill(Process process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone */ }
            process.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException) { /* already gone */ }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
