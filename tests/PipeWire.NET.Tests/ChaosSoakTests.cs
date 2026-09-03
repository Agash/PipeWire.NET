using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Several actors changing the same graph at once, with everything counted before and after.
/// </summary>
/// <remarks>
/// The rest of the suite drives one thing at a time and asserts what it did. Real sessions do not
/// look like that: a session manager is moving defaults while a patchbay relinks and a mixer writes
/// volumes, and the failures that only appear there are the ones nobody writes a targeted test for.
/// <para>
/// The accounting is the point as much as the survival. A leak on our side shows up as descriptors
/// or threads that never come back; a leak on the daemon's side shows up as objects that outlive
/// the client that made them, and no amount of counting inside this process can see that one. Both
/// are measured against a settled baseline, and the daemon's count comes from pw-dump rather than
/// from our own registry, so a projection that quietly forgets an object cannot hide the evidence.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ChaosSoakTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<(PipeWireContext Context, PipeWireRegistry Registry)> ConnectAsync(
        string name, CancellationToken cancellationToken)
    {
        var context = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cancellationToken);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cancellationToken);
        return (context, registry);
    }

    private static string Unique(string p) => $"{p}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    /// <summary>Descriptors that could represent something this library failed to release.</summary>
    /// <remarks>
    /// <para>
    /// Not every descriptor. The runtime opens assemblies lazily, so a long test that reaches more
    /// of the BCL than its baseline did ends with a dozen more files open and never closes them:
    /// counting those measures JIT warm-up and calls it a leak.
    /// </para>
    /// <para>
    /// What a context actually holds is a unix socket to the daemon and an eventfd for the loop's
    /// wakeup, so those are what is counted. Pipes, epolls and non-unix sockets are deliberately
    /// not: the thread pool and any child process another class in the suite started open them
    /// between the two censuses, and there is nothing in the count to say whose they are. The
    /// breakdown is still printed on failure, so a genuinely leaked child pipe is visible without
    /// being asserted on.
    /// </para>
    /// </remarks>
    private static int LeakableDescriptors(Dictionary<string, int> targets) =>
        targets
            .Where(kv => kv.Key is "unix-socket" or "eventfd")
            .Sum(kv => kv.Value);

    /// <summary>What each open descriptor points at, counted by target.</summary>
    /// <remarks>
    /// A number alone says a descriptor leaked and nothing about which, and the answer decides
    /// whether it is the library or the harness: a socket to the daemon is ours, a pipe is a child
    /// process nobody waited on. Reading the link can fail for a descriptor that closes underneath
    /// the walk, which is normal and not worth reporting.
    /// </remarks>
    private static Dictionary<string, int> DescriptorTargets()
    {
        var targets = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string entry in Directory.GetFiles("/proc/self/fd"))
        {
            string target;
            try { target = File.ResolveLinkTarget(entry, returnFinalTarget: false)?.Name ?? "(unresolved)"; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            // A PipeWire connection is a unix socket, so those are separated from the rest. The
            // runtime opens sockets of its own for name resolution and the like, and one of those
            // arriving partway through a soak read as a leaked connection to the daemon.
            if (target.StartsWith("socket:", StringComparison.Ordinal))
            {
                target = IsUnixSocket(target) ? "unix-socket" : "other-socket";
            }
            else
            {
                // Anonymous inodes carry a number that differs every time; the kind is the useful
                // part.
                int colon = target.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0) target = target[..colon];
            }

            targets[target] = targets.GetValueOrDefault(target) + 1;
        }

        return targets;
    }

    /// <summary>True when a socket descriptor is a unix socket, which is what the daemon speaks.</summary>
    /// <remarks>
    /// Read fresh each time rather than cached: the table changes as connections come and go, and a
    /// stale copy would classify a new socket by an old snapshot.
    /// </remarks>
    private static bool IsUnixSocket(string target)
    {
        string inode = target["socket:[".Length..].TrimEnd(']');

        try
        {
            foreach (string line in File.ReadLines("/proc/net/unix"))
            {
                foreach (string field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (field == inode) return true;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable here means nothing can be classified, and treating everything as ours
            // would report a leak on every run. Better to count none than to count wrongly.
        }

        return false;
    }

    /// <summary>The exact socket inodes open now, so a leak can be named rather than counted.</summary>
    /// <remarks>
    /// A count says one socket did not come back. Which one decides whether it is a connection to
    /// the daemon, something the runtime opened for itself, or a child process's end that this
    /// process happens to hold. /proc/net/unix maps the inode to a path when there is one.
    /// </remarks>
    private static HashSet<string> SocketInodes()
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);

        foreach (string entry in Directory.GetFiles("/proc/self/fd"))
        {
            string target;
            try { target = File.ResolveLinkTarget(entry, returnFinalTarget: false)?.Name ?? ""; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (target.StartsWith("socket:", StringComparison.Ordinal)) inodes.Add(target);
        }

        return inodes;
    }

    private static string DescribeSockets(HashSet<string> before, HashSet<string> after)
    {
        string[] added = [.. after.Except(before)];
        if (added.Length == 0) return "no new sockets";

        var described = new List<string>();
        string[] unix = File.Exists("/proc/net/unix")
            ? File.ReadAllLines("/proc/net/unix")
            : [];

        foreach (string socket in added)
        {
            string inode = socket["socket:[".Length..].TrimEnd(']');
            string? line = Array.Find(unix, l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                  .Any(f => f == inode));

            described.Add(line is null ? $"{socket} (not a unix socket)" : $"{socket} -> {line.Trim()}");
        }

        return string.Join("; ", described);
    }

    private static string DescribeGrowth(Dictionary<string, int> before, Dictionary<string, int> after)
    {
        IEnumerable<string> grown = after
            .Where(kv => kv.Value > before.GetValueOrDefault(kv.Key))
            .OrderByDescending(kv => kv.Value - before.GetValueOrDefault(kv.Key))
            .Select(kv => $"{kv.Key} +{kv.Value - before.GetValueOrDefault(kv.Key)}");

        return string.Join(", ", grown);
    }

    /// <summary>Threads belonging to a PipeWire loop, by name.</summary>
    /// <remarks>
    /// Not the process total. Four concurrent actors make the thread pool grow, and it does not
    /// give those threads back promptly, so counting every thread measures the pool's high-water
    /// mark and calls it a stranded loop. PipeWire names its own threads, so the ones that matter
    /// can be counted directly: a loop that was never stopped is a thread that is still named.
    /// </remarks>
    private static int LoopThreads()
    {
        var count = 0;

        foreach (string task in Directory.GetDirectories("/proc/self/task"))
        {
            string name;
            try { name = File.ReadAllText(Path.Combine(task, "comm")).Trim(); }
            catch (IOException) { continue; }          // exited between the listing and the read
            catch (UnauthorizedAccessException) { continue; }

            if (name.StartsWith("pw-", StringComparison.Ordinal)
                || name.Contains("pipewire", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("pwnet-", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private readonly record struct Census(
        int Fds, int Threads, long Heap, int Nodes, int Links, int Clients, Dictionary<string, int> FdTargets)
    {
        public override string ToString() =>
            $"leakable-fds={Fds} loop-threads={Threads} heap={Heap / 1024}KB nodes={Nodes} links={Links} clients={Clients}";
    }

    /// <summary>The name prefix every object this soak creates carries.</summary>
    /// <remarks>
    /// The daemon's side of the census counts only objects with this prefix, which is the whole
    /// difference between an assertion about this test and an assertion about the machine. The
    /// suite shares one session and other classes create and destroy nodes throughout, so a
    /// graph-wide count reads their work as this test's leak.
    /// </remarks>
    private const string SoakPrefix = "pwnet_soak";

    /// <summary>The client-name prefix the soak's own connections carry.</summary>
    private const string SoakClientPrefix = "pwnet-soak";

    private static bool IsOurs(PwDump.Entry entry, string key, string prefix) =>
        entry.Prop(key) is { } name && name.StartsWith(prefix, StringComparison.Ordinal);

    private static async Task<Census> TakeCensusAsync(CancellationToken ct)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        PwDump dump = await PwDump.CaptureAsync(ct);

        // One walk, used for both the count and the diagnostic. Two walks disagree, and then the
        // number says one thing while the breakdown under it says another.
        Dictionary<string, int> targets = DescriptorTargets();

        // A link carries no name of its own, so it is counted by the node at either end of it
        // belonging to this test.
        var ourNodes = dump.OfKind("Node")
            .Where(e => IsOurs(e, "node.name", SoakPrefix))
            .Select(e => e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        return new Census(
            LeakableDescriptors(targets),
            LoopThreads(),
            GC.GetTotalMemory(forceFullCollection: true),
            ourNodes.Count,
            dump.OfKind("Link").Count(e =>
                (e.Prop("link.output.node") is { } o && ourNodes.Contains(o))
                || (e.Prop("link.input.node") is { } i && ourNodes.Contains(i))),
            dump.OfKind("Client").Count(e => IsOurs(e, "application.name", SoakClientPrefix)),
            targets);
    }

    /// <summary>Takes a census once this process's own descriptor count has stopped moving.</summary>
    /// <remarks>
    /// Closing a socket is not instant, and the daemon reaps its side before we release ours. A
    /// census taken straight after a disconnect therefore reads one lower than the steady state,
    /// and comparing that against a settled figure later reports a leak of exactly the sockets that
    /// had not finished closing when the baseline was taken. Both ends of the comparison have to be
    /// settled or neither is meaningful.
    /// </remarks>
    private static async Task<Census> SettledCensusAsync(CancellationToken ct)
    {
        Census census = await TakeCensusAsync(ct);

        for (int attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

            Census next = await TakeCensusAsync(ct);
            if (next.Fds == census.Fds && next.Threads == census.Threads) return next;

            census = next;
        }

        return census;
    }

    [TestMethod]
    public async Task SeveralActorsChangingOneGraph_LeaveNothingBehindOnEitherSide()
    {
        RequireLinux();
        PwTools.Require();

        using var cts = new CancellationTokenSource(Budget);

        // A settled baseline: connect and disconnect once first, so the daemon has already created
        // and reaped whatever a client of ours costs it. Counting from a cold session would charge
        // the soak for the first connection's permanent structures.
        {
            (PipeWireContext warmCtx, PipeWireRegistry warmReg) = await ConnectAsync("pwnet-soak-warm", cts.Token);
            await using (warmCtx)
            await using (warmReg)
            {
                PipeWireNode warm = await warmReg.CreateVirtualNode("Warm")
                    .WithName(Unique("pwnet_soak_warm")).ExecuteAsync(cts.Token);
                await warmReg.DestroyGlobalAsync(warm.NodeId, cts.Token);
            }
        }

        await SettleAsync(cts.Token);
        Census before = await SettledCensusAsync(cts.Token);
        HashSet<string> socketsBefore = SocketInodes();

        var faults = new ConcurrentQueue<string>();
        var created = new ConcurrentBag<uint>();

        (PipeWireContext ctxA, PipeWireRegistry a) = await ConnectAsync("pwnet-soak-a", cts.Token);
        (PipeWireContext ctxB, PipeWireRegistry b) = await ConnectAsync("pwnet-soak-b", cts.Token);

        await using (ctxA)
        await using (a)
        await using (ctxB)
        await using (b)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            stop.CancelAfter(TimeSpan.FromSeconds(45));

            Task maker = Task.Run(() => MakeAndBreakAsync(a, created, faults, stop.Token), CancellationToken.None);
            Task inspector = Task.Run(() => InspectAsync(b, faults, stop.Token), CancellationToken.None);
            Task linker = Task.Run(() => LinkAndUnlinkAsync(a, faults, stop.Token), CancellationToken.None);
            Task external = Task.Run(() => ExternalToolsAsync(b, faults, stop.Token), CancellationToken.None);

            await Task.WhenAll(maker, inspector, linker, external);

            Assert.IsTrue(faults.IsEmpty,
                $"{faults.Count} actor faults, first: {(faults.TryPeek(out string? f) ? f : string.Empty)}");

            // Everything this test made, gone, so the census compares like with like.
            foreach (uint id in created)
            {
                try { await a.DestroyGlobalAsync(id, cts.Token); }
                catch (PipeWireException) { /* already gone, which is the point of the soak */ }
            }

            await a.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(a.Current.Nodes.Length > 0, "the graph is empty, so the session did not survive");
            await b.WaitForInitialEnumerationAsync(cts.Token);
        }

        await SettleAsync(cts.Token);

        // Closing a socket is not instant from this side either: the contexts have gone, but the
        // descriptors they held can still be on their way out while the daemon already reports the
        // client gone. Waiting for the count to come back down distinguishes that from a leak,
        // where it never does, rather than reading whichever moment the census happened to land on.
        Census after = await SettledCensusAsync(cts.Token);

        Console.Error.WriteLine($"soak before: {before}");
        Console.Error.WriteLine($"soak after:  {after}");

        // Our side. Descriptors and threads must come back; the managed heap is allowed to grow,
        // because the allocator keeps what it has taken and a fixed ceiling would be a flake.
        Assert.IsTrue(after.Fds <= before.Fds,
            $"sockets, pipes and event descriptors grew {before.Fds} -> {after.Fds} and stayed up: "
            + DescribeGrowth(before.FdTargets, after.FdTargets)
            + ". New sockets: " + DescribeSockets(socketsBefore, SocketInodes()));
        Assert.IsTrue(after.Threads <= before.Threads,
            $"PipeWire loop threads grew {before.Threads} -> {after.Threads}, so a loop was left running");

        // The daemon's side, which nothing inside this process can see. Both contexts are gone, so
        // every object and client we accounted for should have gone with them. Counted by name, so
        // this is about what the soak left behind rather than about what the rest of the suite
        // happened to be doing at the same moment.
        Assert.IsTrue(after.Clients <= before.Clients,
            $"the daemon still holds {SoakClientPrefix} clients from this run: "
            + $"{before.Clients} -> {after.Clients}");
        Assert.IsTrue(after.Nodes <= before.Nodes,
            $"the daemon still holds {SoakPrefix} nodes from this run: {before.Nodes} -> {after.Nodes}");
        Assert.IsTrue(after.Links <= before.Links,
            $"the daemon still holds {SoakPrefix} links from this run: {before.Links} -> {after.Links}");
    }

    /// <summary>Waits until the daemon's own object counts stop moving.</summary>
    /// <remarks>
    /// A census taken while the session manager is still reaping reads as a leak. Rather than a
    /// fixed delay, which is both too long here and too short on a loaded runner, this waits for
    /// two consecutive dumps to agree.
    /// </remarks>
    private static async Task SettleAsync(CancellationToken ct)
    {
        (int Nodes, int Links, int Clients) previous = (-1, -1, -1);

        for (int attempt = 0; attempt < 40; attempt++)
        {
            PwDump dump = await PwDump.CaptureAsync(ct);
            (int, int, int) now = (dump.OfKind("Node").Count(), dump.OfKind("Link").Count(),
                                   dump.OfKind("Client").Count());

            if (now == previous) return;

            previous = now;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private static async Task MakeAndBreakAsync(
        PipeWireRegistry registry, ConcurrentBag<uint> created, ConcurrentQueue<string> faults, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PipeWireNode node = await registry.CreateVirtualNode("Soak")
                    .WithName(Unique("pwnet_soak")).ExecuteAsync(ct);
                created.Add(node.NodeId);

                await using (PipeWireNodeControl control = registry.BindNode(node.NodeId))
                {
                    await control.ReadyAsync(ct);
                    for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++)
                        await control.SetVolumeAsync(0.1f * (i + 1), ct);
                }

                // ENOENT is the object having gone already, and under a soak against a live
                // session manager that is legitimate: WirePlumber destroys nodes it cannot
                // activate, and a virtual node it has decided against is reaped before this gets
                // to it. Narrowed to that code deliberately - a refusal (EACCES) or a protocol
                // error is still a fault, and swallowing every PipeWireException here would hide
                // both. The id is already in `created`, so the cleanup accounting is unaffected.
                try { await registry.DestroyGlobalAsync(node.NodeId, ct); }
                catch (PipeWireException e) when (e.Result == -2) { }
            }
        }
        catch (OperationCanceledException) { /* the soak's own clock */ }
        catch (Exception ex) { faults.Enqueue($"maker: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task InspectAsync(
        PipeWireRegistry registry, ConcurrentQueue<string> faults, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PipeWireGraphSnapshot graph = registry.Current;

                foreach (PipeWireNode node in graph.Nodes.Take(6))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
                        await control.ReadyAsync(ct);
                        _ = await control.GetVolumeAsync(ct);
                    }
                    catch (ArgumentException) { /* it went away between the snapshot and the bind */ }
                    catch (PipeWireException) { /* or the daemon refused, which is its right */ }
                }

                await registry.WaitForInitialEnumerationAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { faults.Enqueue($"inspector: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task LinkAndUnlinkAsync(
        PipeWireRegistry registry, ConcurrentQueue<string> faults, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PipeWireGraphSnapshot graph = registry.Current;

                PipeWirePort? output = graph.Ports.FirstOrDefault(p => p.PortDirection == PipeWirePortDirection.Out);
                PipeWirePort? input = graph.Ports.FirstOrDefault(p => p.PortDirection == PipeWirePortDirection.In);

                if (output is null || input is null)
                {
                    await registry.WaitForInitialEnumerationAsync(ct);
                    continue;
                }

                try
                {
                    PipeWireLink link = await registry.CreateLinkAsync(output, input, ct);
                    await registry.DestroyGlobalAsync(link.LinkId, ct);
                }
                catch (ArgumentException) { /* the ports face the wrong way or one just left */ }
                catch (PipeWireException)
                {
                    // The daemon refused this pairing, which is the expected answer most of the
                    // time: the actor links arbitrary ports from the whole graph, so most pairs
                    // cannot negotiate a format at all. Refusing is the daemon working.
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { faults.Enqueue($"linker: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task ExternalToolsAsync(
        PipeWireRegistry registry, ConcurrentQueue<string> faults, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Processes that are not us, changing the same graph. This is what makes the soak
                // more than four threads sharing one library.
                await using (PwTools.Loopback loop = await PwTools.StartLoopbackAsync(Unique("pwnet_soak_lb"), ct))
                {
                    await registry.WaitForInitialEnumerationAsync(ct);
                }

                List<(uint Link, uint Output, uint Input)> links = await PwTools.ListLinksAsync(ct);
                Assert.IsNotNull(links);

                await registry.WaitForInitialEnumerationAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (AssertInconclusiveException) { /* a tool is missing; the other actors carry the soak */ }
        catch (Exception ex) { faults.Enqueue($"external: {ex.GetType().Name}: {ex.Message}"); }
    }
}
