using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The creation builders, the clock's drift fields, and the screen-capture metadata.
/// </summary>
/// <remarks>
/// <para>
/// The audit put <c>PipeWireNodeBuilder</c> and <c>PipeWireLinkBuilder</c> at zero
/// strongly-asserted members. They are used constantly - almost every graph test builds objects
/// with them - but the assertions were always about the resulting graph, never about the builder.
/// A <c>With...</c> that dropped its argument would pass every one of them, because the property it
/// failed to set was never the property under test.
/// </para>
/// <para>
/// <c>PipeWireGraphClock.RateNum</c>, <c>RateDiff</c> and <c>NextTimeNs</c> were referenced nowhere at
/// all, and they are the clock's drift fields - the numbers a consumer reads to discover that its
/// idea of time is diverging from the graph's.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GraphSurfaceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<PipeWireNode> WaitForNodeAsync(
        PipeWireRegistry reg, string name, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            PipeWireNode? n = reg.Current.Nodes.FirstOrDefault(
                x => string.Equals(x.NodeName, name, StringComparison.Ordinal));

            if (n is not null) return n;
            await Task.Delay(50, ct);
        }

        throw new InvalidOperationException($"node {name} never appeared");
    }

    /// <summary>
    /// The object's properties once binding it has merged its full info into the snapshot.
    /// </summary>
    /// <remarks>
    /// The registry event carries only the keys upstream whitelists as global - seventeen for a node
    /// (<c>impl-node.c</c> <c>global_keys</c>), eight for a link (<c>impl-link.c</c>) - so a key like
    /// <c>audio.position</c> or a caller's own marker is absent until the object is bound and its info
    /// arrives. Waiting on the key that proves the info landed keeps that race out of the assertions.
    /// </remarks>
    private static async Task<PipeWireProperties> WaitForBoundPropertiesAsync(
        Func<PipeWireProperties?> read, string key, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            if (read() is { } props && props.GetValueOrDefault(key) is not null) return props;
            await Task.Delay(50, ct);
        }

        throw new InvalidOperationException($"binding never delivered '{key}'");
    }

    /// <summary>
    /// Every node-creation builder call reaches the node the daemon ends up holding.
    /// </summary>
    /// <remarks>
    /// Asserted against the created node's own properties rather than against whether creation
    /// succeeded. That is the difference this test exists for: creation succeeds whether or not the
    /// builder applied anything.
    /// </remarks>
    [TestMethod]
    public async Task EveryNodeCreationBuilder_ReachesTheCreatedNode()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string nodeName = $"pwnet-builder-{Environment.ProcessId}";
        const string description = "a described node";

        await using var ctx = new PipeWireContext("pwnet-builder", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode created = await reg
            .CreateVirtualSink(nodeName, "Audio/Sink")
            .WithName(nodeName)
            .WithMediaClass("Audio/Sink")
            .WithChannelPositions("[ FL FR ]")
            .WithAutoConnect(false)
            .WithProperty(PipeWireKeys.PW_KEY_NODE_DESCRIPTION, description)
            .ExecuteAsync(cts.Token);

        Assert.IsNotNull(created, "the node was never created");

        // Read from the graph rather than from the returned object, so this is the daemon's view.
        PipeWireNode node = await WaitForNodeAsync(reg, nodeName, cts.Token);

        Assert.AreEqual(nodeName, node.NodeName, "WithName did not reach the node");
        Assert.AreEqual("Audio/Sink", node.MediaClass, "WithMediaClass did not reach the node");

        // Bound for its info: audio.position and node.autoconnect are not global keys, so the
        // registry event never carries them and only the node's own info reports what it holds.
        await using PipeWireNodeProxy control = reg.BindNode(node.NodeId);
        PipeWireProperties props = await WaitForBoundPropertiesAsync(
            () => reg.Current.Nodes.FirstOrDefault(n => n.NodeId == node.NodeId)?.Properties,
            PipeWireKeys.SPA_KEY_AUDIO_POSITION,
            cts.Token);

        Assert.AreEqual(
            description,
            props.GetValueOrDefault(PipeWireKeys.PW_KEY_NODE_DESCRIPTION),
            "WithProperty did not reach the node");

        Assert.AreEqual(
            "[ FL FR ]",
            props.GetValueOrDefault(PipeWireKeys.SPA_KEY_AUDIO_POSITION),
            "WithChannelPositions did not reach the node");

        Assert.AreEqual(
            "false",
            props.GetValueOrDefault(PipeWireKeys.PW_KEY_NODE_AUTOCONNECT)?.ToLowerInvariant(),
            "WithAutoConnect did not reach the node");
    }

    /// <summary>
    /// The link-creation builders reach the created link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Passive()</c> is the one with teeth: a passive link does not keep its nodes running, so a
    /// builder that dropped it produces a graph that never suspends and quietly holds hardware open.
    /// </para>
    /// <para>
    /// Whether it lands is the daemon's policy, not the builder's: <c>module-link-factory</c> removes
    /// <c>link.passive</c> from a request unless it was loaded with <c>allow.link.passive = true</c>
    /// (module-link-factory.c, "if (!d->allow_passive) pw_properties_set(properties,
    /// PW_KEY_LINK_PASSIVE, NULL)"), and the default is false. So the factory's own arguments are read
    /// and the expectation follows them: carried through when allowed, absent when not.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task TheLinkCreationBuilders_ReachTheCreatedLink()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-linkbuilder-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-linkbuilder", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // A producer and a sink of our own, so the link is between things this test controls.
        await using var output = new PipeWireAudioOutput(
            ctx, $"{name}-src", 48000, 2, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };

        output.Connect(autoConnect: false);
        await WaitForNodeAsync(reg, $"{name}-src", cts.Token);

        PipeWireNode sink = await reg
            .CreateVirtualSink($"{name}-sink", "Audio/Sink")
            .WithName($"{name}-sink")
            .ExecuteAsync(cts.Token);

        PipeWireNode sinkNode = await WaitForNodeAsync(reg, $"{name}-sink", cts.Token);
        PipeWireNode srcNode = await WaitForNodeAsync(reg, $"{name}-src", cts.Token);

        PipeWirePort? outPort = null;
        PipeWirePort? inPort = null;
        for (var i = 0; i < 100 && (outPort is null || inPort is null); i++)
        {
            PipeWireGraphSnapshot g = reg.Current;
            outPort ??= g.Ports.FirstOrDefault(
                p => p.NodeId == srcNode.NodeId && p.PortDirection == PipeWirePortDirection.Out);
            inPort ??= g.Ports.FirstOrDefault(
                p => p.NodeId == sinkNode.NodeId && p.PortDirection == PipeWirePortDirection.In);

            if (outPort is null || inPort is null) await Task.Delay(50, cts.Token);
        }

        if (outPort is null || inPort is null)
            Assert.Inconclusive("the two nodes never both exposed a port to link.");

        PipeWireLink link = await reg
            .CreateLink(outPort!, inPort!)
            .Passive()
            .WithProperty("pwnet.test.marker", name)
            .ExecuteAsync(cts.Token);

        Assert.IsNotNull(link, "the link was never created");

        // Neither the marker nor link.passive is a global key - a link's registry event carries only
        // its endpoints and origin - so the link is found by id and bound for its full properties.
        await using PipeWireLinkProxy control = reg.BindLink(link.LinkId);
        PipeWireProperties made = await WaitForBoundPropertiesAsync(
            () => reg.Current.Links.FirstOrDefault(l => l.LinkId == link.LinkId)?.Properties,
            "pwnet.test.marker",
            cts.Token);

        Assert.AreEqual(name, made.GetValueOrDefault("pwnet.test.marker"), "WithProperty did not reach the created link");

        PipeWireModule? factoryModule = reg.Current.Modules.FirstOrDefault(
            m => m.ModuleName == "libpipewire-module-link-factory");
        Assert.IsNotNull(factoryModule, "no link-factory module, so the link could not have been created");

        PipeWireModule details = await reg.ReadModuleDetailsAsync(factoryModule.Id, cts.Token);
        string args = details.Properties.GetValueOrDefault(PipeWireKeys.MODULE_ARGS) ?? string.Empty;
        bool passiveAllowed = System.Text.RegularExpressions.Regex.IsMatch(
            args, @"allow\.link\.passive\s*[=:]\s*true", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        string? passive = made.GetValueOrDefault(PipeWireKeys.PW_KEY_LINK_PASSIVE)?.ToLowerInvariant();
        if (passiveAllowed)
        {
            Assert.AreEqual("true", passive,
                "the link factory allows link.passive, but Passive() did not reach the created link");
        }
        else
        {
            Assert.IsNull(passive,
                $"the link factory does not allow link.passive (args '{args}'), so the daemon should have removed it; it reads '{passive}'");
        }
    }

    /// <summary>
    /// The graph clock's rate fields describe a real clock, and its drift stays bounded.
    /// </summary>
    /// <remarks>
    /// <c>RateNum</c>/<c>RateDen</c> are the graph's tick period as a fraction, and <c>RateDiff</c>
    /// is how far the driver's clock has drifted from the monotonic one. None had a test. A
    /// <c>RateDiff</c> read from the wrong offset would look like a wildly drifting clock and drive
    /// any resampler built on it straight into a correction it can never satisfy.
    /// </remarks>
    [TestMethod]
    public async Task TheGraphClock_DescribesARealRateAndABoundedDrift()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string nodeName = $"pwnet-clockrate-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-clockrate", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, 48000, 2, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");
        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        // A clock a running driver has published, not the first one readable. While a driver is
        // still coming up the daemon writes only clock.nsec (context.c, "if (n->info.state <
        // PW_NODE_STATE_RUNNING) ... clock.nsec = get_time_ns(...)") and leaves next_nsec from the
        // driver's previous run, so a snapshot taken then has next_nsec behind nsec by however long
        // the driver was suspended - upstream's behaviour, not a wrong read. A position that has
        // advanced between two reads is a driver that is running and writing the whole clock.
        PipeWireGraphClock? clock = null;
        ulong? firstPosition = null;
        for (var i = 0; i < 100 && clock is null; i++)
        {
            if (output.GraphClock is { RateDen: > 0 } read)
            {
                firstPosition ??= read.Position;
                if (read.Position > firstPosition) clock = read;
            }

            if (clock is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(clock, "the stream never saw a running driver's clock: the position never advanced");

        PipeWireGraphClock c = clock!.Value;

        Assert.AreNotEqual(0u, c.RateDen, "the clock's rate denominator is zero, so the period is undefined");
        Assert.AreNotEqual(0u, c.RateNum, "the clock's rate numerator is zero, so the period is undefined");

        // A graph tick is a fraction of a second, not seconds and not nanoseconds-as-an-integer.
        double period = (double)c.RateNum / c.RateDen;
        Assert.IsTrue(
            period is > 0 and < 1,
            $"the clock period reads {period}s, which is not a graph tick");

        // RateDiff is a ratio around 1. Zero means it was never written; far from 1 means it was
        // read from the wrong place.
        Assert.IsTrue(
            c.RateDiff is > 0.5 and < 2.0,
            $"the clock's rate difference reads {c.RateDiff}, which is not a drift ratio");

        // NextTimeNs is when the next cycle is due, so it is ahead of the current cycle's time.
        Assert.IsTrue(
            c.NextTimeNs >= c.TimeNs,
            $"the next cycle ({c.NextTimeNs}) is scheduled before the current one ({c.TimeNs})");
    }

    /// <summary>
    /// The screen-capture metadata is coherent on frames that are carrying real pixels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HasCursor</c>, <c>Cursor</c>, <c>Damage</c> and <c>MapOffset</c> had no test between them.
    /// The producer here writes a per-cycle pattern and the consumer verifies it, so the metadata is
    /// being read off frames known to be fully written rather than off whatever turned up - a frame
    /// that failed to arrive would otherwise make every metadata assertion vacuously true.
    /// </para>
    /// <para>
    /// A test source attaches no cursor, so the negative is what can be pinned here: a consumer that
    /// trusted a cursor a producer never sent would composite garbage over every frame. Producing
    /// cursor metadata needs a compositor portal, which a headless box does not have.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task TheCaptureMetadata_IsCoherentOnFramesCarryingRealPixels()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        const int width = 64, height = 32;

        await using var ctx = new PipeWireContext("pwnet-cursor", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        var produced = 0;

        await using var output = new PipeWireVideoOutput(
            ctx, "pwnet-cursor-src", width, height, PixelFormat.Bgra, 30);

        output.FillFrame += (_, pixels, stride, w, h, _) =>
        {
            byte tag = (byte)(Interlocked.Increment(ref produced) & 0xFF);
            for (var y = 0; y < h; y++) pixels.Slice(y * stride, w * 4).Fill(tag);
            return true;
        };

        output.Connect(autoConnect: false);

        uint? nodeId = await output.WaitForNodeIdAsync(cts.Token);

        var verified = 0;
        var failures = new List<string>();

        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-cursor-sink");
        capture.FrameReady += (_, f) =>
        {
            if (f.Pixels.IsEmpty) return;

            // Only inspect metadata on a frame whose pixels are confirmed intact.
            byte tag = f.Pixels[0];
            for (var y = 0; y < f.Height; y++)
            {
                foreach (byte b in f.Pixels.Slice(y * f.Stride, f.Width * 4))
                {
                    if (b != tag) return;
                }
            }

            if (!f.HasCursor && f.Cursor.Width != 0)
                failures.Add("a frame reporting no cursor still described one");

            foreach (VideoRegion r in f.Damage)
            {
                if (r.Width == 0 || r.Height == 0) failures.Add("a damage region is degenerate");
                if (r.X + r.Width > f.Width || r.Y + r.Height > f.Height)
                    failures.Add("a damage region falls outside the frame");
            }

            Interlocked.Increment(ref verified);
        };

        capture.Connect(nodeId!.Value, [PixelFormat.Bgra]);
        await capture.WaitForStreamingAsync(cts.Token);
        await Task.Delay(800, cts.Token);

        Assert.IsTrue(
            Volatile.Read(ref verified) > 5,
            $"only {Volatile.Read(ref verified)} intact frames arrived to inspect");

        Assert.AreEqual(0, failures.Count, string.Join("; ", failures.Distinct()));
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern unsafe void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern unsafe int munmap(void* addr, nuint length);

    /// <summary>
    /// Maps the frame's fd as upstream's pw_map_range_init would and compares it to the frame.
    /// </summary>
    /// <returns>Null when the mapped bytes are the frame's, otherwise what went wrong.</returns>
    internal static unsafe string? CompareMappedPixels(VideoFrame f)
    {
        const int ProtRead = 1, MapShared = 1;
        long pageSize = Environment.SystemPageSize;

        long pageStart = f.MapOffset - (f.MapOffset % pageSize);
        long within = f.MapOffset - pageStart;
        int length = f.Pixels.Length;
        var span = (nuint)(within + length);

        void* p = mmap(null, span, ProtRead, MapShared, (int)f.Fd, pageStart);
        if (p == (void*)-1)
            return $"mmap at the rounded offset {pageStart} failed (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})";

        try
        {
            var mapped = new ReadOnlySpan<byte>((byte*)p + within, length);
            return mapped.SequenceEqual(f.Pixels)
                ? null
                : $"mapping fd {f.Fd} at offset {f.MapOffset} did not yield the frame's pixels";
        }
        finally
        {
            _ = munmap(p, span);
        }
    }
}
