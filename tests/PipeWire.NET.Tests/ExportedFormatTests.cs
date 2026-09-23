using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// What an exported node accepts as its settled format, against the formats real peers set.
/// </summary>
/// <remarks>
/// The audio adapter wrapping an exported node negotiates the follower's format and sets it with
/// <c>port_set_param(Format)</c>; a refusal fails the node's start with -EINVAL and nothing ever
/// flows. These are the shapes that arrive there, taken from the adapter's own debug log.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class ExportedFormatTests
{
    private static SpaPodProperty Prop(SpaKey key, SpaValue value) => new(key, SpaPodPropFlags.None, value);

    /// <summary>The format audioadapter's configure_format sets on a mono F32 follower, as it logged it.</summary>
    private static byte[] AdapterFormat(params SpaPodProperty[] extra)
    {
        var props = new List<SpaPodProperty>
        {
            Prop(SpaFormat.MediaType, new SpaId((uint)SpaMediaType.Audio)),
            Prop(SpaFormat.MediaSubtype, new SpaId((uint)SpaMediaSubtype.Raw)),
            Prop(SpaFormat.AudioFormat, new SpaId((uint)SpaAudioFormat.F32Le)),
            Prop(SpaFormat.AudioRate, new SpaInt(48000)),
            Prop(SpaFormat.AudioChannels, new SpaInt(1)),
            Prop(SpaFormat.AudioPosition, new SpaArray(SpaType.Id, [new SpaId((uint)SpaAudioChannel.Mono)])),
        };
        props.AddRange(extra);
        return SpaPod.ToBytes(new SpaObject(SpaType.ObjectFormat, SpaParamType.Format, [.. props]));
    }

    [TestMethod]
    public void TheFormatTheAdapterSets_IsAccepted()
    {
        PipeWireExportedFormat? settled = PipeWireExportedFormat.FromPod(AdapterFormat());

        Assert.IsNotNull(settled, "the format the audio adapter sets on its follower was refused");
        Assert.AreEqual(48000, settled.Rate);
        Assert.AreEqual(1, settled.Channels);
        Assert.AreEqual(4, settled.BytesPerFrame);
    }

    [TestMethod]
    public void ValuesWrappedInANoneChoice_AreAccepted()
    {
        // The daemon sends settled formats with each value in a None choice, which spa_pod_parser
        // unwraps; a parser that did not would refuse every format the graph settles.
        byte[] pod = SpaPod.ToBytes(new SpaObject(SpaType.ObjectFormat, SpaParamType.Format,
        [
            Prop(SpaFormat.MediaType, new SpaChoice(SpaChoiceType.None, SpaType.Id, [new SpaId((uint)SpaMediaType.Audio)])),
            Prop(SpaFormat.MediaSubtype, new SpaChoice(SpaChoiceType.None, SpaType.Id, [new SpaId((uint)SpaMediaSubtype.Raw)])),
            Prop(SpaFormat.AudioFormat, new SpaChoice(SpaChoiceType.None, SpaType.Id, [new SpaId((uint)SpaAudioFormat.S16Le)])),
            Prop(SpaFormat.AudioRate, new SpaChoice(SpaChoiceType.None, SpaType.Int, [new SpaInt(44100)])),
            Prop(SpaFormat.AudioChannels, new SpaChoice(SpaChoiceType.None, SpaType.Int, [new SpaInt(2)])),
        ]));

        PipeWireExportedFormat? settled = PipeWireExportedFormat.FromPod(pod);

        Assert.IsNotNull(settled);
        Assert.AreEqual(4, settled.BytesPerFrame);
    }

    /// <summary>
    /// A format fixated the way upstream fixates one - every choice turned to None, every value kept -
    /// is read by its first values.
    /// </summary>
    /// <remarks>
    /// The shape that failed on the lab box: audioadapter intersects the follower's EnumFormat, runs
    /// <c>spa_pod_fixate</c> (which changes each choice's type and nothing else,
    /// spa/pod/iter.h), and sets the result. Upstream reads such a choice by clamping to the kind's
    /// count (<c>spa_pod_choice_body_get_values</c>); a parser demanding an exact count refused every
    /// format the adapter set, so the exported node's start failed with -EINVAL.
    /// </remarks>
    [TestMethod]
    public void AFormatFixatedTheWayUpstreamDoesIt_IsAccepted()
    {
        byte[] pod = SpaPod.ToBytes(new SpaObject(SpaType.ObjectFormat, SpaParamType.Format,
        [
            Prop(SpaFormat.MediaType, new SpaId((uint)SpaMediaType.Audio)),
            Prop(SpaFormat.MediaSubtype, new SpaId((uint)SpaMediaSubtype.Raw)),
            Prop(SpaFormat.AudioFormat, new SpaChoice(SpaChoiceType.Enum, SpaType.Id,
                [new SpaId((uint)SpaAudioFormat.F32Le), new SpaId((uint)SpaAudioFormat.F32Le), new SpaId((uint)SpaAudioFormat.S16Le)])),
            Prop(SpaFormat.AudioRate, new SpaChoice(SpaChoiceType.Range, SpaType.Int, [new SpaInt(48000), new SpaInt(1), new SpaInt(384000)])),
            Prop(SpaFormat.AudioChannels, new SpaChoice(SpaChoiceType.Range, SpaType.Int, [new SpaInt(1), new SpaInt(1), new SpaInt(64)])),
        ]));

        FixateInPlace(pod);

        PipeWireExportedFormat? settled = PipeWireExportedFormat.FromPod(pod);

        Assert.IsNotNull(settled, "a format fixated the way spa_pod_fixate leaves it was refused");
        Assert.AreEqual(48000, settled.Rate);
        Assert.AreEqual(1, settled.Channels);
        Assert.AreEqual(4, settled.BytesPerFrame);
    }

    /// <summary>
    /// <c>spa_pod_object_fixate</c>, byte for byte: each property whose value is a choice has the
    /// choice type set to None, and nothing else changes.
    /// </summary>
    private static void FixateInPlace(byte[] pod)
    {
        // Object: size, type, then the body: object type, object id, then properties.
        uint size = BitConverter.ToUInt32(pod, 0);
        int end = 8 + (int)size;
        int pos = 16;
        while (pos + 16 <= end)
        {
            // Property: key, flags, then the value pod: size, type, body.
            uint valueSize = BitConverter.ToUInt32(pod, pos + 8);
            uint valueType = BitConverter.ToUInt32(pod, pos + 12);
            if (valueType == (uint)SpaType.Choice)
                BitConverter.TryWriteBytes(pod.AsSpan(pos + 16, 4), (uint)SpaChoiceType.None);

            int valueLen = (int)(8 + valueSize);
            pos += 8 + ((valueLen + 7) & ~7);
        }
    }

    [TestMethod]
    public void AFormatThisNodeNeverOffered_IsRefused()
    {
        byte[] pod = SpaPod.ToBytes(new SpaObject(SpaType.ObjectFormat, SpaParamType.Format,
        [
            Prop(SpaFormat.MediaType, new SpaId((uint)SpaMediaType.Audio)),
            Prop(SpaFormat.MediaSubtype, new SpaId((uint)SpaMediaSubtype.Raw)),
            Prop(SpaFormat.AudioFormat, new SpaId((uint)SpaAudioFormat.F32P)),
            Prop(SpaFormat.AudioRate, new SpaInt(48000)),
            Prop(SpaFormat.AudioChannels, new SpaInt(1)),
        ]));

        Assert.IsNull(PipeWireExportedFormat.FromPod(pod), "a planar format this node never offered was accepted");
    }
}
