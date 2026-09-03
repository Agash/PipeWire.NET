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
        // One line per conversion, because that is what a wrong one costs: an id the daemon reads
        // as a different member of a different enum, with no error anywhere.
        Check<SpaType>(SpaType.Object, SpaType.Object);
        Check<SpaParamType>(SpaParamType.Format, SpaParamType.Format);
        Check<SpaMediaType>(SpaMediaType.Video, SpaMediaType.Video);
        Check<SpaMediaSubtype>(SpaMediaSubtype.Raw, SpaMediaSubtype.Raw);
        Check<SpaVideoFormat>(SpaVideoFormat.Nv12, SpaVideoFormat.Nv12);
        Check<SpaAudioFormat>(SpaAudioFormat.F32Le, SpaAudioFormat.F32Le);
        Check<SpaAudioChannel>(SpaAudioChannel.Mono, SpaAudioChannel.Mono);
        Check<SpaMetaType>(SpaMetaType.Header, SpaMetaType.Header);
        Check<SpaDataType>(SpaDataType.DmaBuf, SpaDataType.DmaBuf);
        Check<SpaDirection>(SpaDirection.Input, SpaDirection.Input);
        Check<SpaVideoColorRange>(SpaVideoColorRange.Full, SpaVideoColorRange.Full);
        Check<SpaVideoColorMatrix>(SpaVideoColorMatrix.Bt709, SpaVideoColorMatrix.Bt709);
        Check<SpaVideoColorPrimaries>(SpaVideoColorPrimaries.Bt709, SpaVideoColorPrimaries.Bt709);
        Check<SpaVideoTransferFunction>(SpaVideoTransferFunction.Bt709, SpaVideoTransferFunction.Bt709);
        Check<SpaVideoInterlaceMode>(SpaVideoInterlaceMode.Progressive, SpaVideoInterlaceMode.Progressive);

        static void Check<TEnum>(SpaIdValue converted, TEnum original) where TEnum : unmanaged, Enum
        {
            uint expected = Convert.ToUInt32(original, System.Globalization.CultureInfo.InvariantCulture);

            Assert.AreEqual(expected, converted.Value, $"{typeof(TEnum).Name} converted to the wrong id");
            Assert.AreEqual(expected, (uint)converted);
            Assert.AreEqual(original, converted.As<TEnum>(), "As<T> must undo the conversion");
        }
    }

    [TestMethod]
    public void AnIdReadAsAnEnumOfTheWrongWidth_SaysSoRatherThanReinterpreting()
    {
        // The reinterpret underneath throws a NotSupportedException naming neither type, which is
        // not something a caller can act on.
        Assert.ThrowsExactly<ArgumentException>(() => SpaIdValue.FromRaw(1).As<ByteWide>());
        Assert.ThrowsExactly<ArgumentException>(() => SpaKey.FromRaw(1).As<ByteWide>());
    }

    private enum ByteWide : byte { Zero }

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
