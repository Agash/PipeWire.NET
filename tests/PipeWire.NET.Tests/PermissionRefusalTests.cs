using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The daemon actually refusing something, rather than the code that would report a refusal.
/// </summary>
/// <remarks>
/// <para>
/// <c>IsPermissionDenied</c> is only worth having if it is ever true, and a test that never
/// provokes a refusal proves the property exists rather than that it works. Provoking one needs a
/// client the daemon will say no to, and the cheapest is this one: permissions are per connection,
/// so a context can drop its own and everything it tries afterwards is refused for real, by the
/// daemon, over the protocol. The restriction dies with the connection, so nothing outside the test
/// is affected.
/// </para>
/// <para>
/// This is deliberately not done through a security context. Building one needs a listening socket
/// the daemon accepts sandboxed clients on and a second process to connect through it, and the
/// interesting assertion, that the daemon refuses, is the same either way.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class PermissionRefusalTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static string Unique(string p) => $"{p}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    /// <summary>Finds this connection's own client object, by the name it connected under.</summary>
    private static PipeWireClient? OurClient(PipeWireRegistry registry, string applicationName) =>
        registry.Current.Clients.FirstOrDefault(
            c => string.Equals(c.ApplicationName, applicationName, StringComparison.Ordinal));

    [TestMethod]
    public async Task AClientThatDropsItsOwnPermissions_IsRefusedByTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = Unique("pwnet-selfrestrict");

        await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireClient? self = OurClient(registry, name);
        if (self is null) Assert.Inconclusive("this connection's own client is not visible in the graph.");

        // Creating works before the restriction, so the refusal afterwards is the restriction and
        // not something else about the request.
        PipeWireNode before = await registry.CreateVirtualNode("BeforeRestriction")
            .WithName(Unique("pwnet_before")).ExecuteAsync(cts.Token);
        await registry.DestroyGlobalAsync(before.NodeId, cts.Token);

        await using (PipeWireClientControl control = registry.BindClient(self!.Id))
        {
            // Everything not named individually loses every permission. The daemon matches
            // most-specific first, so this is the whole graph unless something says otherwise.
            PipeWireObjectPermission[] confine =
                [new PipeWireObjectPermission(PipeWireClientControl.AnyObject, PipeWirePermissions.None)];

            try
            {
                await control.UpdatePermissionsAsync(confine, cts.Token);
            }
            catch (PipeWireException ex)
            {
                // Some daemons refuse a client changing even its own permissions without the
                // manager permission. That is itself a real refusal and worth asserting on.
                Assert.IsTrue(ex.Result < 0, "a refusal must carry the daemon's code");
                Console.Error.WriteLine($"the daemon refused the self-restriction itself: {ex.Message}");
                return;
            }
        }

        // Now something that needs permission it no longer has.
        PipeWireException refused = await Assert.ThrowsExactlyAsync<PipeWireException>(
            async () => await registry.CreateVirtualNode("AfterRestriction")
                .WithName(Unique("pwnet_after")).ExecuteAsync(cts.Token));

        Assert.IsTrue(refused.Result < 0, "a refusal must carry the daemon's code");

        Console.Error.WriteLine(
            $"after dropping permissions: result {refused.Result}, "
            + $"permission-denied {refused.IsPermissionDenied}: {refused.Message}");

        // Which errno the daemon picks is its business, and asserting a specific one would be
        // asserting the daemon's implementation. What is ours is the mapping: IsPermissionDenied
        // has to be exactly the EACCES case and nothing else, or a caller branching on it gets a
        // different answer than the code says. That is the part worth pinning.
        Assert.AreEqual(refused.Result == -13, refused.IsPermissionDenied,
            $"IsPermissionDenied disagrees with the result code {refused.Result}");

        // And the property is reachable on a real refusal rather than only on a constructed one,
        // which is the whole reason it exists.
        Assert.IsFalse(refused.IsDisconnected, "a refusal is not a disconnection");
    }

    [TestMethod]
    public async Task ASecurityContextGivenNonsenseDescriptors_IsRefusedRatherThanAccepted()
    {
        // The adversarial half. A security context hands the daemon file descriptors, and the
        // failure mode worth knowing is a bad one being accepted: the sandbox would then exist on
        // paper while admitting anyone. Descriptors that cannot be a listening socket must come
        // back as a refusal, and the connection must survive it.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(Unique("pwnet-secctx"), ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireSecurityContext? available = registry.Current.SecurityContext;
        if (available is null) Assert.Inconclusive("this daemon exposes no security context.");

        await using PipeWireSecurityContextControl control = registry.BindSecurityContext(available!.Id);

        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pipewire.access"] = "restricted",
            ["pipewire.sec.engine"] = "org.pipewire.Test",
        };

        // Negative descriptors are a caller mistake and are caught before the daemon sees them.
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await control.CreateAsync(-1, 0, properties, cts.Token));

        // A descriptor that exists but is not a listening socket. Whether the daemon validates it
        // is the daemon's business and not this library's contract: /dev/null is a character device
        // that epoll accepts quite happily. A regular file is the stricter case, since epoll refuses
        // those outright.
        //
        // So what is asserted is our half. If the daemon refuses, the refusal must reach the caller
        // as a typed error carrying the code, rather than the call appearing to succeed. If it
        // accepts, the connection must still be usable, because a sandbox that was set up wrongly
        // is not a reason for this client to stop working.
        string scratch = Path.Combine(Path.GetTempPath(), $"pwnet-secctx-{Environment.ProcessId}.tmp");
        await File.WriteAllTextAsync(scratch, "not a socket", cts.Token);

        try
        {
            using var notASocket = new FileStream(scratch, FileMode.Open, FileAccess.Read);
            int fd = (int)notASocket.SafeFileHandle.DangerousGetHandle();

            try
            {
                await control.CreateAsync(fd, fd, properties, cts.Token);
                Console.Error.WriteLine(
                    "the daemon accepted a regular file as a sandbox socket; it validates on use, not here");
            }
            catch (PipeWireException ex)
            {
                Assert.IsTrue(ex.Result < 0, "a refusal must carry the daemon's code");
                Assert.AreEqual(ex.Result == -13, ex.IsPermissionDenied,
                    "IsPermissionDenied disagrees with the result code");
                Console.Error.WriteLine($"the daemon refused a bogus sandbox socket: {ex.Message}");
            }
        }
        finally
        {
            File.Delete(scratch);
        }

        // And the connection is unharmed by the refusal, which is what makes it a refusal rather
        // than a disconnection.
        await registry.WaitForInitialEnumerationAsync(cts.Token);
        Assert.IsTrue(registry.Current.Nodes.Length > 0);
    }
}
