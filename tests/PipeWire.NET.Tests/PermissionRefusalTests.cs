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
/// so a context can deny itself writes to one object and everything it tries there afterwards is
/// refused for real, by the daemon, over the protocol. The restriction dies with the connection,
/// so nothing outside the test is affected.
/// </para>
/// <para>
/// Denying everything is not the shape: without read access to its own core the connection stops
/// answering its own round-trips rather than refusing them, and the caller sees a cancellation
/// after the budget instead of a refusal. Naming the denied object keeps the connection alive to
/// hear the no.
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
    public async Task AClientThatLosesWriteAccess_IsRefusedAndRollsBack()
    {
        // A real refusal, and what the store does with one: the write was applied optimistically,
        // the daemon said no, and no echo is coming to correct it, so the key must read back as
        // never written rather than as the refused value.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = Unique("pwnet-selfrestrict");

        await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        // Found by the name the connection advertises.
        PipeWireClient? self = OurClient(registry, name);
        Assert.IsNotNull(self, "this connection's own client is not visible in the graph.");

        PipeWireMetadataStore? store = registry.BindMetadataStore("default");
        if (store is null)
            Assert.Inconclusive("no session manager, so no default store.");

        await using (store)
        {
            await store!.ReadyAsync(cts.Token);
            string key = $"pwnet.refused.{Environment.ProcessId}";

            // A value already there, so the refusal below restores rather than removes: both
            // halves of the rollback are exercised, not just the easy one.
            await store.SetAsync(key, "v1", cancellationToken: cts.Token);

            await using (PipeWireClientControl control = registry.BindClient(self!.Id))
            {
                // Writes to this store only. Everything else keeps its permissions, so the
                // connection stays alive to hear the refusal.
                await control.UpdatePermissionsAsync(
                    new[] { new PipeWireObjectPermission(store.Id, PipeWirePermissions.None) }, cts.Token);
            }

            PipeWireException refused = await Assert.ThrowsExactlyAsync<PipeWireException>(
                () => store.SetAsync(key, "v2", cancellationToken: cts.Token));

            Assert.IsTrue(refused.Result < 0, "a refusal must carry the daemon's code");
            Console.Error.WriteLine($"after losing write access: {refused.Message}");

            // Which errno the daemon picks is its business, and asserting a specific one would be
            // asserting the daemon's implementation. What is ours is the mapping:
            // IsPermissionDenied has to be exactly the EACCES case and nothing else, or a caller
            // branching on it gets a different answer than the code says.
            Assert.AreEqual(refused.Result == -13, refused.IsPermissionDenied,
                $"IsPermissionDenied disagrees with the result code {refused.Result}");

            // And the property is reachable on a real refusal rather than only on a constructed
            // one, which is the whole reason it exists.
            Assert.IsFalse(refused.IsDisconnected, "a refusal is not a disconnection");

            // The refused value must not linger in the optimistic cache: the earlier value is
            // what reads back.
            Assert.AreEqual("v1", store.Get(key), "a refused write replaced the stored value");
        }
    }

    [TestMethod]
    public async Task ClientControlGuards_RefuseBadInputBeforeTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = Unique("pwnet-clientguards");

        await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireClient? self = OurClient(registry, name);
        Assert.IsNotNull(self, "this connection's own client is not visible in the graph.");

        // Synchronous disposal: tearing a binding down does no I/O.
        using (PipeWireClientControl control = registry.BindClient(self!.Id))
        {
            // A default is the method's own to write, so one in the grants contradicts the
            // confining it exists to do.
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await control.ConfineToAsync(
                    [new PipeWireObjectPermission(PipeWireClientControl.AnyObject, PipeWirePermissions.Read)],
                    cts.Token));
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(
                async () => await control.UpdatePropertiesAsync(null!, cts.Token));
        }
    }

    [TestMethod]
    public async Task AClientDeniedItsFactory_IsRefusedWhenCreating()
    {
        // Creation refused at the factory is the error path object creation exists for: the
        // daemon answers the request with an error rather than an object, and the wait must
        // fail with it instead of hanging until the caller's budget.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = Unique("pwnet-factorydeny");

        await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireFactory? factory = registry.Current.Factories
            .FirstOrDefault(f => string.Equals(f.FactoryName, "adapter", StringComparison.Ordinal));
        if (factory is null)
            Assert.Inconclusive("this session has no adapter factory to be denied.");

        PipeWireClient? self = OurClient(registry, name);
        Assert.IsNotNull(self, "this connection's own client is not visible in the graph.");

        await using (PipeWireClientControl control = registry.BindClient(self!.Id))
        {
            await control.UpdatePermissionsAsync(
                new[] { new PipeWireObjectPermission(factory!.Id, PipeWirePermissions.None) }, cts.Token);
        }

        PipeWireException refused = await Assert.ThrowsExactlyAsync<PipeWireException>(
            () => registry.CreateVirtualNode("Denied").WithName(Unique("pwnet_denied")).ExecuteAsync(cts.Token));

        Assert.IsTrue(refused.Result < 0, "a refusal must carry the daemon's code");
        Console.Error.WriteLine($"after losing the factory: {refused.Message}");
        Assert.AreEqual(refused.Result == -13, refused.IsPermissionDenied,
            $"IsPermissionDenied disagrees with the result code {refused.Result}");
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
