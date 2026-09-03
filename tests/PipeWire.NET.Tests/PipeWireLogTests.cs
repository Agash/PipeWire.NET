using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>Control over the native library's own logging.</summary>
/// <remarks>
/// What this can check is that the call reaches the library and is accepted for every level, and
/// that a value outside the enum is refused before it gets there. What the daemon then writes to
/// stderr is not something a test can assert on without capturing another process's output.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class PipeWireLogTests
{
    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    [TestMethod]
    public async Task EveryLevel_IsAcceptedAndLeavesTheLibraryUsable()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Settable before anything exists, which is the point: a caller turning logging up to
        // diagnose a startup failure has nowhere else to put the call.
        PipeWireLog.SetLevel(PipeWireLogLevel.Warn);

        await using var ctx = new PipeWireContext("pwnet-loglevel", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        foreach (PipeWireLogLevel level in Enum.GetValues<PipeWireLogLevel>())
            PipeWireLog.SetLevel(level);

        // Back to what the process started with. The level is global to the library, so leaving it
        // wherever this test finished changes every test that runs afterwards.
        //
        // Warn is the library's own default and what nothing having called SetLevel means.
        PipeWireLog.SetLevel(PipeWireLogLevel.Warn);

        // The connection is unharmed by any of it.
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);
        Assert.IsTrue(registry.Current.Nodes.Length > 0);
    }

    [TestMethod]
    public void ALevelOutsideTheEnum_IsRefusedRatherThanPassedToTheLibrary()
    {
        RequireLinux();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PipeWireLog.SetLevel((PipeWireLogLevel)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PipeWireLog.SetLevel((PipeWireLogLevel)(-1)));
    }
}
