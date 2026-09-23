using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// The wire side of DMA-BUF device-ID negotiation: the Capability param a stream advertises and the
/// PeerCapability param it is answered with.
/// </summary>
/// <remarks>
/// <para>
/// Upstream's protocol (<c>doc/dox/internals/dma-buf.dox</c>, <c>video-src-fixate.c</c>,
/// <c>video-play-fixate.c</c>): each end connects inactive with a bare format and a
/// <c>SPA_PARAM_Capability</c> ParamDict carrying <c>pipewire.device-id-negotiation = 1</c>; a producer
/// may add <c>pipewire.device-ids</c>, a JSON object whose <c>available-devices</c> lists hex-encoded
/// <c>dev_t</c>s. Each end then receives the other's as <c>SPA_PARAM_PeerCapability</c> - a PeerParam
/// object keyed by peer id - and, if both take part, offers one format per device with
/// <c>SPA_FORMAT_VIDEO_deviceId</c> before activating. A stream always receives a PeerCapability:
/// when the daemon sends none, pw_stream synthesises one on the first Latency param
/// (<c>stream.c, emit_dummy_peer_capability</c>), so waiting for it cannot stall.
/// </para>
/// <para>
/// Built and read with the managed pod model rather than the zero-allocation builder: this happens
/// once per link, on the loop thread, and the ParamDict's struct of strings is exactly what the
/// model describes.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class DeviceIdNegotiation
{
    private const string AvailableDevicesKey = "available-devices";

    private static string NegotiationKey => Encoding.UTF8.GetString(NativeConstants.PW_CAPABILITY_DEVICE_ID_NEGOTIATION);

    private static string DeviceIdsKey => Encoding.UTF8.GetString(NativeConstants.PW_CAPABILITY_DEVICE_IDS);

    /// <summary>The Capability param announcing negotiation, and the devices when a producer names them.</summary>
    /// <remarks><c>spa_param_dict_build_dict(b, SPA_PARAM_Capability, ...)</c>, field for field.</remarks>
    internal static byte[] CapabilityParam(ReadOnlySpan<DrmDevice> availableDevices)
    {
        var fields = ImmutableArray.CreateBuilder<SpaValue>();
        int items = availableDevices.IsEmpty ? 1 : 2;
        fields.Add(new SpaInt(items));
        fields.Add(new SpaString(NegotiationKey));
        fields.Add(new SpaString("1"));

        if (!availableDevices.IsEmpty)
        {
            fields.Add(new SpaString(DeviceIdsKey));
            fields.Add(new SpaString(DeviceIdsJson(availableDevices)));
        }

        var dict = new SpaObject(
            SpaType.ObjectParamDict,
            SpaParamType.Capability,
            [new SpaPodProperty(SpaParamDict.Info, SpaPodPropFlags.HintDict, new SpaStruct(fields.ToImmutable()))]);

        return SpaPod.ToBytes(dict);
    }

    /// <summary>Reads a PeerCapability param.</summary>
    /// <remarks>
    /// Any peer that takes part is enough, as in the upstream examples; the device lists of all the
    /// peers that name one are combined.
    /// </remarks>
    internal static unsafe PeerCapabilities Parse(spa_pod* param)
    {
        if (param is null) return default;

        uint size = ((uint*)param)[0];
        if (size > SpaFormatPod.MaxParamPodBytes) return default;

        if (!SpaPod.TryParse(new ReadOnlySpan<byte>(param, 8 + (int)size), out SpaValue? value)
            || value is not SpaObject { ObjectType: SpaType.ObjectPeerParam } peers)
        {
            return default;
        }

        bool negotiates = false;
        var devices = ImmutableArray.CreateBuilder<ulong>();

        foreach (SpaPodProperty peer in peers.Properties)
        {
            // Keyed by peer id; the value is that peer's Capability ParamDict, or None.
            if (peer.Value is not SpaObject { ObjectType: SpaType.ObjectParamDict } dict) continue;

            SpaPodProperty? info = dict.Properties.FirstOrDefault(p => p.Key == (SpaKey)SpaParamDict.Info);
            if (info?.Value is not SpaStruct { Fields: var f } || f.Length < 1 || f[0] is not SpaInt) continue;

            // spa_param_dict_info_parse: Int n, then n (String key, String value) pairs.
            for (int i = 1; i + 1 < f.Length; i += 2)
            {
                if (f[i] is not SpaString { Value: var key } || f[i + 1] is not SpaString { Value: var text }) continue;

                if (key == NegotiationKey
                    && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
                    && version >= 1)
                {
                    negotiates = true;
                }
                else if (key == DeviceIdsKey)
                {
                    ReadDeviceIds(text, devices);
                }
            }
        }

        return new PeerCapabilities(negotiates, devices.ToImmutable());
    }

    /// <summary>
    /// The EnumFormats a stream announces once it knows its peer's capabilities, laid out end to end
    /// for <c>pw_stream_update_params</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream's <c>build_formats</c> (video-play-fixate.c) and the producer's equivalent
    /// (video-src-fixate.c). A peer that negotiates gets one format per device it can work with, each
    /// carrying that device's id and modifiers. A peer that does not gets one format without a device,
    /// with the first offer's modifiers - upstream's "implicitly assumed device" - so a stream connected
    /// with device offers still streams to every current client.
    /// </para>
    /// <para>
    /// With <paramref name="hostMemoryFallback"/>, a last format with no modifiers: the consumer's
    /// <c>build_format(-1)</c>, the shape a producer that cannot do DMA-BUF agrees to.
    /// </para>
    /// </remarks>
    /// <param name="peer">What the peer said.</param>
    /// <param name="offers">This side's devices and their modifiers, in preference order; not empty.</param>
    /// <param name="format">The one pixel format the modifiers apply to.</param>
    /// <param name="width">The width to offer.</param>
    /// <param name="height">The height to offer.</param>
    /// <param name="frameRate">The frame rate to offer.</param>
    /// <param name="fixedSize">Whether the geometry is demanded or a preference.</param>
    /// <param name="hostMemoryFallback">Whether to end with a format without modifiers.</param>
    /// <param name="count">How many pods were written.</param>
    /// <param name="deviceFormats">How many of them name a device.</param>
    internal static byte[] WriteDeviceFormats(
        in PeerCapabilities peer,
        ReadOnlySpan<DmaBufDeviceOffer> offers,
        PixelFormat format, uint width, uint height, uint frameRate, bool fixedSize,
        bool hostMemoryFallback,
        out int count, out int deviceFormats)
    {
        int capacity = 1024;
        foreach (DmaBufDeviceOffer offer in offers) capacity += 1024 + ((offer.Modifiers.Length + 1) * 8);

        byte[] buffer = new byte[capacity];
        int used = 0;
        count = 0;
        deviceFormats = 0;
        ReadOnlySpan<PixelFormat> formats = [format];

        if (peer.NegotiatesDeviceIds)
        {
            foreach (DmaBufDeviceOffer offer in offers)
            {
                if (!peer.Accepts(offer.Device.Id)) continue;

                used += Align(SpaFormatPod.WriteVideoFormat(buffer.AsSpan(used), formats,
                    width, height, frameRate, fixedSize,
                    modifiers: offer.Modifiers.AsSpan(), deviceId: offer.Device.Id));
                count++;
                deviceFormats++;
            }
        }
        else
        {
            used += Align(SpaFormatPod.WriteVideoFormat(buffer.AsSpan(used), formats,
                width, height, frameRate, fixedSize, modifiers: offers[0].Modifiers.AsSpan()));
            count++;
        }

        if (hostMemoryFallback)
        {
            used += Align(SpaFormatPod.WriteVideoFormat(buffer.AsSpan(used), formats,
                width, height, frameRate, fixedSize));
            count++;
        }

        return buffer[..used];

        // SPA pods start on 8-byte boundaries; RequestParamsFromCallback walks them the same way.
        static int Align(int n) => (n + 7) & ~7;
    }

    /// <summary>
    /// Validates a set of device offers the way every public entry point needs them validated.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No offers, an offer without modifiers, or the same device offered twice.
    /// </exception>
    internal static void Validate(ReadOnlySpan<DmaBufDeviceOffer> offers, string paramName)
    {
        if (offers.IsEmpty) throw new ArgumentException("At least one device must be offered.", paramName);

        for (int i = 0; i < offers.Length; i++)
        {
            if (offers[i].Modifiers.IsDefaultOrEmpty)
                throw new ArgumentException($"The offer for {offers[i].Device} names no DRM modifiers.", paramName);

            for (int j = 0; j < i; j++)
            {
                if (offers[j].Device.Equals(offers[i].Device))
                    throw new ArgumentException($"{offers[i].Device} is offered twice.", paramName);
            }
        }
    }

    /// <summary>
    /// The negotiated device, described by the matching offer when there is one so its render node
    /// path comes with it.
    /// </summary>
    internal static DrmDevice? Resolve(ulong? negotiated, ReadOnlySpan<DmaBufDeviceOffer> offers)
    {
        if (negotiated is not { } id) return null;

        foreach (DmaBufDeviceOffer offer in offers)
        {
            if (offer.Device.Id == id) return offer.Device;
        }

        return new DrmDevice(id);
    }

    /// <summary><c>{"available-devices": ["&lt;hex dev_t&gt;", ...]}</c>, as video-src-fixate writes it.</summary>
    private static string DeviceIdsJson(ReadOnlySpan<DrmDevice> devices)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray(AvailableDevicesKey);
            foreach (DrmDevice device in devices) json.WriteStringValue(EncodeDevice(device.Id));
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads the device list; anything malformed is skipped, as upstream skips it.</summary>
    private static void ReadDeviceIds(string text, ImmutableArray<ulong>.Builder into)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
            bool inDevices = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    inDevices = reader.ValueTextEquals(AvailableDevicesKey);
                }
                else if (inDevices && reader.TokenType == JsonTokenType.String
                         && reader.GetString() is { } hex && DecodeDevice(hex) is { } id)
                {
                    into.Add(id);
                }
                else if (inDevices && reader.TokenType == JsonTokenType.EndArray)
                {
                    inDevices = false;
                }
            }
        }
        catch (JsonException)
        {
            // Deliberately not logged here: a peer whose list will not parse is treated as naming no
            // devices, which upstream's reader does too, and the negotiation result is logged by the
            // stream that asked.
        }
    }

    /// <summary>The bytes of a <c>dev_t</c> in host order, as hex - upstream's encode_hex over the value.</summary>
    internal static string EncodeDevice(ulong id) =>
        Convert.ToHexStringLower(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(ref id)));

    /// <summary>The inverse of <see cref="EncodeDevice"/>, or null for anything that is not eight bytes of hex.</summary>
    internal static ulong? DecodeDevice(string hex)
    {
        if (hex.Length != sizeof(ulong) * 2) return null;

        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            return MemoryMarshal.Read<ulong>(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
