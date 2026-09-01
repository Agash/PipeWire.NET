using PipeWire.NET.Generated;

namespace PipeWire.NET.Media;

/// <summary>
/// Centralizes SPA format-pod construction and the managed-to-SPA format-enum mappings
/// shared by the video/audio capture and output stream wrappers.
/// </summary>
internal static class SpaFormat
{
    // - Param: request the SPA_META_Header so buffers carry PTS -

    /// <summary>
    /// Writes a ParamMeta object requesting <c>SPA_META_Header</c> of the given size, so the
    /// daemon allocates buffers with a header and the producer fills in the presentation
    /// timestamp. Without this, frames never carry a PTS.
    /// </summary>
    internal static unsafe int WriteHeaderMetaParam(Span<byte> buf)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectParamMeta, SpaParam.Meta);
        b.AddId(SpaParamMeta.Type, SpaMetaType.Header);
        b.AddInt(SpaParamMeta.Size, sizeof(spa_meta_header));
        return b.GetPod().Length;
    }

    // - Param: buffer requirements (declares accepted data types incl. DMA-BUF) -

    /// <summary>
    /// Writes a ParamBuffers object advertising the accepted buffer data types and count.
    /// Including <c>SPA_DATA_DmaBuf</c> in <paramref name="dataTypes"/> opts into zero-copy GPU
    /// buffers; the producer picks one of the offered types, falling back to host memory when it
    /// cannot provide DMA-BUF.
    /// </summary>
    /// <param name="buf">Destination for the pod.</param>
    /// <param name="size">
    /// Size of <em>one data block</em>, not of the whole image. SPA documents
    /// <c>SPA_PARAM_BUFFERS_size</c> as "size of a data block memory", so a planar format declaring
    /// three blocks describes its largest plane here, not the sum of them - see
    /// <see cref="VideoBlockSize"/>.
    /// </param>
    /// <param name="stride">Stride of a data block.</param>
    /// <param name="dataTypes">Mask of acceptable <c>spa_data_type</c> values.</param>
    /// <param name="blocks">Data blocks per buffer: one per plane.</param>
    /// <param name="sizeIsAnyOf">
    /// When true the size is offered as an open range rather than a fixed value, which is what a
    /// consumer wants: the producer owns the layout and we read whatever <c>chunk-&gt;size</c> says.
    /// Pinning a value made the daemon refuse allocation outright whenever the producer laid its
    /// planes out differently. PipeWire's own <c>gstpipewiresrc</c> offers a range for the same reason.
    /// </param>
    internal static int WriteVideoBuffersParam(
        Span<byte> buf, int size, int stride, int dataTypes, int blocks = 1, bool sizeIsAnyOf = false)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectParamBuffers, SpaParam.Buffers);
        b.AddChoiceRangeInt(SpaParamBuffers.Buffers, 8, 2, 16);
        // Blocks = number of planes (one spa_data block per plane). A packed format / host buffer is
        // a single block; planar needs one block per plane so each plane gets its own fd.
        b.AddInt(SpaParamBuffers.Blocks, blocks);

        if (sizeIsAnyOf)
            b.AddChoiceRangeInt(SpaParamBuffers.Size, size, 1, int.MaxValue);
        else
            b.AddInt(SpaParamBuffers.Size, size);

        b.AddInt(SpaParamBuffers.Stride, stride);
        b.AddChoiceFlagsInt(SpaParamBuffers.DataType, dataTypes);
        return b.GetPod().Length;
    }

    /// <summary>
    /// Size of the largest single data block for a format, which is what
    /// <c>SPA_PARAM_BUFFERS_size</c> describes when more than one block is declared.
    /// </summary>
    /// <remarks>
    /// The luma plane is the largest in every planar layout we support, so the block size is one
    /// full-height plane at the primary stride. Passing the whole-image size instead over-declares
    /// each block by roughly the chroma, which the daemon rejects.
    /// </remarks>
    internal static int VideoBlockSize(PixelFormat fmt, int width, int height) =>
        VideoPlaneCount(fmt) == 1
            ? VideoImageSize(fmt, width, height)
            : VideoStride(fmt, width) * height;

    /// <summary>Primary-plane stride (bytes per row) for the negotiated format.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The width is negative, or the row does not fit in <see cref="int"/> bytes.
    /// </exception>
    internal static int VideoStride(PixelFormat fmt, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);

        long stride = (long)width * BytesPerPixel(fmt);
        if (stride > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width),
                $"a {width}px {fmt} row needs {stride} bytes, which does not fit in an int.");

        return (int)stride;
    }

    /// <summary>Total buffer size in bytes for the negotiated format (incl. planar chroma planes).</summary>
    /// <remarks>
    /// <para>
    /// Chroma planes are <c>ceil(w/2) x ceil(h/2)</c>, not <c>w*h/4</c>. The difference only shows on
    /// odd dimensions, where the rounded-down form under-allocates and the producer writes past the
    /// buffer: 5x7 I420 needs 59 bytes and <c>w*h*3/2</c> yields 52.
    /// </para>
    /// <para>
    /// Width and height arrive from a negotiated param, so they are not trusted. The arithmetic runs
    /// in 64-bit and a frame that cannot be addressed is rejected rather than wrapped - at 32768
    /// square, packed 32bpp is exactly 2^32 bytes and truncates to a zero-sized buffer.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A dimension is negative, or the frame does not fit in <see cref="int"/> bytes.
    /// </exception>
    internal static int VideoImageSize(PixelFormat fmt, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        long w = width, h = height;
        long chroma = ((w + 1) / 2) * ((h + 1) / 2);

        long size = fmt switch
        {
            PixelFormat.Yuv420 => (w * h) + (2 * chroma),   // Y + U + V
            PixelFormat.Nv12   => (w * h) + (2 * chroma),   // Y + interleaved UV
            PixelFormat.Yuyv   => w * h * 2,
            _                  => w * h * 4,
        };

        if (size > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width),
                $"a {width}x{height} {fmt} frame needs {size} bytes, which does not fit in an int.");

        return (int)size;
    }

    /// <summary>Accepted capture data types: host memory + DMA-BUF (zero-copy GPU).</summary>
    internal static int VideoCaptureDataTypeMask =>
        (1 << (int)SpaType.DataMemPtr) | (1 << (int)SpaType.DataMemFd) | (1 << (int)SpaType.DataDmaBuf);

    /// <summary>
    /// Number of memory blocks a buffer carries for the format: one block per plane. gst's pipewiresink
    /// (and PipeWire's convention) splits a planar format into one <c>spa_data</c> per plane for both
    /// host memory and DMA-BUF, so a consumer must declare a block per plane (I420=3, NV12=2). Packed
    /// formats are a single block.
    /// </summary>
    internal static int VideoPlaneCount(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Yuv420 => 3,   // Y + U + V
        PixelFormat.Nv12   => 2,   // Y + interleaved UV
        _                  => 1,   // packed
    };

    // - Buffer metadata -

    /// <summary>Maps a raw <c>spa_data.type</c> to <see cref="PipeWireBufferType"/>.</summary>
    internal static PipeWireBufferType ToBufferType(uint spaDataType) => spaDataType switch
    {
        _ when spaDataType == SpaType.DataMemPtr => PipeWireBufferType.MemPtr,
        _ when spaDataType == SpaType.DataMemFd  => PipeWireBufferType.MemFd,
        _ when spaDataType == SpaType.DataDmaBuf => PipeWireBufferType.DmaBuf,
        _                                        => PipeWireBufferType.Unknown,
    };

    /// <summary>
    /// Finds the presentation timestamp (ns) from a buffer's SPA_META_Header, or -1 if absent.
    /// </summary>
    /// <remarks>
    /// Video producers populate this; PipeWire audio typically does NOT carry a per-buffer
    /// header PTS (audio timing is derived from the graph clock + sample position), so audio
    /// frames usually report -1 here.
    /// </remarks>
    internal static unsafe long FindPresentationTimeNs(spa_buffer* buf)
    {
        if (buf is null || buf->metas is null) return -1;
        uint headerType = (uint)spa_meta_type.SPA_META_Header;
        for (uint i = 0; i < buf->n_metas; i++)
        {
            spa_meta* m = &buf->metas[i];
            if (m->type == headerType && m->data is not null && m->size >= (uint)sizeof(spa_meta_header))
                return (long)((spa_meta_header*)m->data)->pts;
        }
        return -1;
    }

    // - Format-pod parsing (param_changed) -

    /// <summary>The negotiated video format extracted from a Format pod.</summary>
    /// <param name="Format">Negotiated pixel format.</param>
    /// <param name="Width">Frame width in pixels.</param>
    /// <param name="Height">Frame height in pixels.</param>
    /// <param name="Color">Negotiated color metadata.</param>
    /// <param name="Modifier">
    /// The negotiated DRM format modifier, or <see cref="DrmFormatModifier.Invalid"/> when none was
    /// negotiated (host-memory path). When <paramref name="ModifierNeedsFixation"/> is set this is the
    /// producer's <em>preferred</em> (first-returned) modifier rather than a final value.
    /// </param>
    /// <param name="ModifierNeedsFixation">
    /// True when the producer returned more than one modifier (it honoured <c>DONT_FIXATE</c>) and the
    /// negotiation must be fixated by re-offering a single modifier.
    /// </param>
    // We deliberately keep ONLY the preferred modifier + a "needs fixation" flag, never the full
    // returned set. The set is never needed: the consumer offers exactly the modifiers its GPU can
    // import, so whatever subset the producer returns is entirely importable and the first one is
    // always a safe choice to fixate on. Storing just a scalar keeps this a stack-friendly value type
    // (a record struct held in a field) with zero heap allocation per negotiation - materialising the
    // set into a long[] would allocate for no benefit and break the library's zero-copy discipline.
    internal readonly record struct VideoFormatInfo(
        PixelFormat Format, int Width, int Height, VideoColorInfo Color,
        ulong Modifier = DrmFormatModifier.Invalid,
        bool ModifierNeedsFixation = false);

    /// <summary>The negotiated audio format extracted from a Format pod.</summary>
    internal readonly record struct AudioFormatInfo(AudioSampleFormat Format, int SampleRate, int Channels);

    /// <summary>Parses a Format object pod into video format/size/color. Unset fields keep their incoming value.</summary>
    internal static unsafe VideoFormatInfo ParseVideoFormat(spa_pod* param, VideoFormatInfo current)
    {
        var (fmt, w, h, color) = (current.Format, current.Width, current.Height, current.Color);
        var (range, matrix, transfer, primaries) = (color.Range, color.Matrix, color.Transfer, color.Primaries);
        ulong modifier = current.Modifier;
        bool modifierNeedsFixation = current.ModifierNeedsFixation;

        uint size = ((uint*)param)[0];
        var pod = new ReadOnlySpan<byte>(param, 8 + (int)size);
        var reader = new SpaPodReader(pod);
        if (reader.EnterObject(out uint objType, out _, out _) && objType == SpaType.ObjectFormat)
        {
            while (reader.TryReadProperty(out uint key, out var value))
            {
                try
                {
                    if (key == SpaFormatVideo.Format)
                        fmt = FromSpaVideoFormat(ReadId(ref value));
                    else if (key == SpaFormatVideo.Modifier)
                    {
                        // The producer returns either a single fixated modifier or, when it honoured
                        // DONT_FIXATE, a choice of several it supports. We only need the preferred one
                        // (to use/fixate on) and whether more than one came back (must fixate) - both
                        // read in place with no allocation. See VideoFormatInfo for why the full set
                        // is intentionally discarded.
                        if (value.TryReadModifier(out long first, out int n) && n > 0)
                        {
                            modifier = (ulong)first;
                            modifierNeedsFixation = n > 1;
                        }
                    }
                    else if (key == SpaFormatVideo.Size)
                    {
                        var (rw, rh) = value.TryUnwrapChoice(out var i) ? i.ReadRectangle() : value.ReadRectangle();
                        w = (int)rw; h = (int)rh;
                    }
                    else if (key == SpaFormatVideo.ColorRange)
                        range = MapColorRange(ReadId(ref value));
                    else if (key == SpaFormatVideo.ColorMatrix)
                        matrix = MapColorMatrix(ReadId(ref value));
                    else if (key == SpaFormatVideo.TransferFunction)
                        transfer = MapTransfer(ReadId(ref value));
                    else if (key == SpaFormatVideo.ColorPrimaries)
                        primaries = MapPrimaries(ReadId(ref value));
                }
                catch (InvalidOperationException) { /* malformed property - skip */ }
            }
        }
        return new VideoFormatInfo(fmt, w, h, new VideoColorInfo(range, matrix, transfer, primaries),
            modifier, modifierNeedsFixation);

        static uint ReadId(ref SpaPodReader v) => v.TryUnwrapChoice(out var inner) ? inner.ReadId() : v.ReadId();
    }

    /// <summary>Parses a Format object pod into audio format/rate/channels.</summary>
    internal static unsafe AudioFormatInfo ParseAudioFormat(spa_pod* param, AudioFormatInfo current)
    {
        var (fmt, rate, ch) = (current.Format, current.SampleRate, current.Channels);

        uint size = ((uint*)param)[0];
        var pod = new ReadOnlySpan<byte>(param, 8 + (int)size);
        var reader = new SpaPodReader(pod);
        if (reader.EnterObject(out uint objType, out _, out _) && objType == SpaType.ObjectFormat)
        {
            while (reader.TryReadProperty(out uint key, out var value))
            {
                try
                {
                    if (key == SpaFormatAudio.Format)
                        fmt = FromSpaAudioFormat(value.TryUnwrapChoice(out var i) ? i.ReadId() : value.ReadId());
                    else if (key == SpaFormatAudio.Rate)
                        rate = value.TryUnwrapChoice(out var i) ? i.ReadInt() : value.ReadInt();
                    else if (key == SpaFormatAudio.Channels)
                        ch = value.TryUnwrapChoice(out var i) ? i.ReadInt() : value.ReadInt();
                }
                catch (InvalidOperationException) { /* malformed property - skip */ }
            }
        }
        return new AudioFormatInfo(fmt, rate, ch);
    }

    // - Video -

    /// <summary>
    /// Writes a video EnumFormat object. When <paramref name="formats"/> is empty a
    /// broad default set is offered; otherwise exactly the requested formats.
    /// </summary>
    /// <param name="buf">Destination buffer for the POD bytes.</param>
    /// <param name="formats">Pixel formats to offer (empty = broad default set).</param>
    /// <param name="defaultWidth">Default/preferred width.</param>
    /// <param name="defaultHeight">Default/preferred height.</param>
    /// <param name="defaultFrameRate">Default/preferred frame rate (per second).</param>
    /// <param name="fixedSize">True to offer a single fixed size/rate; false to offer a range.</param>
    /// <param name="modifiers">
    /// DRM format modifiers to offer for zero-copy dmabuf. When non-empty, a single
    /// <paramref name="formats"/> entry should be supplied (modifiers are per-format). The first
    /// modifier is preferred; the rest are alternatives.
    /// </param>
    /// <param name="fixateModifier">
    /// On the first pass pass <see langword="false"/>: the modifier choice is offered with
    /// <c>DONT_FIXATE</c> so the producer narrows it without collapsing. Once the consumer has
    /// chosen a single modifier its GPU supports, re-submit with <see langword="true"/> (no
    /// <c>DONT_FIXATE</c>) to fixate the negotiation.
    /// </param>
    internal static int WriteVideoFormat(
        Span<byte> buf,
        ReadOnlySpan<PixelFormat> formats,
        uint defaultWidth, uint defaultHeight, uint defaultFrameRate,
        bool fixedSize,
        ReadOnlySpan<long> modifiers = default,
        bool fixateModifier = false)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParam.EnumFormat);
        b.AddId(SpaFormatVideo.MediaType,    SpaMediaType.Video);
        b.AddId(SpaFormatVideo.MediaSubtype, SpaMediaSubtype.Raw);

        if (formats.IsEmpty)
        {
            b.AddChoiceEnum(SpaFormatVideo.Format,
                SpaVideoFormat.BGRA, SpaVideoFormat.RGBA,
                SpaVideoFormat.BGRx, SpaVideoFormat.RGBx,
                SpaVideoFormat.YUY2, SpaVideoFormat.I420);
        }
        else if (formats.Length == 1)
        {
            b.AddId(SpaFormatVideo.Format, ToSpaVideoFormat(formats[0]));
        }
        else
        {
            Span<uint> ids = stackalloc uint[formats.Length];
            for (int i = 0; i < formats.Length; i++)
                ids[i] = ToSpaVideoFormat(formats[i]);
            b.AddChoiceEnum(SpaFormatVideo.Format, ids);
        }

        // The modifier property must follow the format and precede size (PipeWire convention).
        // First pass (negotiation): a MANDATORY|DONT_FIXATE Choice Enum so the peer narrows the modifier set
        // without collapsing it. Fixation pass: a single MANDATORY plain Long (NOT a choice) - the modifier is
        // settled. This mirrors pipewire's video-src-fixate.c (build_format uses a choice; fixate_format uses a
        // plain long); a fixated modifier written as a 1-entry choice would read back as "still needs fixation".
        if (!modifiers.IsEmpty)
        {
            if (fixateModifier)
            {
                b.AddLong(SpaFormatVideo.Modifier, modifiers[0], SpaPodPropFlag.Mandatory);
            }
            else
            {
                b.AddChoiceEnumLong(SpaFormatVideo.Modifier, modifiers,
                    SpaPodPropFlag.Mandatory | SpaPodPropFlag.DontFixate);
            }
        }

        if (fixedSize)
        {
            b.AddRectangle(SpaFormatVideo.Size, defaultWidth, defaultHeight);
            b.AddFraction(SpaFormatVideo.Framerate, defaultFrameRate, 1);
        }
        else
        {
            b.AddChoiceRangeRectangle(SpaFormatVideo.Size,
                defaultWidth, defaultHeight, 1, 1, 8192, 8192);
            b.AddChoiceRangeFraction(SpaFormatVideo.Framerate,
                defaultFrameRate, 1, 0, 1, 1000, 1);
        }

        return b.GetPod().Length;
    }

    internal static uint ToSpaVideoFormat(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Rgba   => SpaVideoFormat.RGBA,
        PixelFormat.Bgra   => SpaVideoFormat.BGRA,
        PixelFormat.Rgbx   => SpaVideoFormat.RGBx,
        PixelFormat.Bgrx   => SpaVideoFormat.BGRx,
        PixelFormat.Yuyv   => SpaVideoFormat.YUY2,
        PixelFormat.Yuv420 => SpaVideoFormat.I420,
        PixelFormat.Nv12   => SpaVideoFormat.NV12,
        _                  => SpaVideoFormat.BGRA,
    };

    internal static PixelFormat FromSpaVideoFormat(uint spa) => spa switch
    {
        _ when spa == SpaVideoFormat.RGBA => PixelFormat.Rgba,
        _ when spa == SpaVideoFormat.BGRA => PixelFormat.Bgra,
        _ when spa == SpaVideoFormat.RGBx => PixelFormat.Rgbx,
        _ when spa == SpaVideoFormat.BGRx => PixelFormat.Bgrx,
        _ when spa == SpaVideoFormat.YUY2 => PixelFormat.Yuyv,
        _ when spa == SpaVideoFormat.I420 => PixelFormat.Yuv420,
        _ when spa == SpaVideoFormat.NV12 => PixelFormat.Nv12,
        _                                  => PixelFormat.Bgra,
    };

    /// <summary>
    /// Number of dmabuf planes (spa_data blocks) for a format: packed RGB and YUY2 are one plane;
    /// NV12 is two (Y, interleaved UV); I420 is three (Y, U, V). Planes may share one fd at different
    /// offsets, but each still occupies its own block so PipeWire allocates the right buffer shape.
    /// </summary>
    internal static int PlaneCount(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Nv12   => 2,
        PixelFormat.Yuv420 => 3,
        _                  => 1,
    };

    /// <summary>Bytes per pixel in the primary plane (planar formats report the Y-plane stride unit).</summary>
    internal static int BytesPerPixel(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Yuv420 => 1,
        PixelFormat.Nv12   => 1,
        PixelFormat.Yuyv   => 2,
        _                  => 4,
    };

    // - Audio -

    internal static int WriteAudioFormat(
        Span<byte> buf, AudioSampleFormat format, int sampleRate, int channels)
    {
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParam.EnumFormat);
        b.AddId(SpaFormatVideo.MediaType,    SpaMediaType.Audio);
        b.AddId(SpaFormatVideo.MediaSubtype, SpaMediaSubtype.Raw);
        b.AddId(SpaFormatAudio.Format,       ToSpaAudioFormat(format));
        b.AddInt(SpaFormatAudio.Rate,        sampleRate);
        b.AddInt(SpaFormatAudio.Channels,    channels);
        return b.GetPod().Length;
    }

    internal static uint ToSpaAudioFormat(AudioSampleFormat fmt) => fmt switch
    {
        AudioSampleFormat.U8    => SpaAudioFormat.U8,
        AudioSampleFormat.S16Le => SpaAudioFormat.S16Le,
        AudioSampleFormat.S24Le => SpaAudioFormat.S24Le,
        AudioSampleFormat.S32Le => SpaAudioFormat.S32Le,
        AudioSampleFormat.F32Le => SpaAudioFormat.F32Le,
        _                       => SpaAudioFormat.F32Le,
    };

    // - Color enum mapping -
    // SPA's enum numbering is not contiguous with our public enums, so map by the
    // generated enum values explicitly (a plain cast would be wrong).

    internal static VideoColorRange MapColorRange(uint spa) => spa switch
    {
        _ when spa == (uint)spa_video_color_range.SPA_VIDEO_COLOR_RANGE_0_255  => VideoColorRange.Full_0_255,
        _ when spa == (uint)spa_video_color_range.SPA_VIDEO_COLOR_RANGE_16_235 => VideoColorRange.Limited_16_235,
        _                                                                      => VideoColorRange.Unknown,
    };

    internal static VideoColorMatrix MapColorMatrix(uint spa) => spa switch
    {
        _ when spa == (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_RGB    => VideoColorMatrix.Rgb,
        _ when spa == (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT709  => VideoColorMatrix.Bt709,
        _ when spa == (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT601  => VideoColorMatrix.Bt601,
        _ when spa == (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT2020 => VideoColorMatrix.Bt2020,
        _                                                                        => VideoColorMatrix.Unknown,
    };

    internal static VideoTransferFunction MapTransfer(uint spa) => spa switch
    {
        _ when spa == (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_GAMMA22   => VideoTransferFunction.Gamma22,
        _ when spa == (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_BT709     => VideoTransferFunction.Bt709,
        _ when spa == (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_SRGB      => VideoTransferFunction.Srgb,
        _ when spa == (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_BT2020_12 => VideoTransferFunction.Bt2020_12,
        _                                                                            => VideoTransferFunction.Unknown,
    };

    internal static VideoColorPrimaries MapPrimaries(uint spa) => spa switch
    {
        _ when spa == (uint)spa_video_color_primaries.SPA_VIDEO_COLOR_PRIMARIES_BT709  => VideoColorPrimaries.Bt709,
        _ when spa == (uint)spa_video_color_primaries.SPA_VIDEO_COLOR_PRIMARIES_BT2020 => VideoColorPrimaries.Bt2020,
        _                                                                              => VideoColorPrimaries.Unknown,
    };

    internal static AudioSampleFormat FromSpaAudioFormat(uint spa) => spa switch
    {
        _ when spa == SpaAudioFormat.U8    => AudioSampleFormat.U8,
        _ when spa == SpaAudioFormat.S16Le => AudioSampleFormat.S16Le,
        _ when spa == SpaAudioFormat.S24Le => AudioSampleFormat.S24Le,
        _ when spa == SpaAudioFormat.S32Le => AudioSampleFormat.S32Le,
        _ when spa == SpaAudioFormat.F32Le => AudioSampleFormat.F32Le,
        _                                   => AudioSampleFormat.F32Le,
    };
}
