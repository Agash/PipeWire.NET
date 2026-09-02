using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The implicit conversions that let a call site name a key by its enum.
/// </summary>
/// <remarks>
/// Each one carries a numeric value onto the wire, and a wrong conversion is silent: the daemon
/// receives a property nobody asked for and simply ignores it, so the symptom is a setting that
/// does nothing rather than an error. They exist to remove casts, which also removes the place a
/// reader would notice the mistake.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaKeyTests
{
    [TestMethod]
    public void EveryKeyEnum_ConvertsToItsOwnNumericValue()
    {
        AssertKey(SpaFormat.VideoSize);
        AssertKey(SpaProp.Volume);
        AssertKey(SpaPropInfo.Id);
        AssertKey(SpaParamBuffers.Size);
        AssertKey(SpaParamMeta.Type);
        AssertKey(SpaParamRoute.Index);
        AssertKey(SpaParamProfile.Index);
        AssertKey(SpaParamPortConfig.Mode);
        AssertKey(SpaParamLatency.Direction);
        AssertKey(SpaParamProcessLatency.Quantum);
        AssertKey(SpaParamTag.Direction);
        AssertKey(SpaParamIo.Id);
        AssertKey(SpaProfiler.Info);

        static void AssertKey<TEnum>(TEnum value) where TEnum : unmanaged, Enum
        {
            SpaKey key = value switch
            {
                SpaFormat f => f,
                SpaProp p => p,
                SpaPropInfo p => p,
                SpaParamBuffers p => p,
                SpaParamMeta p => p,
                SpaParamRoute p => p,
                SpaParamProfile p => p,
                SpaParamPortConfig p => p,
                SpaParamLatency p => p,
                SpaParamProcessLatency p => p,
                SpaParamTag p => p,
                SpaParamIo p => p,
                SpaProfiler p => p,
                _ => throw new InvalidOperationException($"unmapped enum {typeof(TEnum).Name}"),
            };

            Assert.AreEqual(Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture), key.Value,
                $"{typeof(TEnum).Name}.{value} converted to the wrong key");
            Assert.AreEqual(value, key.As<TEnum>(), "As<T> must undo the conversion");
        }
    }

    [TestMethod]
    public void AKey_RoundTripsThroughItsRawValue()
    {
        SpaKey key = SpaKey.FromRaw(0x1234_5678);

        Assert.AreEqual(0x1234_5678u, key.Value);
        Assert.AreEqual(0x1234_5678u, (uint)key);
        Assert.AreEqual(key, (SpaKey)0x1234_5678u);
        Assert.AreEqual("305419896", key.ToString());
    }

    [TestMethod]
    public void AnIdValue_RoundTripsThroughItsRawValueAndEnum()
    {
        SpaIdValue id = SpaVideoFormat.Bgra;

        Assert.AreEqual((uint)SpaVideoFormat.Bgra, id.Value);
        Assert.AreEqual(SpaVideoFormat.Bgra, id.As<SpaVideoFormat>());
        Assert.AreEqual(id, SpaIdValue.FromRaw((uint)SpaVideoFormat.Bgra));
        Assert.AreEqual((uint)SpaVideoFormat.Bgra, (uint)id);
    }

    [TestMethod]
    public void EveryIdEnum_ConvertsToItsOwnNumericValue()
    {
        SpaIdValue media = SpaMediaType.Video;
        SpaIdValue subtype = SpaMediaSubtype.Raw;
        SpaIdValue video = SpaVideoFormat.Nv12;
        SpaIdValue audio = SpaAudioFormat.F32Le;
        SpaIdValue direction = SpaDirection.Input;

        Assert.AreEqual((uint)SpaMediaType.Video, media.Value);
        Assert.AreEqual((uint)SpaMediaSubtype.Raw, subtype.Value);
        Assert.AreEqual((uint)SpaVideoFormat.Nv12, video.Value);
        Assert.AreEqual((uint)SpaAudioFormat.F32Le, audio.Value);
        Assert.AreEqual((uint)SpaDirection.Input, direction.Value);
    }

    [TestMethod]
    public void KeysCompareByValue_SoTheyCanIndexALookup()
    {
        // They are the key type of the parser's property lookups; identity equality would make
        // every lookup miss.
        var lookup = new Dictionary<SpaKey, string> { [SpaProp.Volume] = "volume", [SpaProp.Mute] = "mute" };

        Assert.AreEqual("volume", lookup[SpaProp.Volume]);
        Assert.AreEqual("mute", lookup[SpaProp.Mute]);
        Assert.IsFalse(lookup.ContainsKey(SpaProp.ChannelVolumes));
    }
}
