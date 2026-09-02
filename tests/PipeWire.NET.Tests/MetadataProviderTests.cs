using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Serving a metadata store, rather than consuming one.
/// </summary>
/// <remarks>
/// The first attempt at this asked the daemon to create the store through the metadata factory,
/// which returns an object the creating client is expected to serve. Nothing served it, so the
/// daemon blocked and stopped answering every client on the machine. These check the store works
/// and that the session survives it.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class MetadataProviderTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static string Unique() => $"pwnet-own-{Environment.ProcessId}-{Random.Shared.Next():x}";

    [TestMethod]
    public async Task AStoreWeServe_LeavesTheSessionResponsive()
    {
        RequireLinux();
        CliTool cli = CliTool.Require("pw-cli");
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        using (PipeWireMetadataProvider provider = PipeWireMetadataProvider.Create(ctx, Unique()))
        {
            provider.Set("k", "v");

            // The failure this exists for: the daemon stops answering every client, not just us.
            (int exit, _, _) = await cli.RunAsync(["info", "0"], cts.Token, TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, exit, "the daemon stopped answering while we served a store");

            await registry.WaitForInitialEnumerationAsync(cts.Token);
        }

        (int after, _, _) = await cli.RunAsync(["info", "0"], cts.Token, TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, after, "the daemon stopped answering after the store went away");
    }

    [TestMethod]
    public async Task AStoreWeServe_ReportsEveryChangeItAccepts()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider-events", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        using PipeWireMetadataProvider provider = PipeWireMetadataProvider.Create(ctx, Unique());

        var seen = new List<PipeWireMetadataEntry>();
        provider.EntryChanged += (_, e) => { lock (seen) seen.Add(e); };

        provider.Set("a", "1");
        provider.Set("a", "2");
        provider.Set("a", null);

        lock (seen)
        {
            Assert.AreEqual(3, seen.Count, "each accepted change must be reported once");
            Assert.AreEqual("1", seen[0].Value);
            Assert.AreEqual("2", seen[1].Value);
            Assert.IsNull(seen[2].Value, "a removal is reported as a null value");
        }

        Assert.IsNull(provider.Get("a"));
        await Task.CompletedTask;
    }
}
