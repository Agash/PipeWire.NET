using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Driving the loop from the application's own thread, the way upstream's <c>gmain</c> example
/// drives it from glib's main loop.
/// </summary>
/// <remarks>
/// <para>
/// The point is where callbacks run. By default this library owns a thread and events arrive on it,
/// so anything touching UI state has to marshal. A host that drives the loop itself gets the events
/// on its own thread instead - for a GTK application that is <c>g_unix_fd_add</c> over
/// <see cref="PipeWireContext.LoopDescriptor"/>, and the view model updates without a hop.
/// </para>
/// <para>
/// What makes it work rather than deadlock is that PipeWire's loop mutex is recursive: the driving
/// thread is also the callback thread, and every method in this library takes that lock, so a
/// callback re-entering the API would otherwise block on itself.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed partial class HostDrivenLoopTests
{
    private const short PollIn = 0x001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(ref PollFd fds, nuint nfds, int timeout);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>
    /// A host that polls the loop descriptor and iterates gets the graph, on its own thread.
    /// </summary>
    /// <remarks>
    /// Everything here happens on the calling thread: no PipeWire thread is started at all. The
    /// registry filling up is the proof - those are daemon events, dispatched only because this
    /// thread pumped the loop, and the thread id check is what distinguishes that from a background
    /// thread having quietly done the work.
    /// </remarks>
    [TestMethod]
    public async Task AHostThatPumpsTheLoop_ReceivesEventsOnItsOwnThread()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var ctx = new PipeWireContext(
            "pwnet-hostloop",
            ConsoleTestLoggerFactory.Instance
        )
        {
            DriveExternally = true,
        };

        await ctx.StartAsync(cts.Token);

        int fd = ctx.LoopDescriptor;
        Assert.IsTrue(fd >= 0, "the context exposed no loop descriptor to poll");

        ctx.EnterLoop();
        try
        {
            await using var reg = new PipeWireRegistry(ctx);

            int drivingThread = Environment.CurrentManagedThreadId;
            var callbackThreads = new HashSet<int>();
            reg.GraphChanged += (_, _) => callbackThreads.Add(Environment.CurrentManagedThreadId);

            // The host's loop: wait on the descriptor, dispatch what is ready. Nothing else is
            // running, so if this stops pumping, nothing arrives.
            var iterations = 0;
            PipeWireGraphSnapshot graph = reg.Current;

            for (var i = 0; i < 600 && !graph.Nodes.Any(); i++)
            {
                var pfd = new PollFd { Fd = fd, Events = PollIn };
                if (Poll(ref pfd, 1, 50) > 0 && (pfd.Revents & PollIn) != 0)
                {
                    Assert.IsTrue(ctx.IterateLoop(0) >= 0, "iterating the loop failed");

                    iterations++;
                }

                graph = reg.Current;
                cts.Token.ThrowIfCancellationRequested();
            }

            Assert.IsTrue(
                iterations > 0,
                "the loop descriptor never signalled, so nothing was pumped"
            );

            Assert.IsTrue(
                graph.Nodes.Any(),
                "the graph never filled, so the host's pumping delivered no daemon events"
            );

            // The whole point: the events arrived on the thread that pumped, not a PipeWire one.
            Assert.IsTrue(callbackThreads.Count > 0, "no graph change was observed at all");

            CollectionAssert.AreEquivalent(
                new[] { drivingThread },
                callbackThreads.ToArray(),
                "events were dispatched on a thread other than the one driving the loop, so a UI "
                    + "host would still have to marshal"
            );
        }
        finally
        {
            ctx.LeaveLoop();
        }
    }

    /// <summary>
    /// The host-driven surface refuses to be used on a context that runs its own thread.
    /// </summary>
    /// <remarks>
    /// Two iterators on one loop race over the poll set. Refusing is the only safe answer, and it
    /// has to be an exception rather than a silent no-op: a host that thought it was driving would
    /// otherwise poll a descriptor nothing ever services.
    /// </remarks>
    [TestMethod]
    public async Task TheHostDrivenCalls_RefuseAContextThatOwnsItsThread()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var ctx = new PipeWireContext(
            "pwnet-hostloop-refuse",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        Assert.ThrowsExactly<InvalidOperationException>(() => ctx.IterateLoop());
        Assert.ThrowsExactly<InvalidOperationException>(ctx.EnterLoop);
        Assert.ThrowsExactly<InvalidOperationException>(ctx.LeaveLoop);

        // The descriptor is still readable, because polling it is harmless and a caller may want to
        // know the loop is alive; it is driving it that is refused.
        Assert.IsTrue(ctx.LoopDescriptor >= 0);
    }
}
