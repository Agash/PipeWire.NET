using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// <c>internal</c>: a PipeWire graph that lives entirely inside this process, with no daemon.
/// </summary>
/// <remarks>
/// <para>
/// <c>pw_context_connect_self</c> makes the context its own core. Nothing is shared with the
/// session, no socket is opened, and there is no daemon that has to be running. An in-process graph
/// starts with nothing in it - not even a factory to make nodes with - so the modules that provide
/// those have to be loaded first, which is why <see cref="PipeWireContext.LoadModule"/> exists.
/// </para>
/// <para>
/// Worth having for its own sake, beyond the example: a graph with no external dependency is one a
/// test can rely on. Most of this suite needs a live daemon and skips without one.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[SupportedOSPlatform("linux")]
public sealed class InProcessGraphTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux library.");
    }

    /// <summary>
    /// A self-connected context comes up and has a graph of its own, without any daemon.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The proof that no daemon is involved is the graph's own contents, not an environment trick.
    /// An earlier version of this test pointed <c>PIPEWIRE_REMOTE</c> at a socket that does not
    /// exist - which worked, and also broke every other test class, because MSTest runs classes in
    /// parallel and the variable is process-wide. Every concurrent test then tried to connect to a
    /// daemon that was not there.
    /// </para>
    /// <para>
    /// The factories are what shows the modules loaded: an in-process graph has none until they do,
    /// so seeing them means this context built its own graph. The session's devices being absent is
    /// what shows it is not the desktop's daemon answering.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task ASelfConnectedContext_HasItsOwnGraphWithNoDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-inprocess",
            ConsoleTestLoggerFactory.Instance
        )
        {
            RunInProcess = true,
        };

        // Without these the graph has no way to create anything, which is the difference between
        // connecting to a daemon and being one.
        ctx.LoadModule("libpipewire-module-spa-node-factory");
        ctx.LoadModule("libpipewire-module-link-factory");

        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireGraphSnapshot graph = reg.Current;

        Assert.IsNotNull(graph.Core, "the in-process graph has no core object");

        string[] factories =
        [
            .. graph.Factories.Select(f => f.FactoryName).Where(n => n is not null).Select(n => n!),
        ];

        Assert.IsTrue(
            factories.Contains("spa-node-factory", StringComparer.Ordinal),
            $"the spa-node-factory module did not load; factories present: "
                + $"{string.Join(", ", factories)}"
        );

        Assert.IsTrue(
            factories.Contains("link-factory", StringComparer.Ordinal),
            $"the link-factory module did not load; factories present: "
                + $"{string.Join(", ", factories)}"
        );

        Assert.IsFalse(
            graph.Nodes.Any(n =>
                n.NodeName?.StartsWith("alsa_", StringComparison.Ordinal) ?? false
            ),
            "the graph contains the session's ALSA nodes, so this connected to a daemon rather "
                + "than running its own graph"
        );
    }

    /// <summary>
    /// Modules have to be loaded before the context starts, and saying so beats failing later.
    /// </summary>
    /// <remarks>
    /// The self-connection creates the core out of whatever the context holds, so a module added
    /// afterwards would load into a context whose core already exists and silently do nothing for
    /// the graph in use.
    /// </remarks>
    [TestMethod]
    public async Task LoadingAModuleAfterStarting_IsRefused()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-inprocess-late",
            ConsoleTestLoggerFactory.Instance
        )
        {
            RunInProcess = true,
        };

        ctx.LoadModule("libpipewire-module-spa-node-factory");
        await ctx.StartAsync(cts.Token);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ctx.LoadModule("libpipewire-module-link-factory")
        );
    }

    /// <summary>A module that does not exist fails at start, naming what could not be loaded.</summary>
    [TestMethod]
    public async Task AMissingModule_FailsTheStartRatherThanBeingIgnored()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-inprocess-missing",
            ConsoleTestLoggerFactory.Instance
        )
        {
            RunInProcess = true,
        };

        ctx.LoadModule("libpipewire-module-pwnet-does-not-exist");

        PipeWireException ex = await Assert.ThrowsExactlyAsync<PipeWireInteropException>(() =>
            ctx.StartAsync(cts.Token)
        );

        Assert.IsTrue(
            ex.Message.Contains("pwnet-does-not-exist", StringComparison.Ordinal),
            $"the failure did not name the module that could not be loaded: {ex.Message}"
        );
    }
}
