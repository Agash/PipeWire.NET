using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Interop;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The wire half of DMA-BUF device-ID negotiation, checked against the shapes upstream builds: the
/// Capability ParamDict, the PeerCapability a stream is answered with, the device encoding, and the
/// format lists each end announces.
/// </summary>
/// <remarks>
/// No daemon. The pods are built the way upstream's <c>spa_param_dict_build_dict</c> and
/// <c>spa_peer_param_build_*</c> build them, so a mismatch here is a mismatch with every other
/// PipeWire client. The end-to-end half is <see cref="DeviceIdNegotiationEndToEndTests"/>.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed unsafe class DeviceIdNegotiationTests : PipeWireTestBase
{
    private static DrmDevice CardA => DrmDevice.FromNumbers(226, 128);
    private static DrmDevice CardB => DrmDevice.FromNumbers(226, 129);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("dev_t arithmetic is glibc's (gnu_dev_makedev).");
    }

    /// <summary>A PeerCapability the way the daemon hands it over: a PeerParam object keyed by peer id.</summary>
    private static byte[] PeerCapability(params (uint PeerId, byte[]? Capability)[] peers)
    {
        var props = ImmutableArray.CreateBuilder<SpaPodProperty>();
        foreach ((uint peerId, byte[]? capability) in peers)
        {
            SpaValue value = SpaNone.Instance;
            if (capability is not null)
            {
                Assert.IsTrue(SpaPod.TryParse(capability, out SpaValue? parsed));
                value = parsed!;
            }

            props.Add(new SpaPodProperty(peerId, 0, value));
        }

        return SpaPod.ToBytes(
            new SpaObject(SpaType.ObjectPeerParam, SpaParamType.PeerCapability, props.ToImmutable())
        );
    }

    private static PeerCapabilities Parse(byte[] pod)
    {
        fixed (byte* p = pod)
            return DeviceIdNegotiation.Parse((spa_pod*)p);
    }

    [TestMethod]
    public void ADevice_IsEncodedAsTheBytesOfItsDevT_InHostOrder()
    {
        RequireLinux();

        // makedev(226, 128) = 0xE280; upstream's encode_hex runs over the dev_t's bytes as they sit
        // in memory, so on a little-endian machine the low byte comes first.
        Assert.AreEqual(0xE280UL, CardA.Id);
        Assert.AreEqual("80e2000000000000", DeviceIdNegotiation.EncodeDevice(CardA.Id));
        Assert.AreEqual(CardA.Id, DeviceIdNegotiation.DecodeDevice("80e2000000000000"));

        Assert.IsNull(DeviceIdNegotiation.DecodeDevice("80e2"), "a short value is not a dev_t");
        Assert.IsNull(DeviceIdNegotiation.DecodeDevice("zz00000000000000"), "not hex");
    }

    [TestMethod]
    public void ADrmDevice_IsItsNumber_AndNotItsPath()
    {
        RequireLinux();

        Assert.AreEqual(226u, CardA.Major);
        Assert.AreEqual(128u, CardA.Minor);
        Assert.AreEqual(CardA, new DrmDevice(CardA.Id, "/dev/dri/renderD128"));
        Assert.AreEqual(
            CardA.GetHashCode(),
            new DrmDevice(CardA.Id, "/somewhere/else").GetHashCode()
        );
        Assert.AreNotEqual(CardA, CardB);
        Assert.AreEqual(
            "226:128 (/dev/dri/renderD128)",
            new DrmDevice(CardA.Id, "/dev/dri/renderD128").ToString()
        );
    }

    [TestMethod]
    public void TheRenderNodesOnThisMachine_ReadBackAsTheNodesTheyCameFrom()
    {
        RequireLinux();

        ImmutableArray<DrmDevice> nodes = DrmDevice.EnumerateRenderNodes();
        if (nodes.IsEmpty)
            Assert.Inconclusive("No render node on this machine.");

        foreach (DrmDevice node in nodes)
        {
            Assert.IsNotNull(node.RenderNodePath);
            Assert.AreEqual(226u, node.Major, $"{node} is not a DRM device number");
            Assert.AreEqual(node, DrmDevice.FromRenderNode(node.RenderNodePath));
        }

        Assert.ThrowsExactly<ArgumentException>(() => DrmDevice.FromRenderNode("/dev/null"));
    }

    [TestMethod]
    public void AProducersCapability_ReadsBackAsNegotiatingWithItsDevices()
    {
        RequireLinux();

        byte[] capability = DeviceIdNegotiation.CapabilityParam([CardA, CardB]);

        // What spa_param_dict_build_dict writes: a ParamDict object with one HINT_DICT property holding
        // a struct of Int n followed by n key/value string pairs.
        Assert.IsTrue(SpaPod.TryParse(capability, out SpaValue? value));
        var dict = (SpaObject)value!;
        Assert.AreEqual(SpaType.ObjectParamDict, dict.ObjectType);
        Assert.AreEqual(SpaParamType.Capability, dict.ObjectId);
        SpaPodProperty info = dict.Properties.Single();
        Assert.AreEqual((SpaKey)SpaParamDict.Info, info.Key);
        Assert.AreEqual(SpaPodPropFlags.HintDict, info.Flags);
        var fields = ((SpaStruct)info.Value).Fields;
        Assert.AreEqual(new SpaInt(2), fields[0]);
        Assert.AreEqual(new SpaString("pipewire.device-id-negotiation"), fields[1]);
        Assert.AreEqual(new SpaString("1"), fields[2]);
        Assert.AreEqual(new SpaString("pipewire.device-ids"), fields[3]);
        Assert.AreEqual(
            new SpaString("""{"available-devices":["80e2000000000000","81e2000000000000"]}"""),
            fields[4]
        );

        PeerCapabilities peer = Parse(PeerCapability((57, capability)));
        Assert.IsTrue(peer.NegotiatesDeviceIds);
        CollectionAssert.AreEqual(new[] { CardA.Id, CardB.Id }, peer.AvailableDevices.ToArray());
        Assert.IsTrue(peer.Accepts(CardB.Id));
        Assert.IsFalse(peer.Accepts(DrmDevice.FromNumbers(226, 130).Id));
    }

    [TestMethod]
    public void AConsumersCapability_NegotiatesAndNamesNoDevices_WhichAcceptsAny()
    {
        RequireLinux();

        PeerCapabilities peer = Parse(
            PeerCapability((57, DeviceIdNegotiation.CapabilityParam([])))
        );

        Assert.IsTrue(peer.NegotiatesDeviceIds);
        Assert.IsTrue(peer.AvailableDevices.IsEmpty);
        Assert.IsTrue(
            peer.Accepts(CardA.Id),
            "a peer that names no devices works with any, as video-play-fixate reads it"
        );
    }

    [TestMethod]
    public void TheDummyPeerCapability_SaysThePeerDoesNotNegotiate()
    {
        RequireLinux();

        // stream.c's emit_dummy_peer_capability: one property keyed SPA_ID_INVALID with a None value.
        PeerCapabilities peer = Parse(PeerCapability((uint.MaxValue, null)));

        Assert.IsFalse(peer.NegotiatesDeviceIds);
        Assert.IsTrue(peer.AvailableDevices.IsEmpty);
        Assert.AreEqual(default, Parse([]), "an empty buffer is not a capability");
    }

    [TestMethod]
    public void AMalformedDeviceList_IsReadAsNamingNone_NotAsAFailure()
    {
        RequireLinux();

        byte[] capability = SpaPod.ToBytes(
            new SpaObject(
                SpaType.ObjectParamDict,
                SpaParamType.Capability,
                [
                    new SpaPodProperty(
                        SpaParamDict.Info,
                        SpaPodPropFlags.HintDict,
                        new SpaStruct([
                            new SpaInt(2),
                            new SpaString("pipewire.device-id-negotiation"),
                            new SpaString("1"),
                            new SpaString("pipewire.device-ids"),
                            new SpaString("{\"available-devices\": [\"80e2\", 12, "),
                        ])
                    ),
                ]
            )
        );

        PeerCapabilities peer = Parse(PeerCapability((3, capability)));

        Assert.IsTrue(peer.NegotiatesDeviceIds);
        Assert.IsTrue(peer.AvailableDevices.IsEmpty);
    }

    [TestMethod]
    public void AFormatForADevice_CarriesItsIdMandatory_AndReadsBack()
    {
        RequireLinux();

        byte[] pod = new byte[1024];
        int len = SpaFormatPod.WriteVideoFormat(
            pod,
            [PixelFormat.Bgra],
            640,
            480,
            30,
            fixedSize: true,
            modifiers: [0, 0x0100000000000001],
            deviceId: CardB.Id
        );

        Assert.IsTrue(SpaPod.TryParse(pod.AsSpan(0, len), out SpaValue? value));
        SpaPodProperty device = ((SpaObject)value!).Properties.Single(p =>
            p.Key == (SpaKey)SpaFormat.VideoDeviceId
        );
        Assert.AreEqual(SpaPodPropFlags.Mandatory, device.Flags);
        CollectionAssert.AreEqual(
            BitConverter.GetBytes(CardB.Id),
            ((SpaBytes)device.Value).Value.ToArray()
        );

        SpaFormatPod.VideoFormatInfo info;
        fixed (byte* p = pod)
            info = SpaFormatPod.ParseVideoFormat((spa_pod*)p, default);
        Assert.AreEqual(CardB.Id, info.DeviceId);
        Assert.IsTrue(
            info.ModifierNeedsFixation,
            "the negotiation offer leaves the modifier to fixate"
        );

        // The fixation pass keeps the device: a fixation without it matches none of a negotiating
        // peer's formats, which all name one.
        len = SpaFormatPod.WriteVideoFormat(
            pod,
            [PixelFormat.Bgra],
            640,
            480,
            30,
            fixedSize: true,
            modifiers: [0],
            fixateModifier: true,
            deviceId: CardB.Id
        );
        fixed (byte* p = pod)
            info = SpaFormatPod.ParseVideoFormat((spa_pod*)p, default);
        Assert.AreEqual(CardB.Id, info.DeviceId);
        Assert.IsFalse(info.ModifierNeedsFixation);
        Assert.AreEqual(0UL, info.Modifier);
    }

    [TestMethod]
    public void ANegotiatedFormat_YieldsItsDevice_FromInsideTheChoiceTheFilterWrapsItIn()
    {
        RequireLinux();

        // The daemon's settled Format: spa_pod_filter_prop writes every property it intersected as a
        // Choice, and a single match stays a Choice(None) holding one value (filter.h 246-257). A
        // reader that only takes a bare Bytes pod reports the device as undefined - which is how the
        // first live run of the end-to-end tests streamed on the right device and reported none.
        byte[] pod = SpaPod.ToBytes(
            new SpaObject(
                SpaType.ObjectFormat,
                SpaParamType.Format,
                [
                    new SpaPodProperty(SpaFormat.MediaType, 0, new SpaId((uint)SpaMediaType.Video)),
                    new SpaPodProperty(
                        SpaFormat.MediaSubtype,
                        0,
                        new SpaId((uint)SpaMediaSubtype.Raw)
                    ),
                    new SpaPodProperty(
                        SpaFormat.VideoDeviceId,
                        SpaPodPropFlags.Mandatory,
                        new SpaChoice(
                            SpaChoiceType.None,
                            SpaType.Bytes,
                            [new SpaBytes([.. BitConverter.GetBytes(CardB.Id)])]
                        )
                    ),
                    new SpaPodProperty(
                        SpaFormat.VideoFormat,
                        0,
                        new SpaChoice(
                            SpaChoiceType.None,
                            SpaType.Id,
                            [new SpaId((uint)SpaVideoFormat.Bgra)]
                        )
                    ),
                ]
            )
        );

        SpaFormatPod.VideoFormatInfo info;
        fixed (byte* p = pod)
            info = SpaFormatPod.ParseVideoFormat((spa_pod*)p, default);

        Assert.AreEqual(CardB.Id, info.DeviceId);
        Assert.AreEqual(PixelFormat.Bgra, info.Format);
    }

    [TestMethod]
    public void AFormatWithoutADevice_ReadsBackAsDeviceUndefined_EvenAfterOneThatHadIt()
    {
        RequireLinux();

        byte[] pod = new byte[1024];
        SpaFormatPod.WriteVideoFormat(
            pod,
            [PixelFormat.Bgra],
            640,
            480,
            30,
            fixedSize: true,
            modifiers: [0]
        );

        var previous = new SpaFormatPod.VideoFormatInfo(
            PixelFormat.Bgra,
            640,
            480,
            VideoColorInfo.Unknown,
            DeviceId: CardA.Id
        );
        SpaFormatPod.VideoFormatInfo info;
        fixed (byte* p = pod)
            info = SpaFormatPod.ParseVideoFormat((spa_pod*)p, previous);

        Assert.IsNull(
            info.DeviceId,
            "a device from the last negotiation must not survive into one that named none"
        );
    }

    private static List<SpaFormatPod.VideoFormatInfo> ReadPods(byte[] pods, int count)
    {
        var read = new List<SpaFormatPod.VideoFormatInfo>();
        int at = 0;
        fixed (byte* start = pods)
        {
            for (int i = 0; i < count; i++)
            {
                var pod = (spa_pod*)(start + at);
                read.Add(SpaFormatPod.ParseVideoFormat(pod, default));
                at += (8 + (int)pod->size + 7) & ~7;
            }
        }

        Assert.AreEqual(
            pods.Length,
            at,
            "the pods must fill the buffer exactly, each 8-byte aligned"
        );
        return read;
    }

    [TestMethod]
    public void ANegotiatingPeer_IsOfferedOneFormatPerDeviceItAccepts_InOrder()
    {
        RequireLinux();

        DrmDevice cardC = DrmDevice.FromNumbers(226, 130);
        DmaBufDeviceOffer[] offers =
        [
            new(cardC, [0x0100000000000002]),
            new(CardA, [0]),
            new(CardB, [0x0100000000000001, 0]),
        ];

        // The producer named A and B only: C is filtered out, as video-play-fixate's has_device_id does.
        var peer = new PeerCapabilities(true, [CardA.Id, CardB.Id]);
        byte[] pods = DeviceIdNegotiation.WriteDeviceFormats(
            peer,
            offers,
            PixelFormat.Bgra,
            640,
            480,
            30,
            fixedSize: false,
            hostMemoryFallback: true,
            out int count,
            out int deviceFormats
        );

        Assert.AreEqual(3, count);
        Assert.AreEqual(2, deviceFormats);

        List<SpaFormatPod.VideoFormatInfo> read = ReadPods(pods, count);
        Assert.AreEqual(CardA.Id, read[0].DeviceId);
        Assert.AreEqual(0UL, read[0].Modifier);
        Assert.AreEqual(CardB.Id, read[1].DeviceId);
        Assert.AreEqual(
            0x0100000000000001UL,
            read[1].Modifier,
            "each device keeps its own modifiers, first preferred"
        );
        Assert.IsNull(read[2].DeviceId);
        Assert.AreEqual(
            DrmFormatModifier.Invalid,
            read[2].Modifier,
            "the fallback is host memory: no modifier at all"
        );
    }

    [TestMethod]
    public void APeerThatDoesNotNegotiate_IsOfferedTheFirstDevicesModifiersWithoutADevice()
    {
        RequireLinux();

        DmaBufDeviceOffer[] offers = [new(CardA, [0x0100000000000001]), new(CardB, [0])];

        byte[] pods = DeviceIdNegotiation.WriteDeviceFormats(
            default,
            offers,
            PixelFormat.Bgra,
            640,
            480,
            30,
            fixedSize: true,
            hostMemoryFallback: false,
            out int count,
            out int deviceFormats
        );

        Assert.AreEqual(1, count);
        Assert.AreEqual(0, deviceFormats);
        SpaFormatPod.VideoFormatInfo only = ReadPods(pods, count).Single();
        Assert.IsNull(
            only.DeviceId,
            "an old peer cannot match a mandatory property it has never heard of"
        );
        Assert.AreEqual(0x0100000000000001UL, only.Modifier);
    }

    [TestMethod]
    public void DeviceOffers_AreValidatedBeforeAnythingIsSent()
    {
        RequireLinux();

        Assert.ThrowsExactly<ArgumentException>(() => DeviceIdNegotiation.Validate([], "offers"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            DeviceIdNegotiation.Validate([new(CardA, [])], "offers")
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            DeviceIdNegotiation.Validate([new(CardA, default)], "offers")
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            DeviceIdNegotiation.Validate(
                [new(CardA, [0]), new(new DrmDevice(CardA.Id, "/dev/dri/renderD128"), [0])],
                "offers"
            )
        );

        DeviceIdNegotiation.Validate([new(CardA, [0]), new(CardB, [0])], "offers");
    }

    [TestMethod]
    public void TheNegotiatedDevice_IsDescribedByTheMatchingOffer()
    {
        RequireLinux();

        var named = new DrmDevice(CardB.Id, "/dev/dri/renderD129");
        DmaBufDeviceOffer[] offers = [new(CardA, [0]), new(named, [0])];

        Assert.IsNull(DeviceIdNegotiation.Resolve(null, offers));
        Assert.AreEqual(
            "/dev/dri/renderD129",
            DeviceIdNegotiation.Resolve(CardB.Id, offers)!.Value.RenderNodePath
        );

        DrmDevice? unknown = DeviceIdNegotiation.Resolve(
            DrmDevice.FromNumbers(226, 140).Id,
            offers
        );
        Assert.AreEqual(140u, unknown!.Value.Minor);
        Assert.IsNull(unknown.Value.RenderNodePath);
    }
}
