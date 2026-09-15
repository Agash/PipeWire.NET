using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// The generated keys, the graph clock and the drive timer, against a live daemon and a tool that
/// knows nothing about this library.
/// </summary>
/// <remarks>
/// <para>
/// The key tests here close a loop the unit tests cannot. <c>GeneratedKeysTests</c> proves the two
/// generated forms agree with each other; it cannot prove either of them is the name PipeWire
/// actually uses. Writing a property under a generated constant and reading it back out of
/// <c>pw-dump</c> - a separate process, holding its own connection, that was never told what this
/// library calls anything - is what makes that a fact rather than an internal consistency check.
/// </para>
/// <para>
/// A whole-namespace rename in a header bump would sail past every unit test in this repo. It
/// would fail here.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GeneratedKeyRoundTripTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private const int Rate = 48000;
    private const int Channels = 2;

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>
    /// Properties this library writes under generated keys come back out of <c>pw-dump</c> under
    /// those exact names.
    /// </summary>
    /// <remarks>
    /// The values are deliberately unique per run, so a match cannot be some other node's property
    /// that happens to carry the same key.
    /// </remarks>
    [TestMethod]
    public async Task PropertiesWrittenUnderGeneratedKeys_AreReadBackByPwDumpUnderTheSameNames()
    {
        RequireLinux();
        PwTools.Require();

        using var cts = new CancellationTokenSource(Budget);

        string nodeName = $"pwnet-keyroundtrip-{Environment.ProcessId}";
        string description = $"description-{Guid.NewGuid():N}";
        string role = "Production";

        await using var ctx = new PipeWireContext("pwnet-keyroundtrip", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, Rate, Channels, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // Retag while running, which is the path that writes the keys through to the daemon.
        int changed = output.UpdateProperties(new Dictionary<string, string>
        {
            [PipeWireKeys.PW_KEY_NODE_DESCRIPTION] = description,
            [PipeWireKeys.PW_KEY_MEDIA_ROLE] = role,
        });

        Assert.IsTrue(changed > 0, "the daemon accepted no property change");

        PwDump.Entry? seen = null;
        for (var attempt = 0; attempt < 40 && seen is null; attempt++)
        {
            PwDump dump = await PwDump.CaptureAsync(cts.Token);
            seen = dump.OfKind("Node").FirstOrDefault(
                e => string.Equals(e.Prop(PipeWireKeys.PW_KEY_NODE_NAME), nodeName, StringComparison.Ordinal));

            if (seen is null || seen.Prop(PipeWireKeys.PW_KEY_NODE_DESCRIPTION) is null)
            {
                seen = null;
                await Task.Delay(100, cts.Token);
            }
        }

        Assert.IsNotNull(seen, $"pw-dump never reported a node named {nodeName}");

        // The assertion is on the key, not the value: pw-dump prints whatever the daemon holds, so
        // reading it under this name is what proves the name is PipeWire's.
        Assert.AreEqual(
            description,
            seen!.Prop(PipeWireKeys.PW_KEY_NODE_DESCRIPTION),
            $"'{PipeWireKeys.PW_KEY_NODE_DESCRIPTION}' is not the name the daemon stored it under");

        Assert.AreEqual(
            role,
            seen.Prop(PipeWireKeys.PW_KEY_MEDIA_ROLE),
            $"'{PipeWireKeys.PW_KEY_MEDIA_ROLE}' is not the name the daemon stored it under");

        Assert.AreEqual(
            nodeName,
            seen.Prop(PipeWireKeys.PW_KEY_NODE_NAME),
            $"'{PipeWireKeys.PW_KEY_NODE_NAME}' is not the name the daemon stored it under");
    }

    /// <summary>
    /// The interface type names the registry classifies on are the ones <c>pw-dump</c> reports.
    /// </summary>
    /// <remarks>
    /// These replaced a hand-written block that built each name by concatenating a base constant
    /// with a suffix. Getting one wrong does not throw: the object simply never matches, and it
    /// silently disappears from the graph this library reports.
    /// </remarks>
    [TestMethod]
    public async Task TheGeneratedInterfaceTypeNames_AreTheOnesTheDaemonReports()
    {
        RequireLinux();
        PwTools.Require();

        using var cts = new CancellationTokenSource(Budget);
        PwDump dump = await PwDump.CaptureAsync(cts.Token);

        // pw-dump prints the full "PipeWire:Interface:Node" in each entry's type field.
        var types = dump.Entries
            .Select(e => e.Type)
            .Where(t => t.StartsWith("PipeWire:Interface:", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsTrue(types.Count > 0, "pw-dump reported no interface types at all");

        // Only the ones a live session is certain to contain. A daemon with no security context or
        // profiler bound would make those absent for reasons that are not this library's fault.
        foreach (string expected in new[]
                 {
                     PipeWireKeys.PW_TYPE_INTERFACE_Core,
                     PipeWireKeys.PW_TYPE_INTERFACE_Node,
                     PipeWireKeys.PW_TYPE_INTERFACE_Port,
                     PipeWireKeys.PW_TYPE_INTERFACE_Client,
                     PipeWireKeys.PW_TYPE_INTERFACE_Module,
                     PipeWireKeys.PW_TYPE_INTERFACE_Factory,
                 })
        {
            Assert.IsTrue(
                types.Contains(expected),
                $"the daemon reports no object of type '{expected}'; the generated name does not "
                + $"match the wire. Present: {string.Join(", ", types.Order(StringComparer.Ordinal))}");
        }
    }

    /// <summary>
    /// The graph clock advances while a stream runs, and advances monotonically.
    /// </summary>
    /// <remarks>
    /// Everything else asserts the clock is <em>present</em>. A clock that is delivered once and
    /// then frozen satisfies all of that while being useless: the whole point of it is to be the
    /// time base other media is aligned against, and a constant reads as "no time has passed" to
    /// anything computing a drift from it.
    /// </remarks>
    [TestMethod]
    public async Task TheGraphClock_AdvancesMonotonicallyWhileStreaming()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-clockadvance", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        string nodeName = $"pwnet-clockadvance-{Environment.ProcessId}";

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, Rate, Channels, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");

        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        var readings = new List<(ulong TimeNs, ulong Position)>();
        for (var i = 0; i < 40 && readings.Count < 6; i++)
        {
            if (output.GraphClock is { } clock && clock.TimeNs > 0)
            {
                if (readings.Count == 0 || clock.TimeNs != readings[^1].TimeNs)
                    readings.Add((clock.TimeNs, clock.Position));
            }

            await Task.Delay(50, cts.Token);
        }

        Assert.IsTrue(
            readings.Count >= 6,
            $"the graph clock produced only {readings.Count} distinct readings in two seconds");

        for (var i = 1; i < readings.Count; i++)
        {
            Assert.IsTrue(
                readings[i].TimeNs > readings[i - 1].TimeNs,
                $"the clock went backwards at reading {i}: "
                + $"{readings[i - 1].TimeNs} then {readings[i].TimeNs}");

            Assert.IsTrue(
                readings[i].Position >= readings[i - 1].Position,
                $"the graph position went backwards at reading {i}");
        }

        // Sampled over roughly two seconds of wall clock, so the reported time has to have moved
        // by a real amount rather than by a tick or two of jitter.
        Assert.IsTrue(
            readings[^1].TimeNs - readings[0].TimeNs > 100_000_000,
            "the graph clock advanced by under 100ms while streaming for two seconds");
    }

    /// <summary>
    /// A driving output can be put on a timer, and the timer actually produces cycles.
    /// </summary>
    /// <remarks>
    /// <c>DriveAt</c> is what a producer with no hardware clock of its own uses to pace the graph -
    /// a screen capture feeding a stream has nothing else telling it when a frame is due. Whether
    /// the daemon grants the driver role depends on the session, so refusal is not a failure; what
    /// is pinned is that accepting it produces cycles rather than a silently idle stream.
    /// </remarks>
    [TestMethod]
    public async Task ADrivingOutput_ProducesCyclesOnItsTimer()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-drivetimer", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        string nodeName = $"pwnet-drivetimer-{Environment.ProcessId}";
        var cycles = 0;

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, Rate, Channels, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            Interlocked.Increment(ref cycles);
            samples.Clear();
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");

        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        if (!output.DriveAt(TimeSpan.FromMilliseconds(20)))
            Assert.Inconclusive("the daemon did not grant this stream the driver role.");

        Volatile.Write(ref cycles, 0);
        await Task.Delay(500, cts.Token);

        int seen = Volatile.Read(ref cycles);

        // 20ms over 500ms is about 25 cycles. Asserting a lower bound well under that keeps this
        // from failing on a loaded box while still catching a timer that never fires at all.
        Assert.IsTrue(seen > 4, $"the drive timer produced only {seen} cycles in 500ms");

        // And the stream survived being driven, rather than erroring out partway.
        Assert.IsNotNull(output.Queue, "the stream stopped answering after being put on a timer");
    }
}
