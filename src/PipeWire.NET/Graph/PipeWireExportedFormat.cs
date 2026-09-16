using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// The format an exported node offers, and the buffer shape it wants for it.
/// </summary>
/// <remarks>
/// A node that implements itself has to answer the graph's questions about what it carries, which a
/// stream does not: <c>pw_stream</c> builds these pods from its constructor arguments. This is the
/// same information, in the form <see cref="PipeWireNodeProvider"/> hands back when the graph
/// enumerates its port parameters.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireExportedFormat
{
    private PipeWireExportedFormat(int rate, int channels, SpaAudioFormat sampleFormat, int bytesPerSample)
    {
        Rate = rate;
        Channels = channels;
        SampleFormat = sampleFormat;
        BytesPerSample = bytesPerSample;
    }

    // The ranges EnumFormat advertises. A settled format outside them is one this node never offered,
    // so FromPod refuses it rather than trusting the peer's arithmetic.
    private const int MaxRate = 384_000;
    private const int MaxChannels = 64;

    /// <summary>Sample rate in frames per second.</summary>
    public int Rate { get; }

    /// <summary>Channel count.</summary>
    public int Channels { get; }

    /// <summary>The sample encoding.</summary>
    public SpaAudioFormat SampleFormat { get; }

    /// <summary>Bytes one sample of one channel occupies.</summary>
    public int BytesPerSample { get; }

    /// <summary>Bytes one frame occupies across all channels.</summary>
    public int BytesPerFrame => BytesPerSample * Channels;

    /// <summary>Interleaved 32-bit float audio, which is what the graph carries natively.</summary>
    /// <param name="rate">Sample rate in frames per second.</param>
    /// <param name="channels">Channel count.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate or channel count is not positive.</exception>
    public static PipeWireExportedFormat AudioF32(int rate = 48000, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        return new PipeWireExportedFormat(rate, channels, SpaAudioFormat.F32Le, 4);
    }

    /// <summary>Interleaved 16-bit signed audio, which is what upstream's export-source produces.</summary>
    /// <param name="rate">Sample rate in frames per second.</param>
    /// <param name="channels">Channel count.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate or channel count is not positive.</exception>
    public static PipeWireExportedFormat AudioS16(int rate = 44100, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        return new PipeWireExportedFormat(rate, channels, SpaAudioFormat.S16Le, 2);
    }

    /// <summary>
    /// Writes this format as a <c>SPA_TYPE_OBJECT_Format</c> pod.
    /// </summary>
    /// <param name="destination">Where to write it.</param>
    /// <param name="asEnum">
    /// <see langword="true"/> for <c>SPA_PARAM_EnumFormat</c> - what the node can do - and
    /// <see langword="false"/> for <c>SPA_PARAM_Format</c>, what it settled on. The two differ: an
    /// enumeration answers with choices so a peer has room to agree, a settled format is fixed.
    /// </param>
    /// <returns>Bytes written, or 0 when it did not fit.</returns>
    internal int WriteFormat(Span<byte> destination, bool asEnum)
    {
        var b = new SpaPodBuilder(destination);

        b.PushObject(SpaType.ObjectFormat, asEnum ? SpaParamType.EnumFormat : SpaParamType.Format);
        b.AddId(SpaFormat.MediaType, SpaMediaType.Audio);
        b.AddId(SpaFormat.MediaSubtype, SpaMediaSubtype.Raw);

        if (asEnum)
        {
            // An enumeration is what this node *can* carry, and upstream's export-source answers it
            // with choices - an enum of sample formats and ranges for rate and channels - not with
            // one fixed point. The difference decides whether a link forms at all: a fixed offer
            // only intersects a peer whose filter admits exactly that value, so a sink asking for
            // stereo, or for a rate this node did not happen to be constructed with, gets an empty
            // intersection and the graph reports "no more output formats" rather than a mismatch.
            //
            // The offered values lead each choice, so they stay the preferred answer, and whatever
            // the peer settles on comes back through port_set_param and is recorded there.
            b.AddChoiceEnum(SpaFormat.AudioFormat, SampleFormat, SpaAudioFormat.F32Le, SpaAudioFormat.S16Le);
            b.AddChoiceRangeInt(SpaFormat.AudioRate, Rate, 1, MaxRate);
            b.AddChoiceRangeInt(SpaFormat.AudioChannels, Channels, 1, MaxChannels);
        }
        else
        {
            b.AddId(SpaFormat.AudioFormat, SampleFormat);
            b.AddInt(SpaFormat.AudioRate, Rate);
            b.AddInt(SpaFormat.AudioChannels, Channels);
        }

        b.Pop();

        return b.GetPod().Length;
    }

    /// <summary>
    /// Reads a settled <c>SPA_PARAM_Format</c> pod back into a format, or null if it is not one this
    /// node can carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart to offering choices. Once this node advertises a range, the value the peer
    /// picks is the one that governs <see cref="BytesPerFrame"/>, and reporting the offered format
    /// instead makes every cycle size its span from a frame width the graph is not using.
    /// </para>
    /// <para>
    /// Parsing is not accepting. Upstream's export-source parses the format and then refuses anything
    /// it does not produce - a sample format other than the ones it offered, or a zero rate or channel
    /// count - with <c>-EINVAL</c>. This does the same, and also refuses a pod that is not audio/raw
    /// at all, and a rate or channel count outside the ranges <c>EnumFormat</c> advertised.
    /// </para>
    /// <para>
    /// A settled format arrives with each value wrapped in a choice of kind None - the daemon's own
    /// negotiation log prints them that way. Upstream reads it through <c>spa_pod_parser</c>, which
    /// takes the single value out of such a choice; reading the choice as the value would refuse every
    /// real format the graph ever settles on.
    /// </para>
    /// </remarks>
    internal static PipeWireExportedFormat? FromPod(ReadOnlySpan<byte> pod)
    {
        if (!SpaPod.TryParse(pod, out SpaValue? value) || value is not SpaObject format) return null;

        if (Fixed(format, SpaFormat.MediaType) is not SpaId mediaType
            || mediaType.Value != (uint)SpaMediaType.Audio) return null;
        if (Fixed(format, SpaFormat.MediaSubtype) is not SpaId subtype
            || subtype.Value != (uint)SpaMediaSubtype.Raw) return null;

        if (Fixed(format, SpaFormat.AudioFormat) is not SpaId sampleFormat) return null;
        if (Fixed(format, SpaFormat.AudioRate) is not SpaInt rate) return null;
        if (Fixed(format, SpaFormat.AudioChannels) is not SpaInt channels) return null;

        // Only what WriteFormat offers. Anything else is a format this node never said it could make.
        var settled = (SpaAudioFormat)sampleFormat.Value;
        int bytesPerSample = settled switch
        {
            SpaAudioFormat.F32Le => 4,
            SpaAudioFormat.S16Le => 2,
            _ => 0,
        };

        if (bytesPerSample == 0) return null;
        if (rate.Value is < 1 or > MaxRate || channels.Value is < 1 or > MaxChannels) return null;

        return new PipeWireExportedFormat(rate.Value, channels.Value, settled, bytesPerSample);

        static SpaValue? Fixed(SpaObject o, SpaKey key) => o.Find(key)?.Value switch
        {
            SpaChoice { Kind: SpaChoiceType.None } choice when !choice.Alternatives.IsDefaultOrEmpty
                => choice.Alternatives[0],
            var v => v,
        };
    }

    /// <summary>
    /// The metadata this port asks buffers to carry, as a <c>SPA_PARAM_Meta</c> pod: a header, which
    /// is where a producer puts the presentation timestamp.
    /// </summary>
    /// <remarks>What upstream's export-source and export-sink answer for <c>SPA_PARAM_Meta</c>.</remarks>
    internal static unsafe int WriteMetaHeader(Span<byte> destination)
    {
        var b = new SpaPodBuilder(destination);
        b.PushObject(SpaType.ObjectParamMeta, SpaParamType.Meta);
        b.AddId(SpaParamMeta.Type, SpaMetaType.Header);
        b.AddInt(SpaParamMeta.Size, sizeof(spa_meta_header));
        b.Pop();
        return b.GetPod().Length;
    }

    /// <summary>
    /// The io area this port needs, as a <c>SPA_PARAM_IO</c> pod: the buffers area, which is where
    /// the graph and the node exchange buffer ids each cycle.
    /// </summary>
    /// <remarks>What upstream's export examples answer for <c>SPA_PARAM_IO</c> index 0.</remarks>
    internal static unsafe int WriteIoBuffers(Span<byte> destination)
    {
        var b = new SpaPodBuilder(destination);
        b.PushObject(SpaType.ObjectParamIo, SpaParamType.Io);
        b.AddId(SpaParamIo.Id, SpaIoType.Buffers);
        b.AddInt(SpaParamIo.Size, sizeof(spa_io_buffers));
        b.Pop();
        return b.GetPod().Length;
    }

    /// <summary>
    /// Writes the buffer shape the peer should allocate, as a <c>SPA_PARAM_Buffers</c> pod.
    /// </summary>
    /// <param name="destination">Where to write it.</param>
    /// <returns>Bytes written, or 0 when it did not fit.</returns>
    /// <remarks>
    /// The stride is one frame, and the size is a quantum's worth of them. Getting this wrong is not
    /// a negotiation failure - the peer allocates what it is told, and a node that then writes more
    /// than it asked for runs off the end of a buffer the graph owns.
    /// </remarks>
    internal int WriteBuffers(Span<byte> destination)
    {
        const int quantum = 1024;

        var b = new SpaPodBuilder(destination);

        b.PushObject(SpaType.ObjectParamBuffers, SpaParamType.Buffers);
        b.AddChoiceRangeInt(SpaParamBuffers.Buffers, 2, 1, 32);
        b.AddInt(SpaParamBuffers.Blocks, 1);
        b.AddChoiceRangeInt(SpaParamBuffers.Size, quantum * BytesPerFrame, BytesPerFrame, int.MaxValue);
        b.AddInt(SpaParamBuffers.Stride, BytesPerFrame);
        b.Pop();

        return b.GetPod().Length;
    }
}
