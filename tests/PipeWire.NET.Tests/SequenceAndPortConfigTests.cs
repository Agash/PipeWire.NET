using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The sequence writer and the port-configuration model.
/// </summary>
/// <remarks>
/// The writer is checked by parsing what it wrote: the reader is the thing the daemon's own output
/// is validated against, so agreeing with it is the only meaningful statement about the bytes.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SequenceAndPortConfigTests
{
    // ------------------------------------------------------------------ the sequence writer

    private static SpaValue Parse(ReadOnlySpan<byte> pod)
    {
        Assert.IsTrue(SpaPod.TryParse(pod, out SpaValue? value),
            "the writer produced bytes the reader refuses");

        return value!;
    }

    [TestMethod]
    public void AnEmptySequence_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[256];
        var builder = new SpaPodBuilder(buffer);
        builder.PushSequence(48000);

        var value = (SpaSequence)Parse(builder.GetPod());

        Assert.AreEqual(48000u, value.Unit);
        Assert.IsEmpty(value.Controls);
    }

    [TestMethod]
    public void ASequenceOfMidiControls_RoundTripsWithItsOffsetsAndBytes()
    {
        // Note on, note off. The bytes are opaque to the pod layer, which is the point: a control
        // carries a payload the sequence does not interpret.
        byte[] noteOn = [0x90, 0x40, 0x7f];
        byte[] noteOff = [0x80, 0x40, 0x00];

        Span<byte> buffer = stackalloc byte[256];
        var builder = new SpaPodBuilder(buffer);
        builder.PushSequence();
        builder.AddControl(0, SpaControlType.Ump, noteOn);
        builder.AddControl(480, SpaControlType.Ump, noteOff);

        var value = (SpaSequence)Parse(builder.GetPod());

        Assert.AreEqual(0u, value.Unit);
        Assert.HasCount(2, value.Controls);

        Assert.AreEqual(0u, value.Controls[0].Offset);
        Assert.AreEqual((uint)SpaControlType.Ump, value.Controls[0].Type);
        CollectionAssert.AreEqual(noteOn, ((SpaBytes)value.Controls[0].Value).Value.ToArray());

        Assert.AreEqual(480u, value.Controls[1].Offset);
        CollectionAssert.AreEqual(noteOff, ((SpaBytes)value.Controls[1].Value).Value.ToArray());
    }

    [TestMethod]
    public void AControlWhosePayloadIsNotAMultipleOfEight_IsStillReadBack()
    {
        // Every pod is padded to eight bytes, and the padding belongs between pods rather than to
        // the pod. A writer that counted it into the size makes the reader compute a wrong number of
        // following controls, which shows up as a refusal here rather than as a wrong value.
        Span<byte> buffer = stackalloc byte[256];
        foreach (int length in new[] { 1, 3, 7, 8, 9, 15, 16 })
        {
            byte[] payload = [.. Enumerable.Range(0, length).Select(static i => (byte)i)];

            var builder = new SpaPodBuilder(buffer);
            builder.PushSequence();
            builder.AddControl(0, SpaControlType.Midi, payload);
            builder.AddControl(1, SpaControlType.Midi, payload);

            var value = (SpaSequence)Parse(builder.GetPod());

            Assert.HasCount(2, value.Controls, $"a {length} byte payload lost a control");
            CollectionAssert.AreEqual(payload, ((SpaBytes)value.Controls[1].Value).Value.ToArray(),
                $"a {length} byte payload came back wrong");
        }
    }

    [TestMethod]
    public void ASequenceOfPropertyControls_CarriesWholeObjects()
    {
        // The other control shape: a Props object applied at an offset, which is how timed
        // automation rides the same sequence as MIDI.
        Span<byte> buffer = stackalloc byte[256];
        var builder = new SpaPodBuilder(buffer);
        builder.PushSequence();
        builder.AddControl(256, SpaControlType.Properties);
        builder.PushObject(SpaType.ObjectProps, SpaParamType.Props);
        builder.AddLong(SpaProp.LatencyOffsetNsec, 1234);
        builder.Pop();

        var value = (SpaSequence)Parse(builder.GetPod());

        Assert.HasCount(1, value.Controls);
        Assert.AreEqual(256u, value.Controls[0].Offset);

        var props = (SpaObject)value.Controls[0].Value;
        Assert.AreEqual(SpaType.ObjectProps, props.ObjectType);
        Assert.AreEqual(new SpaLong(1234), props[SpaProp.LatencyOffsetNsec]);
    }

    [TestMethod]
    public void PoppingWithNothingOpen_IsRefused()
    {
        Assert.ThrowsExactly<InvalidOperationException>(static () =>
        {
            Span<byte> buffer = stackalloc byte[64];
            var builder = new SpaPodBuilder(buffer);
            builder.Pop();
        });
    }

    // ------------------------------------------------------------------ port configuration

    [TestMethod]
    public void APortConfig_SurvivesTheRoundTrip()
    {
        var written = new PipeWirePortConfig(
            SpaDirection.Input, SpaParamPortConfigMode.Dsp, Monitor: true, Control: true);

        PipeWirePortConfig? read = PipeWirePortConfig.From(written.ToParameter());

        Assert.AreEqual(written, read);
    }

    [TestMethod]
    public void APortConfigWithoutAFormatFilter_DoesNotWriteAnEmptyOne()
    {
        // An empty Object property is not the same as an absent one: the adapter reads it as a
        // filter matching nothing, so a config with no filter must omit the property entirely.
        SpaObject param = new PipeWirePortConfig(
            SpaDirection.Output, SpaParamPortConfigMode.Convert).ToParameter();

        Assert.IsNull(param[(uint)SpaParamPortConfig.Format],
            "an absent format filter was written as an empty object");
    }

    [TestMethod]
    public void APortConfigCarryingAFormatFilter_KeepsIt()
    {
        var filter = new SpaObject(SpaType.ObjectFormat, SpaParamType.EnumFormat,
            [new SpaProperty((uint)SpaFormat.AudioChannels, 0, new SpaInt(2))]);

        var written = new PipeWirePortConfig(
            SpaDirection.Input, SpaParamPortConfigMode.Dsp, Format: filter);

        PipeWirePortConfig? read = PipeWirePortConfig.From(written.ToParameter());

        Assert.IsNotNull(read?.Format);
        Assert.AreEqual(new SpaInt(2), read!.Format![(uint)SpaFormat.AudioChannels]);
    }

    [TestMethod]
    public void AnObjectOfAnotherType_IsNotReadAsAPortConfig()
    {
        var props = new SpaObject(SpaType.ObjectProps, SpaParamType.Props, []);

        Assert.IsNull(PipeWirePortConfig.From(props));
        Assert.IsNull(PipeWirePortConfig.From(null));
    }

    // ------------------------------------------------------------------ explicit sync

    [TestMethod]
    public void TheUnscheduledReleaseFlag_IsReadOffTheTimeline()
    {
        Assert.IsTrue(new VideoSyncTimeline(VideoSyncTimeline.UnscheduledRelease, 1, 2)
            .ReleaseIsUnscheduled);
        Assert.IsFalse(new VideoSyncTimeline(0, 1, 2).ReleaseIsUnscheduled);
    }

    // ------------------------------------------------------------------ live session

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task APortConfigWrite_ReachesTheDaemon()
    {
        // The adapter owns port configuration, so this is attempted against a node that has
        // one to write: whether the daemon accepts or refuses, the write path itself is what
        // is under test, and both outcomes complete the round trip. The node is throwaway
        // because a successful write destroys and recreates its ports.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await using var ctx = new PipeWireContext("pwnet-portconfig-live", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await registry.CreateVirtualNode("PortConfigLive")
            .WithName($"pwnet_pconf_{Environment.ProcessId}_{Random.Shared.Next():x}")
            .ExecuteAsync(cts.Token);

        try
        {
            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            ImmutableArray<PipeWirePortConfig> configs =
                await control.EnumeratePortConfigsAsync(cts.Token);
            Console.Error.WriteLine($"adapter reports {configs.Length} port configs");

            var dsp = new PipeWirePortConfig(SpaDirection.Output, SpaParamPortConfigMode.Dsp);

            try
            {
                await control.SetPortConfigAsync(dsp, cts.Token);
                Console.Error.WriteLine("port config write accepted");
            }
            catch (PipeWireException ex)
            {
                Console.Error.WriteLine($"port config write refused: {ex.Message}");
            }
        }
        finally
        {
            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }
}
