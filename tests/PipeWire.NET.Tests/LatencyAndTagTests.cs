using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The typed views over the latency, process-latency and tag parameters.
/// </summary>
/// <remarks>
/// These read pods a daemon sends and build pods a daemon reads, so both directions are checked
/// here rather than only against a live session: a producer sends the units it knows and omits the
/// rest, and a reader that refuses a partial object rejects most real traffic.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class LatencyAndTagTests
{
    // ------------------------------------------------------------------ latency

    [TestMethod]
    public void AFullLatencyObject_ReadsEveryUnit()
    {
        var written = new PipeWireLatency(SpaDirection.Output, 0.5f, 1.5f, 24, 72, 500_000L, 1_500_000L);

        PipeWireLatency? read = PipeWireLatency.From(written.ToParameter());

        Assert.AreEqual(written, read, "a latency must survive being written and read back");
    }

    [TestMethod]
    public void ALatencyObjectCarryingOnlyNanoseconds_ReadsTheRestAsZero()
    {
        // What a producer that thinks in nanoseconds actually sends. Requiring all seven members
        // would reject it, and there is no meaningful value to substitute for a unit not sent.
        var partial = new SpaObject(SpaType.ObjectParamLatency, SpaParamType.Latency,
        [
            new SpaProperty((uint)SpaParamLatency.Direction, 0, new SpaId((uint)SpaDirection.Input)),
            new SpaProperty((uint)SpaParamLatency.MinNs, 0, new SpaLong(1_000L)),
            new SpaProperty((uint)SpaParamLatency.MaxNs, 0, new SpaLong(2_000L)),
        ]);

        PipeWireLatency? read = PipeWireLatency.From(partial);

        Assert.IsNotNull(read);
        Assert.AreEqual(SpaDirection.Input, read!.Direction);
        Assert.AreEqual(1_000L, read.MinNs);
        Assert.AreEqual(2_000L, read.MaxNs);
        Assert.AreEqual(0f, read.MinQuantum);
        Assert.AreEqual(0, read.MaxRate);
    }

    [TestMethod]
    public void AnObjectOfAnotherType_IsNotReadAsLatency()
    {
        // Props also carries floats and longs, so reading it as a latency would produce a
        // plausible-looking value out of unrelated properties rather than an obvious failure.
        var props = new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
            [new SpaProperty(SpaProp.Volume, 0, new SpaFloat(0.5f))]);

        Assert.IsNull(PipeWireLatency.From(props));
        Assert.IsNull(PipeWireLatency.From(null));
        Assert.IsNull(PipeWireProcessLatency.From(props));
        Assert.IsNull(PipeWireTag.From(props));
    }

    // ------------------------------------------------------------------ process latency

    [TestMethod]
    public void AProcessLatency_SurvivesTheRoundTrip()
    {
        var written = new PipeWireProcessLatency(Quantum: 1f, Rate: 128, Ns: 2_500_000L);

        Assert.AreEqual(written, PipeWireProcessLatency.From(written.ToParameter()));
    }

    [TestMethod]
    public void AProcessLatencyDefaultsToDeclaringNothing()
    {
        // The three units are alternatives, so a caller sets one and leaves the others alone. The
        // default has to be zero rather than absent: an omitted member reads as zero anyway, and a
        // node declaring nothing is a node that adds no delay.
        var none = new PipeWireProcessLatency();

        Assert.AreEqual(0f, none.Quantum);
        Assert.AreEqual(0, none.Rate);
        Assert.AreEqual(0L, none.Ns);
        Assert.AreEqual(none, PipeWireProcessLatency.From(none.ToParameter()));

        var justNs = new PipeWireProcessLatency(Ns: 5_000_000L);
        Assert.AreEqual(justNs, PipeWireProcessLatency.From(justNs.ToParameter()));
    }

    // ------------------------------------------------------------------ tags

    [TestMethod]
    public void ATag_SurvivesTheRoundTripWithItsOrderIntact()
    {
        // Order is preserved deliberately: the info is a Struct, not a dictionary, and a producer
        // writing the same key twice is expressing something a set would discard.
        var written = new PipeWireTag(SpaDirection.Output,
        [
            new KeyValuePair<string, string>("media.title", "Something"),
            new KeyValuePair<string, string>("media.artist", "Someone"),
            new KeyValuePair<string, string>("media.title", "Something Else"),
        ]);

        PipeWireTag? read = PipeWireTag.From(written.ToParameter());

        Assert.IsNotNull(read);
        Assert.AreEqual(SpaDirection.Output, read!.Direction);
        CollectionAssert.AreEqual(written.Info, read.Info);
    }

    [TestMethod]
    public void ATagWhoseCountDisagreesWithItsPairs_ReadsThePairsThatAreThere()
    {
        // The count is the producer's word for how many pairs follow. Trusting it over the pairs
        // actually present reads past the end of the struct or invents empty entries.
        var lying = new SpaObject(SpaType.ObjectParamTag, SpaParamType.Tag,
        [
            new SpaProperty((uint)SpaParamTag.Direction, 0, new SpaId((uint)SpaDirection.Input)),
            new SpaProperty((uint)SpaParamTag.Info, 0, new SpaStruct(
            [
                new SpaInt(99),
                new SpaString("k"),
                new SpaString("v"),
            ])),
        ]);

        PipeWireTag? read = PipeWireTag.From(lying);

        Assert.IsNotNull(read);
        Assert.HasCount(1, read!.Info);
        Assert.AreEqual("k", read.Info[0].Key);
        Assert.AreEqual("v", read.Info[0].Value);
    }

    [TestMethod]
    public void ATagWithATrailingKeyAndNoValue_DropsTheIncompletePair()
    {
        var truncated = new SpaObject(SpaType.ObjectParamTag, SpaParamType.Tag,
        [
            new SpaProperty((uint)SpaParamTag.Info, 0, new SpaStruct(
            [
                new SpaInt(2),
                new SpaString("k"),
                new SpaString("v"),
                new SpaString("orphan"),
            ])),
        ]);

        PipeWireTag? read = PipeWireTag.From(truncated);

        Assert.IsNotNull(read);
        Assert.HasCount(1, read!.Info);
    }

    [TestMethod]
    public void AnEmptyTag_IsReadAsEmptyRatherThanRefused()
    {
        var empty = new PipeWireTag(SpaDirection.Input, []);

        PipeWireTag? read = PipeWireTag.From(empty.ToParameter());

        Assert.IsNotNull(read);
        Assert.IsEmpty(read!.Info);
    }

    [TestMethod]
    public void ATagInfoWithNoLeadingCount_IsStillReadAsPairs()
    {
        // The struct's first field is the count in every pod the daemon sends, and reading pairs
        // from index 0 when it is absent costs one type check.
        var noCount = new SpaObject(SpaType.ObjectParamTag, SpaParamType.Tag,
        [
            new SpaProperty((uint)SpaParamTag.Info, 0, new SpaStruct(
                [new SpaString("k"), new SpaString("v")])),
        ]);

        PipeWireTag? read = PipeWireTag.From(noCount);

        Assert.IsNotNull(read);
        Assert.HasCount(1, read!.Info);
        Assert.AreEqual("k", read.Info[0].Key);
    }

    // ------------------------------------------------------------------ live session

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task ALiveAdapterNode_AnswersLatencyAndTagQueries()
    {
        // What an adapter node actually does with these parameters, which is less than its
        // parameter table suggests: process latency reads back nothing, tags are not listed at
        // all, and writes are the follower's business (a null sink refuses them). What is under
        // test is this library's half: reads surface as null, refusals as errors carrying the
        // daemon's code, and neither hangs the round-trip.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await using var ctx = new PipeWireContext("pwnet-latency-live", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await registry.CreateVirtualNode("LatencyLive")
            .WithName($"pwnet_lat_{Environment.ProcessId}_{Random.Shared.Next():x}")
            .ExecuteAsync(cts.Token);

        await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
        await control.ReadyAsync(cts.Token);

        Assert.IsNull(
            await control.GetProcessLatencyAsync(cts.Token),
            "the adapter reported a process latency it was never given");

        // Tags may or may not be announced yet: the adapter publishes its parameters in
        // stages after binding, so absence reads as a refusal when unannounced and as empty
        // when announced. Either is the adapter having no tags, and both must complete.
        ImmutableArray<PipeWireTag> tags;
        try
        {
            tags = await control.GetTagsAsync(cts.Token);
        }
        catch (PipeWireException ex) when (ex.Result == -2)
        {
            tags = [];
        }

        Console.Error.WriteLine($"tags: {tags.Length}");

        PipeWireException latencyRefused = await Assert.ThrowsExactlyAsync<PipeWireException>(
            () => control.SetProcessLatencyAsync(new PipeWireProcessLatency(Quantum: 128f), cts.Token));
        Assert.IsTrue(latencyRefused.Result < 0, "a refusal must carry the daemon's code");
        Console.Error.WriteLine($"process latency write refused: {latencyRefused.Message}");

        PipeWireException tagRefused = await Assert.ThrowsExactlyAsync<PipeWireException>(
            () => control.SetTagAsync(
                new PipeWireTag(SpaDirection.Output,
                    ImmutableArray.Create(new KeyValuePair<string, string>("pwnet", "live"))),
                cts.Token));
        Assert.IsTrue(tagRefused.Result < 0, "a refusal must carry the daemon's code");
        Console.Error.WriteLine($"tag write refused: {tagRefused.Message}");

        await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
    }
}
