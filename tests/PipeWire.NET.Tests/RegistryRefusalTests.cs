using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// What the registry does with an id that is not in the graph, and after it has been disposed.
/// </summary>
/// <remarks>
/// <para>
/// Every id a caller has came from a snapshot, and a graph moves: the node read a moment ago can be
/// gone by the time it is bound. So "this id is not in the graph" is an ordinary runtime condition
/// rather than a programming error, and each entry point has to answer it the same documented way
/// instead of reaching into the daemon with an id it cannot resolve.
/// </para>
/// <para>
/// Binding an unknown id without this check is the interesting failure: the daemon would answer a
/// bind for an id it does not know by tearing down the connection, so the local refusal is what
/// keeps a stale id from costing the whole session.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class RegistryRefusalTests : PipeWireTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>An id that is nothing at all is refused by every entry point that takes one.</summary>
    /// <remarks>
    /// The id used is one the daemon will never hand out, so the refusal cannot be a coincidence of
    /// what happens to be in this graph. Each of these is a separate lookup against a different part
    /// of the snapshot, which is why they are asserted one by one rather than through a loop.
    /// </remarks>
    [TestMethod]
    public async Task EveryLookupByGlobalId_RefusesAnIdTheGraphDoesNotHave()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-registry-refusal",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        const uint Nothing = 0x7FFF_FFFF;

        Assert.ThrowsExactly<ArgumentException>(() => reg.BindNode(Nothing));
        Assert.ThrowsExactly<ArgumentException>(() => reg.BindDevice(Nothing));
        Assert.ThrowsExactly<ArgumentException>(() => reg.BindClient(Nothing));
        Assert.ThrowsExactly<ArgumentException>(() => reg.BindPort(Nothing));
        Assert.ThrowsExactly<ArgumentException>(() => reg.BindLink(Nothing));
        Assert.ThrowsExactly<ArgumentException>(() => reg.BindMetadata(Nothing));
        Assert.ThrowsExactly<ArgumentException>(() => reg.BindProfiler(Nothing));
        Assert.ThrowsExactly<ArgumentException>(() => reg.BindSecurityContext(Nothing));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await reg.ReadModuleDetailsAsync(Nothing, cts.Token)
        );

        // A link needs two ports, and neither being real is refused before anything is sent.
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await reg.CreateLinkAsync(Nothing, Nothing, cts.Token)
        );

        // By name rather than by id: absent is null, because asking for a store that is simply not
        // running is a question with an answer, not a mistake.
        Assert.IsNull(
            reg.BindMetadata("pwnet-no-such-store"),
            "a metadata store that does not exist was reported as bound"
        );

        Assert.ThrowsExactly<ArgumentException>(() => reg.BindMetadata(string.Empty));
        Assert.ThrowsExactly<ArgumentNullException>(() => reg.BindMetadata((string)null!));
    }

    /// <summary>A disposed registry refuses every call rather than reading a torn-down graph.</summary>
    /// <remarks>
    /// The snapshot outlives the registry - it is an immutable value a caller may still hold - so
    /// the object being gone has to be reported by the registry itself. Its watch stream has to end
    /// too: a consumer that passed no cancellation token would otherwise wait on it for ever.
    /// </remarks>
    [TestMethod]
    public async Task ADisposedRegistry_RefusesEveryCallAndEndsItsWatch()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-registry-disposed",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        await reg.DisposeAsync();
        await reg.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => reg.BindNode(1));
        Assert.ThrowsExactly<ObjectDisposedException>(() => reg.BindMetadata("settings"));

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await reg.WaitForInitialEnumerationAsync(cts.Token)
        );

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await reg.CreateVirtualSinkAsync("pwnet gone", cancellationToken: cts.Token)
        );

        var seen = 0;
        await foreach (PipeWireGraphSnapshot _ in reg.WatchAsync(cts.Token))
        {
            seen++;
            if (seen > 2)
                break;
        }

        Assert.AreEqual(
            0,
            seen,
            "a disposed registry kept yielding snapshots instead of ending its stream"
        );
    }
}
